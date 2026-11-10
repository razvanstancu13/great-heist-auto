using System.Collections.Generic;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>
    /// Generates the whole city from a seed. Layout: a 6 x 6 grid of 60 m blocks between seven roads each way, so 452 m x 452 m of
    /// playable streets (about 200 000 m2), with a perimeter road that closes the loop for traffic. Skyline towers ring the outside, behind the boundary hedge.
    /// Buildings are boxes with tiled facades; every object's tint is in the vertex colours; all static art is merged per chunk.
    /// </summary>
    public class CityBuilder
    {
        // ---- metrics ----
        const float RoadHalf = 6f, WalkW = 4f, Curb = 0.15f, Block = 52f;
        const float Corridor = 2 * (RoadHalf + WalkW);
        const float Pitch = Block + Corridor;
        const float LaneOffset = 1.7f, ParkOffset = 4.75f, Bound = 3f * Pitch + 11.5f;
        static readonly float[] Lines = { -3f * Pitch, -2f * Pitch, -Pitch, 0f, Pitch, 2f * Pitch, 3f * Pitch };
        const int N = 7;

        enum BlockType { Buildings, Towers, Park, Parking, Yard }
        static readonly string[] PlanMap =
        {
            "TBBBTT",
            "BPBYBB",
            "BBKBPB",
            "TYBPBK",
            "BBPBBY",
            "KBBTBT",
        };
        static BlockType Plan(int i, int j) => PlanMap[i][j] switch
        {
            'T' => BlockType.Towers, 'P' => BlockType.Park, 'K' => BlockType.Parking, 'Y' => BlockType.Yard, _ => BlockType.Buildings,
        };

        // ---- palettes (tinting everywhere) ----
        static readonly Color[] WallTints = { C(1f, 1f, 1f), C(.92f, .86f, .78f), C(.82f, .86f, .92f), C(.95f, .78f, .66f), C(.78f, .8f, .74f), C(.9f, .9f, .82f), C(.72f, .74f, .8f) };
        static readonly Color[] GlassTints = { C(1f, 1f, 1f), C(.8f, .92f, 1f), C(.75f, 1f, .92f), C(1f, .92f, .8f), C(.82f, .85f, 1f) };
        static readonly Color[] CarTints = { C(.5f, .09f, .08f), C(.1f, .17f, .38f), C(.05f, .05f, .06f), C(.72f, .72f, .74f), C(.28f, .3f, .33f), C(.09f, .24f, .18f), C(.62f, .52f, .22f), C(.88f, .88f, .88f), C(.34f, .16f, .11f) };
        static readonly Color[] HedgeTints = { C(1f, 1f, 1f), C(.86f, 1f, .78f), C(.78f, .92f, .82f), C(1f, 1f, .72f) };
        static readonly Color[] MetalTints = { C(.55f, .62f, .5f), C(.3f, .42f, .6f), C(.7f, .35f, .2f), C(.5f, .5f, .52f), C(.8f, .65f, .2f) };
        static readonly Color[] ConcreteTints = { C(.9f, .9f, .9f), C(.82f, .8f, .75f), C(.72f, .74f, .76f), C(.85f, .78f, .62f) };
        static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        // ---- state ----
        readonly System.Random rng;
        readonly Dictionary<(Material, int), MeshAccum> accums = new();
        readonly Transform root, colliders;
        readonly List<CoverPoint> cover = new();
        readonly List<Vector3> spawns = new(), patrol = new();
        readonly Dictionary<string, PropInfo> props = new();
        Material asphalt, sidewalk, concrete, grass, hedge, dirt, roof, storefront, marking;
        Material[] facadeWall; Material facadeGlass; Material nightGlow;
        Dictionary<string, GameObject> vehicles;
        readonly Dictionary<(string, int), Material> paints = new();
        readonly List<Vector3> lamps = new();
        readonly List<Vector3> crossings = new();
        Transform carRoot;
        int carCount;
        int boxCount, hedgeMeters, buildingCount;

        class PropInfo
        {
            public Mesh Mesh; public Material Mat; public Matrix4x4 Norm; public Vector3 Size;
        }

        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        int RI(int a, int b) => rng.Next(a, b);
        T Pick<T>(T[] arr) => arr[rng.Next(arr.Length)];
        Color Vary(Color c, float amount) { float k = 1f + R(-amount, amount); return new Color(c.r * k, c.g * k, c.b * k, 1f); }

        CityBuilder(Transform root, int seed)
        {
            this.root = root;
            rng = new System.Random(seed);
            colliders = new GameObject("Colliders").transform;
            colliders.SetParent(root, false);
        }

        public static void Build(Transform root, int seed, Dictionary<string, GameObject> vehiclePrefabs)
        {
            var b = new CityBuilder(root, seed);
            b.vehicles = vehiclePrefabs;
            b.carRoot = new GameObject("Cars").transform;
            b.carRoot.SetParent(root, false);
            b.LoadArt();
            b.BuildRoadsAndGround();
            b.BuildBlocks();
            b.BuildStreetFurniture();
            b.BuildBoundary();
            b.BuildSkyline();
            b.FlushMeshes();
            b.BakeNavMeshAndData();
            b.CheckStanding();
            Debug.Log($"[BUILD] trees: {b.treeCount}, {b.props["tree"].Mesh.triangles.Length / 3} triangles near, {b.props["tree_lod"].Mesh.triangles.Length / 3} far");
            Debug.Log($"[BUILD] city: {b.buildingCount} buildings, {b.carCount} cars, {b.lamps.Count} lamps, {b.boxCount} colliders, {b.hedgeMeters} m of hedges, {b.cover.Count} cover points");
        }

        // ---- art ----

        void LoadArt()
        {
            asphalt = Art.WorldMat("asphalt", 0.22f, 0.3f, 0, 0.8f);
            sidewalk = Art.WorldMat("sidewalk", 0.15f, 0.15f, 0, 1f);
            concrete = Art.WorldMat("concrete", 0.12f, 0.12f, 0, 1f);
            grass = Art.WorldMat("grass", 0.08f, 0.08f, 0, 1.2f);
            hedge = Art.WorldMat("hedge", 0.08f, 0.08f, 0, 1.6f);
            dirt = Art.WorldMat("dirt", 0.05f, 0.05f, 0, 1f);
            roof = Art.WorldMat("roof", 0.1f, 0.1f, 0, 1f);
            storefront = Art.WorldMat("storefront", 0.25f, 0.82f, 0, 0.6f);
            marking = Art.MarkingMat();
            sidewalk.SetColor("_Color", new Color(.66f, .66f, .65f));   // the paving texture is nearly white: it glared in the sun
            concrete.SetColor("_Color", new Color(.8f, .8f, .8f));
            facadeWall = new[]
            {
                Art.WorldMat("facade_brick", 0.15f, 0.78f, 0, 0.8f),
                Art.WorldMat("facade_concrete", 0.15f, 0.8f, 0, 0.8f),
                Art.WorldMat("facade_plaster", 0.15f, 0.78f, 0, 0.7f),
            };
            facadeGlass = Art.WorldMat("facade_glass", 0.65f, 0.92f, 0.25f, 0.4f);

            Art.SetWet(asphalt, 1f); Art.SetWet(sidewalk, 0.75f); Art.SetWet(concrete, 0.5f); Art.SetWet(grass, 0.25f); Art.SetWet(roof, 0.5f);
            Color windows = new(1.15f, 1f, .78f, 1f);
            Art.SetGlow(facadeWall[0], "facade_brick", windows, 0.3f);
            Art.SetGlow(facadeWall[1], "facade_concrete", windows, 0.3f);
            Art.SetGlow(facadeWall[2], "facade_plaster", windows, 0.3f);
            Art.SetGlow(facadeGlass, "facade_glass", new Color(.85f, .8f, .68f, 1f), 0.2f);
            Art.SetGlow(storefront, "storefront", new Color(1.1f, .95f, .72f, 1f), 0.92f);
            nightGlow = Art.Plain("m_night_glow", Shader.Find("GreatHeistAuto/NightGlow"));

            foreach (var n in new[] { "barrier", "dumpster", "trashcan", "bench", "crates", "streetlamp", "tree", "tree_lod" }) props[n] = LoadProp(n);
            props["tree"] = StraightenTree(props["tree"], "tree_straight");
            props["tree_lod"] = StraightenTree(props["tree_lod"], "tree_lod_straight");
            props["tree_lod"].Mat = props["tree"].Mat;     // the same picture
            // A few shades of the one tree, so that a street of them is not one colour.
            treeMats = new Material[TreeShades.Length];
            for (int i = 0; i < treeMats.Length; i++)
            {
                treeMats[i] = new Material(props["tree"].Mat) { name = "m_tree_" + i };
                treeMats[i].SetColor("_Color", TreeShades[i]);
                AssetDatabase.CreateAsset(treeMats[i], $"{Art.Gen}/City/m_tree_{i}.mat");
            }
        }

        PropInfo LoadProp(string name)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{Art.Root}/Props/{name}.fbx");
            var mf = fbx.GetComponentInChildren<MeshFilter>();
            var mesh = mf.sharedMesh;
            var verts = mesh.vertices;
            var toRoot = mf.transform.localToWorldMatrix;   // includes the FBX root's axis change
            var pts = verts.Select(v => toRoot.MultiplyPoint3x4(v)).ToArray();

            // Yaw that gives the smallest footprint box, then the long side along +Z.
            float bestYaw = 0f, bestArea = float.MaxValue;
            for (float y = 0; y < 90f; y += 2f)
            {
                var q = Quaternion.Euler(0, y, 0);
                float minX = 1e9f, maxX = -1e9f, minZ = 1e9f, maxZ = -1e9f;
                foreach (var p in pts) { var r = q * p; minX = Mathf.Min(minX, r.x); maxX = Mathf.Max(maxX, r.x); minZ = Mathf.Min(minZ, r.z); maxZ = Mathf.Max(maxZ, r.z); }
                float area = (maxX - minX) * (maxZ - minZ);
                if (area < bestArea) { bestArea = area; bestYaw = y; }
            }
            var rot = Quaternion.Euler(0, bestYaw, 0);
            var rp = pts.Select(p => rot * p).ToArray();
            if (rp.Max(p => p.x) - rp.Min(p => p.x) > rp.Max(p => p.z) - rp.Min(p => p.z))
            {
                rot = Quaternion.Euler(0, 90, 0) * rot;
                rp = pts.Select(p => rot * p).ToArray();
            }
            float minY = rp.Min(p => p.y), maxY = rp.Max(p => p.y);
            float h = maxY - minY;
            float wantH = PropHeight(name);
            float s = wantH / Mathf.Max(h, 0.001f);
            // Some concepts come out stubby: these props also get real-world footprint proportions.
            float sx = s, sz = s;
            float rawX = rp.Max(p => p.x) - rp.Min(p => p.x), rawZ = rp.Max(p => p.z) - rp.Min(p => p.z);
            if (name == "car") { sx = 1.85f / rawX; sz = 4.5f / rawZ; }
            if (name == "bench") { sx = 0.7f / rawX; sz = 1.7f / rawZ; }
            if (name == "barrier") { sx = 0.6f / rawX; sz = 3f / rawZ; }

            // Tall thin things stand on the centre of their base; box-like things on the centre of their box.
            Vector3 center;
            if (name is "tree" or "tree_lod" or "streetlamp")
            {
                var low = rp.Where(p => p.y < minY + h * 0.08f).ToArray();
                center = new Vector3(low.Average(p => p.x), 0, low.Average(p => p.z));
            }
            else center = new Vector3((rp.Min(p => p.x) + rp.Max(p => p.x)) * .5f, 0, (rp.Min(p => p.z) + rp.Max(p => p.z)) * .5f);
            if (name == "streetlamp")
            {
                // The lamp's arm always points along +Z.
                var top = rp.Where(p => p.y > minY + h * 0.75f).ToArray();
                if (top.Average(p => p.z) < center.z) { rot = Quaternion.Euler(0, 180, 0) * rot; rp = pts.Select(p => rot * p).ToArray(); center = new Vector3(-center.x, 0, -center.z); }
            }
            var norm = Matrix4x4.Scale(new Vector3(sx, s, sz)) * Matrix4x4.Translate(new Vector3(-center.x, -minY, -center.z)) * Matrix4x4.Rotate(rot) * toRoot;
            var size = new Vector3(rawX * sx, h * s, rawZ * sz);
            Debug.Log($"[BUILD] prop {name}: {size.x:F2} x {size.y:F2} x {size.z:F2}, {mesh.triangles.Length / 3} tris, scale {s:F3}");
            float metal = name is "car" or "dumpster" or "trashcan" or "streetlamp" ? 0.3f : 0f;
            float smooth = name == "car" ? 0.62f : name is "dumpster" or "trashcan" or "streetlamp" ? 0.35f : 0.12f;
            return new PropInfo { Mesh = mesh, Mat = Art.ModelMat("Props", name, false, smooth, metal), Norm = norm, Size = size };
        }

        Material[] treeMats;
        static readonly Color[] TreeShades = { C(1f, 1f, 1f), C(1.1f, 1.12f, .92f), C(.9f, 1f, .88f), C(1.16f, 1.05f, .8f) };

        /// <summary>
        /// Generated trees lean and bend. Each height is shifted so the tree's centre line runs straight up: the trunk comes out
        /// vertical and the crown sits on top of it, while the shapes at each height stay what they were.
        /// </summary>
        PropInfo StraightenTree(PropInfo info, string assetName)
        {
            var acc = new MeshAccum();
            acc.Add(info.Mesh, info.Norm, Color.white);
            float height = 0f;
            foreach (var p in acc.V) height = Mathf.Max(height, p.y);
            const int slices = 14;
            var line = new Vector3[slices];
            // Up the trunk: at each height, only what is close to the trunk found so far, so hanging foliage cannot pull the line away.
            float trunkTop = height * 0.36f;
            Vector3 c0 = Vector3.zero; int n0 = 0;
            foreach (var p in acc.V) if (p.y < height * 0.05f) { c0 += new Vector3(p.x, 0, p.z); n0++; }
            Vector3 at = n0 > 0 ? c0 / n0 : Vector3.zero;
            Vector3 crown = Vector3.zero; int nc = 0;
            foreach (var p in acc.V) if (p.y > height * 0.55f) { crown += new Vector3(p.x, 0, p.z); nc++; }
            crown = nc > 0 ? crown / nc : at;
            Vector3 top = at;
            for (int i = 0; i < slices; i++)
            {
                float y0 = i * height / slices, y1 = (i + 1) * height / slices;
                if (y1 <= trunkTop)
                {
                    Vector3 sum = Vector3.zero; int n = 0;
                    foreach (var p in acc.V)
                        if (p.y >= y0 && p.y < y1 && new Vector3(p.x - at.x, 0, p.z - at.z).magnitude < 0.55f) { sum += new Vector3(p.x, 0, p.z); n++; }
                    if (n >= 3) at = sum / n;
                    line[i] = at;
                    top = at;
                }
                else line[i] = Vector3.Lerp(top, crown, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(trunkTop, height * 0.62f, y0)));   // and the crown over it
            }
            float lean = 0f;
            for (int i = 0; i < acc.V.Count; i++)
            {
                Vector3 p = acc.V[i];
                float f = Mathf.Clamp(p.y / height * slices - .5f, 0f, slices - 1.001f);
                int a = Mathf.FloorToInt(f);
                Vector3 c = Vector3.Lerp(line[a], line[a + 1], f - a);
                lean = Mathf.Max(lean, c.magnitude);
                acc.V[i] = p - c;
            }
            CutOffshoot(acc, height, assetName);

            // Light flows across the surface, triangle to triangle: lit one by one, a tree of leaves is a heap of facets. The picture carries the detail.
            var sums = new Dictionary<Vector3Int, Vector3>();
            Vector3Int Spot(Vector3 p) => new(Mathf.RoundToInt(p.x * 2000f), Mathf.RoundToInt(p.y * 2000f), Mathf.RoundToInt(p.z * 2000f));
            for (int i = 0; i < acc.T.Count; i += 3)
            {
                Vector3 a = acc.V[acc.T[i]], b = acc.V[acc.T[i + 1]], d = acc.V[acc.T[i + 2]];
                Vector3 face = Vector3.Cross(b - a, d - a);    // its length weighs big faces more
                foreach (var p in new[] { a, b, d })
                {
                    var key = Spot(p);
                    sums[key] = (sums.TryGetValue(key, out var had) ? had : Vector3.zero) + face;
                }
            }
            for (int i = 0; i < acc.V.Count; i++)
                if (sums.TryGetValue(Spot(acc.V[i]), out var sum) && sum.sqrMagnitude > 0f) acc.N[i] = sum.normalized;

            var mesh = acc.Build(assetName);
            Art.EnsureFolder($"{Art.Gen}/City");
            AssetDatabase.DeleteAsset($"{Art.Gen}/City/{assetName}.asset");
            AssetDatabase.CreateAsset(mesh, $"{Art.Gen}/City/{assetName}.asset");
            info.Mesh = mesh;
            info.Norm = Matrix4x4.identity;
            Debug.Log($"[BUILD] tree straightened: its centre line wandered {lean:F2} m");
            return info;
        }

        /// <summary>
        /// The generated tree has one branch that carries a clump of leaves out past the crown. Whatever lies beyond the crown's radius in that direction goes,
        /// below the height of the crown's widest part: the mesh stays as generated everywhere else.
        /// </summary>
        static void CutOffshoot(MeshAccum acc, float height, string assetName)
        {
            Vector3 middle = Vector3.zero; int n = 0;
            foreach (var p in acc.V) if (p.y > height * 0.45f) { middle += new Vector3(p.x, 0f, p.z); n++; }
            middle /= Mathf.Max(1, n);
            float Reach(Vector3 p) => new Vector2(p.x - middle.x, p.z - middle.z).magnitude;
            float Heading(Vector3 p) => Mathf.Atan2(p.z - middle.z, p.x - middle.x) * Mathf.Rad2Deg;
            Vector3 tip = middle;
            foreach (var p in acc.V) if (p.y > height * 0.3f && Reach(p) > Reach(tip)) tip = p;
            float toward = Heading(tip);
            // The crown's radius is measured away from the offshoot.
            var others = new List<float>();
            foreach (var p in acc.V)
                if (p.y > height * 0.3f && Mathf.Abs(Mathf.DeltaAngle(Heading(p), toward)) > 60f) others.Add(Reach(p));
            others.Sort();
            float crown = others[(int)(others.Count * 0.98f)];
            bool Gone(Vector3 p) => p.y < height * 0.68f && p.y > height * 0.3f && Reach(p) > crown && Mathf.Abs(Mathf.DeltaAngle(Heading(p), toward)) < 40f;
            var kept = new List<int>(acc.T.Count);
            int cut = 0;
            for (int i = 0; i < acc.T.Count; i += 3)
            {
                if (Gone(acc.V[acc.T[i]]) || Gone(acc.V[acc.T[i + 1]]) || Gone(acc.V[acc.T[i + 2]])) { cut++; continue; }
                kept.Add(acc.T[i]); kept.Add(acc.T[i + 1]); kept.Add(acc.T[i + 2]);
            }
            acc.T.Clear();
            acc.T.AddRange(kept);
            Debug.Log($"[BUILD] {assetName}: offshoot reached {Reach(tip):F2} m, the crown {crown:F2} m; {cut} triangles cut");
        }

        static float PropHeight(string name) => name switch
        {
            "barrier" => 0.9f, "dumpster" => 1.35f, "trashcan" => 0.95f, "bench" => 0.85f, "crates" => 1.3f,
            "car" => 1.42f, "streetlamp" => 8.5f, "tree" or "tree_lod" => 8f, _ => 1f
        };

        // ---- mesh merging ----

        MeshAccum A(Material m, Vector3 pos)
        {
            int cx = Mathf.FloorToInt((pos.x + 400f) / 90f), cz = Mathf.FloorToInt((pos.z + 400f) / 90f);
            var key = (m, cx * 100 + cz);
            if (!accums.TryGetValue(key, out var a)) accums[key] = a = new MeshAccum();
            return a;
        }

        void FlushMeshes()
        {
            Art.EnsureFolder($"{Art.Gen}/City");
            int i = 0;
            long tris = 0;
            var meshRoot = new GameObject("Meshes").transform;
            meshRoot.SetParent(root, false);
            foreach (var kv in accums)
            {
                var mesh = kv.Value.Build($"city_{i}");
                string path = $"{Art.Gen}/City/city_{i}.asset";
                AssetDatabase.CreateAsset(mesh, path);
                var go = new GameObject($"{kv.Key.Item1.name}_{kv.Key.Item2}");
                go.transform.SetParent(meshRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = kv.Key.Item1;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI & 0 | StaticEditorFlags.ReflectionProbeStatic);
                tris += kv.Value.T.Count / 3;
                i++;
            }
            Debug.Log($"[BUILD] merged into {i} meshes, {tris} triangles");
        }

        // ---- colliders and cover ----

        /// <summary>rough: the box only roughly matches what is drawn, so decals stay off it (see Surface).</summary>
        void Box(Vector3 center, Vector3 size, float yaw = 0f, bool rough = false)
        {
            var go = new GameObject(rough ? "RoughBox" : "Box");
            go.layer = Layers.World;
            go.isStatic = true;
            go.transform.SetParent(colliders, false);
            go.transform.SetPositionAndRotation(center, Quaternion.Euler(0, yaw, 0));
            go.AddComponent<BoxCollider>().size = size;
            boxCount++;
        }

        void CoverAround(Vector3 center, float halfX, float halfZ, float yaw, float top, float spacing)
        {
            float height = top - Mathf.Max(0f, center.y - top);
            if (top - GroundY(center) < 0.8f) return;
            bool low = top - GroundY(center) < 1.5f;
            var q = Quaternion.Euler(0, yaw, 0);
            const float off = 0.72f;
            void Line(Vector3 a, Vector3 b, Vector3 n)
            {
                float len = Vector3.Distance(a, b);
                int count = Mathf.Max(1, Mathf.RoundToInt(len / spacing));
                for (int i = 0; i < count; i++)
                {
                    Vector3 p = Vector3.Lerp(a, b, (i + .5f) / count) + n * off;
                    Vector3 world = center + q * p;
                    world.y = GroundY(world);   // on the ground, whatever the height of the obstacle's centre
                    cover.Add(new CoverPoint { Position = world, Normal = q * n, Low = low });
                }
            }
            Line(new Vector3(-halfX, 0, -halfZ), new Vector3(halfX, 0, -halfZ), Vector3.back);
            Line(new Vector3(-halfX, 0, halfZ), new Vector3(halfX, 0, halfZ), Vector3.forward);
            Line(new Vector3(-halfX, 0, -halfZ), new Vector3(-halfX, 0, halfZ), Vector3.left);
            Line(new Vector3(halfX, 0, -halfZ), new Vector3(halfX, 0, halfZ), Vector3.right);
        }

        static float GroundY(Vector3 p)
        {
            // Everything off the road surface stands on the raised slab.
            foreach (var x in Lines) if (Mathf.Abs(p.x - x) < RoadHalf && Mathf.Abs(p.z) < Lines[N - 1] + RoadHalf) return 0f;
            foreach (var z in Lines) if (Mathf.Abs(p.z - z) < RoadHalf && Mathf.Abs(p.x) < Lines[N - 1] + RoadHalf) return 0f;
            return Curb;
        }

        readonly List<Vector3> placed = new();

        /// <summary>Everything placed against the solid ground under it, from the colliders themselves.</summary>
        void CheckStanding()
        {
            Physics.SyncTransforms();
            int floating = 0; float worst = 0f;
            foreach (var p in placed)
            {
                float ground = float.MinValue;
                foreach (var hit in Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 3f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                    if (hit.collider.name == "Box") ground = Mathf.Max(ground, hit.point.y);
                float gap = Mathf.Abs(p.y - ground);
                if (gap > 0.02f) { floating++; worst = Mathf.Max(worst, gap); }
            }
            Debug.Log($"[BUILD] standing check: {placed.Count} props and cars, {floating} off the ground, worst {worst:F2} m");
            if (floating > 0) Debug.LogError($"[BUILD] {floating} objects off the ground, too few standing");
        }

        // ---- props ----

        /// <summary>Puts a prop at a ground position; colliderKind: 0 none, 1 box + cover, 2 thin pole, 3 box without cover.</summary>
        void Prop(string name, float x, float z, float yaw, Color tint, int colliderKind, float scale = 1f)
        {
            var info = props[name];
            float y = GroundY(new Vector3(x, 0, z));
            var pos = new Vector3(x, y, z);
            placed.Add(pos);
            var m = Matrix4x4.TRS(pos, Quaternion.Euler(0, yaw, 0), Vector3.one * scale) * info.Norm;
            if (name != "tree") A(info.Mat, pos).Add(info.Mesh, m, tint);    // trees are objects of their own, see Tree
            Vector3 s = info.Size * scale;
            if (colliderKind == 1 || colliderKind == 3)
            {
                Vector3 center = pos + Vector3.up * s.y * .5f;
                Box(center, s, yaw, true);
                if (colliderKind == 1) CoverAround(center, s.x * .5f, s.z * .5f, yaw, pos.y + s.y, 1.9f);
            }
            else if (colliderKind == 2)
            {
                var go = new GameObject("Pole");
                go.layer = Layers.World; go.isStatic = true;
                go.transform.SetParent(colliders, false);
                go.transform.position = pos;
                var c = go.AddComponent<CapsuleCollider>();
                c.radius = name == "tree" ? 0.28f * scale : 0.12f; c.height = name == "tree" ? 3.2f : s.y; c.center = Vector3.up * c.height * .5f;
            }
        }

        // ---- cars and lamps ----

        /// <summary>How often each car model is parked in the city (shares add up to 1).</summary>
        static readonly (string model, float share)[] CarMix = { ("car", 0.36f), ("car_b", 0.2f), ("dacia", 0.2f), ("oltcit", 0.12f), ("trabant", 0.12f) };

        string PickCarModel()
        {
            float r = R(0, 1);
            foreach (var (model, share) in CarMix) { if (r < share) return model; r -= share; }
            return CarMix[0].model;
        }

        void PlaceCar(float x, float z, float yaw)
        {
            string model = PickCarModel();
            int paint = RI(0, CarTints.Length);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(vehicles[model]);
            go.transform.SetParent(carRoot, false);
            Vector3 pos = new(x, GroundY(new Vector3(x, 0, z)), z);
            placed.Add(pos);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            if (!paints.TryGetValue((model, paint), out var mat)) paints[(model, paint)] = mat = Art.CarPaint(model, paint + 1, CarTints[paint]);
            var v = go.GetComponent<Vehicle>();
            foreach (var r in v.BodyRenderers) r.sharedMaterial = mat;
            CoverAround(pos + Vector3.up * .7f, v.BodySize.x * .5f, v.BodySize.z * .5f, yaw, pos.y + v.BodySize.y, 1.9f);
            carCount++;
        }

        /// <summary>A street lamp, its bulb and its pool of light on the ground (both only show at night).</summary>
        void Lamp(float x, float z, float yaw)
        {
            Prop("streetlamp", x, z, yaw, Color.white, 2);
            var info = props["streetlamp"];
            Quaternion q = Quaternion.Euler(0, yaw, 0);
            float g = GroundY(new Vector3(x, 0, z));
            Vector3 bulb = new Vector3(x, g + info.Size.y * 0.95f, z) + q * Vector3.forward * (info.Size.z * 0.82f);
            lamps.Add(bulb - Vector3.up * 0.3f);

            var acc = A(nightGlow, bulb);
            Color warm = new(1f, .78f, .5f, 1f);
            Vector3 c = new(bulb.x, GroundY(bulb) + 0.14f, bulb.z);
            const float r = 8.5f;
            acc.Quad(c + new Vector3(-r, 0, -r), c + new Vector3(-r, 0, r), c + new Vector3(r, 0, r), c + new Vector3(r, 0, -r), Vector2.zero, Vector2.up, Vector2.one, Vector2.right, warm * 0.42f);
            const float b = 1.1f;
            Vector3 fwd = q * Vector3.forward, side = q * Vector3.right;
            foreach (var dir in new[] { fwd, side })
                acc.Quad(bulb - dir * b - Vector3.up * b, bulb - dir * b + Vector3.up * b, bulb + dir * b + Vector3.up * b, bulb + dir * b - Vector3.up * b, Vector2.zero, Vector2.up, Vector2.one, Vector2.right, warm * 3.4f);
            acc.Quad(bulb + new Vector3(-b, -.05f, -b), bulb + new Vector3(-b, -.05f, b), bulb + new Vector3(b, -.05f, b), bulb + new Vector3(b, -.05f, -b), Vector2.zero, Vector2.up, Vector2.one, Vector2.right, warm * 5f);
        }

        // ---- roads ----

        void BuildRoadsAndGround()
        {
            var road = root.gameObject.AddComponent<RoadNetwork>();
            float maxL = Lines[N - 1];

            // Intersections, then the road pieces between them: asphalt only where it is visible.
            foreach (var x in Lines)
                foreach (var z in Lines)
                    A(asphalt, new Vector3(x, 0, z)).Flat(x - RoadHalf, z - RoadHalf, x + RoadHalf, z + RoadHalf, 0f, 8f, Color.white);
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N - 1; j++)
                {
                    float a = Lines[j] + RoadHalf, b = Lines[j + 1] - RoadHalf;
                    A(asphalt, new Vector3(Lines[i], 0, (a + b) / 2)).Flat(Lines[i] - RoadHalf, a, Lines[i] + RoadHalf, b, 0f, 8f, Color.white);
                    A(asphalt, new Vector3((a + b) / 2, 0, Lines[i])).Flat(a, Lines[i] - RoadHalf, b, Lines[i] + RoadHalf, 0f, 8f, Color.white);
                    Markings(new Vector3(Lines[i], 0, a), new Vector3(Lines[i], 0, b));
                    Markings(new Vector3(a, 0, Lines[i]), new Vector3(b, 0, Lines[i]));
                }

            // Road collider, a single slab under everything.
            Box(new Vector3(0, -0.5f, 0), new Vector3(2 * (maxL + RoadHalf + 40f), 1f, 2 * (maxL + RoadHalf + 40f)));

            // Raised slabs: the sidewalk ring of each block (its middle is filled by the block itself).
            for (int i = 0; i < N - 1; i++)
                for (int j = 0; j < N - 1; j++)
                {
                    float x0 = Lines[i] + RoadHalf, x1 = Lines[i + 1] - RoadHalf, z0 = Lines[j] + RoadHalf, z1 = Lines[j + 1] - RoadHalf;
                    SidewalkRing(x0, z0, x1, z1);
                    Box(new Vector3((x0 + x1) / 2, Curb / 2, (z0 + z1) / 2), new Vector3(x1 - x0, Curb, z1 - z0));
                }

            // Outer sidewalk, all round the perimeter road.
            float o0 = maxL + RoadHalf, o1 = Bound + 8f;
            foreach (var (sx, sz, x0, z0, x1, z1) in new[]
            {
                (0, -1, -o1, -o1, o1, -o0), (0, 1, -o1, o0, o1, o1),
                (-1, 0, -o1, -o0, -o0, o0), (1, 0, o0, -o0, o1, o0),
            })
            {
                A(sidewalk, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2)).Flat(x0, z0, x1, z1, Curb, 3f, Color.white);
                Box(new Vector3((x0 + x1) / 2, Curb / 2, (z0 + z1) / 2), new Vector3(x1 - x0, Curb, z1 - z0));
            }
            // Curb faces of the perimeter.
            CurbLine(o0, -o0, -o0, -o0); CurbLine(-o0, o0, o0, o0); CurbLine(-o0, -o0, -o0, o0); CurbLine(o0, o0, o0, -o0);

            BuildNetwork(road);
        }

        void CurbLine(float x0, float z0, float x1, float z1)
        {
            var a = new Vector3(x0, 0, z0); var b = new Vector3(x1, 0, z1);
            A(concrete, (a + b) / 2).Wall(a, b, 0f, Curb, Vector3.Distance(a, b) / 2f, 0f, 0.05f, C(.8f, .8f, .78f));
        }

        void SidewalkRing(float x0, float z0, float x1, float z1)
        {
            float w = WalkW;
            var acc = A(sidewalk, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2));
            acc.Flat(x0, z0, x1, z0 + w, Curb, 3f, Color.white);
            acc.Flat(x0, z1 - w, x1, z1, Curb, 3f, Color.white);
            acc.Flat(x0, z0 + w, x0 + w, z1 - w, Curb, 3f, Color.white);
            acc.Flat(x1 - w, z0 + w, x1, z1 - w, Curb, 3f, Color.white);
            // Curb faces, seen from the road.
            CurbLine(x0, z1, x0, z0);
            CurbLine(x0, z0, x1, z0);
            CurbLine(x1, z0, x1, z1);
            CurbLine(x1, z1, x0, z1);
        }

        /// <summary>Every third block of road has one, chosen from its position so the layout does not depend on the random numbers.</summary>
        static bool HasZebra(Vector3 a, Vector3 b)
        {
            Vector3 m = (a + b) * .5f;
            int ix = Mathf.RoundToInt(m.x / (Pitch * .5f)), iz = Mathf.RoundToInt(m.z / (Pitch * .5f));
            return Mathf.Abs(ix * 5 + iz * 7) % 3 == 0;
        }

        void Mark(Vector3 center, Vector3 along, float length, float width, Color col)
        {
            Vector3 side = Vector3.Cross(Vector3.up, along).normalized * (width / 2);
            Vector3 f = along.normalized * (length / 2);
            Vector3 up = Vector3.up * 0.025f;
            A(marking, center).Quad(center - f - side + up, center + f - side + up, center + f + side + up, center - f + side + up,
                Vector2.zero, Vector2.up * .3f, Vector2.one * .3f, Vector2.right * .3f, col);
        }

        void Markings(Vector3 a, Vector3 b)
        {
            Vector3 d = (b - a).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, d);
            float len = Vector3.Distance(a, b);
            Color white = C(1.25f, 1.25f, 1.2f), yellow = C(1.3f, 1f, .25f);
            // Crosswalks at both ends, stop lines, centre dashes, parking-lane edges.
            for (int end = 0; end < 2; end++)
            {
                Vector3 mouth = end == 0 ? a : b;
                Vector3 inward = end == 0 ? d : -d;
                for (float t = -RoadHalf + 0.5f; t < RoadHalf - 0.3f; t += 1.1f)
                    Mark(mouth + inward * 1.7f + side * t, d, 2.8f, 0.55f, white);
                // The stop line crosses the lane arriving here: that lane keeps to the right of its own direction.
                Vector3 arriving = -inward;
                Vector3 right = Vector3.Cross(Vector3.up, arriving);
                Mark(mouth + inward * 3.6f + right * (LaneOffset), side, 3.2f, 0.4f, white);
            }
            if (HasZebra(a, b))
            {
                // A zebra crossing in the middle of the block, curb to curb.
                Vector3 mid = (a + b) * .5f;
                for (float t = -RoadHalf + 0.5f; t < RoadHalf - 0.3f; t += 1.1f)
                    Mark(mid + side * t, d, 3.4f, 0.58f, white);
                foreach (var e in new[] { -1f, 1f })
                    Mark(mid - d * (e * 3.4f) + side * (e * LaneOffset), side, 3.2f, 0.36f, white);   // give-way lines, on the lane coming towards it
                crossings.Add(mid);
            }
            for (float t = 5f; t < len - 5f; t += 5f)
            {
                if (HasZebra(a, b) && Mathf.Abs(t - len * .5f) < 4.2f) continue;
                Mark(a + d * t, d, 2.4f, 0.14f, yellow);
            }
            foreach (var s in new[] { -1f, 1f })
                Mark((a + b) / 2 + side * (s * 3.5f), d, len - 9f, 0.1f, white);
        }

        void BuildNetwork(RoadNetwork net)
        {
            var nodes = new List<Vector3>();
            int Id(int i, int j) => i * N + j;
            for (int i = 0; i < N; i++) for (int j = 0; j < N; j++) nodes.Add(new Vector3(Lines[i], 0, Lines[j]));
            var lanes = new List<RoadNetwork.Lane>();
            void Dir(int from, int to)
            {
                Vector3 a = nodes[from], b = nodes[to], d = (b - a).normalized, right = Vector3.Cross(Vector3.up, d);
                var pts = new List<Vector3>();
                float len = Vector3.Distance(a, b) - 2f * RoadHalf;
                int steps = Mathf.CeilToInt(len / 4f);
                for (int s = 0; s <= steps; s++) pts.Add(a + d * (RoadHalf + len * s / steps) + right * LaneOffset);
                lanes.Add(new RoadNetwork.Lane { FromNode = from, ToNode = to, Points = pts.ToArray() });
            }
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N - 1; j++)
                {
                    Dir(Id(i, j), Id(i, j + 1)); Dir(Id(i, j + 1), Id(i, j));
                    Dir(Id(j, i), Id(j + 1, i)); Dir(Id(j + 1, i), Id(j, i));
                }

            var connectors = new List<RoadNetwork.Connector>();
            for (int l = 0; l < lanes.Count; l++)
            {
                var inc = lanes[l];
                var next = new List<int>();
                Vector3 p0 = inc.Points[^1];
                Vector3 inDir = (p0 - inc.Points[^2]).normalized;
                for (int k = 0; k < lanes.Count; k++)
                {
                    var outg = lanes[k];
                    if (outg.FromNode != inc.ToNode || outg.ToNode == inc.FromNode) continue;
                    Vector3 p3 = outg.Points[0];
                    Vector3 outDir = (outg.Points[1] - p3).normalized;
                    float c = Vector3.Cross(inDir, outDir).y;
                    int turn = Mathf.Abs(c) < 0.1f ? 0 : (c > 0 ? 1 : -1);
                    // Quadratic bezier through the corner where the two lane lines meet.
                    Vector3 ctrl = turn == 0 ? (p0 + p3) / 2 : p0 + inDir * Vector3.Dot(p3 - p0, inDir);
                    var pts = new Vector3[8];
                    for (int s = 0; s < 8; s++)
                    {
                        float t = s / 7f;
                        pts[s] = (1 - t) * (1 - t) * p0 + 2 * (1 - t) * t * ctrl + t * t * p3;
                    }
                    next.Add(connectors.Count);
                    connectors.Add(new RoadNetwork.Connector { FromLane = l, ToLane = k, Node = inc.ToNode, Points = pts, Turn = turn });
                }
                inc.NextConnectors = next.ToArray();
            }
            net.Nodes = nodes.ToArray();
            net.Lanes = lanes.ToArray();
            net.Connectors = connectors.ToArray();
            Debug.Log($"[BUILD] road network: {nodes.Count} nodes, {lanes.Count} lanes, {connectors.Count} connectors");
        }

        // ---- blocks ----

        void BuildBlocks()
        {
            for (int i = 0; i < N - 1; i++)
                for (int j = 0; j < N - 1; j++)
                {
                    float x0 = Lines[i] + RoadHalf + WalkW, x1 = Lines[i + 1] - RoadHalf - WalkW;
                    float z0 = Lines[j] + RoadHalf + WalkW, z1 = Lines[j + 1] - RoadHalf - WalkW;
                    switch (Plan(i, j))
                    {
                        case BlockType.Buildings: BuildingsBlock(x0, z0, x1, z1, 14f, 42f); break;
                        case BlockType.Towers: BuildingsBlock(x0, z0, x1, z1, 40f, 92f); break;
                        case BlockType.Park: ParkBlock(x0, z0, x1, z1); break;
                        case BlockType.Parking: ParkingBlock(x0, z0, x1, z1); break;
                        case BlockType.Yard: YardBlock(x0, z0, x1, z1); break;
                    }
                    Vector3 c = new Vector3((x0 + x1) / 2, Curb, (z0 + z1) / 2);
                    patrol.Add(c + new Vector3(R(-6, 6), 0, R(-6, 6)));
                }
        }

        void BuildingsBlock(float x0, float z0, float x1, float z1, float hMin, float hMax)
        {
            A(sidewalk, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2)).Flat(x0, z0, x1, z1, Curb, 3f, C(.88f, .88f, .86f));
            float xs = Mathf.Lerp(x0, x1, R(.42f, .58f)), zs = Mathf.Lerp(z0, z1, R(.42f, .58f));
            const float alley = 3f;  // half-width: alleys are 6 m
            var cells = new[]
            {
                (x0, z0, xs - alley, zs - alley), (xs + alley, z0, x1, zs - alley),
                (x0, zs + alley, xs - alley, z1), (xs + alley, zs + alley, x1, z1),
            };
            int courtyard = RI(0, 5);   // 4 of 5 blocks lose one building to a hedged courtyard
            for (int c = 0; c < 4; c++)
            {
                var (a, b, d, e) = cells[c];
                if (c == courtyard) Courtyard(a, b, d, e);
                else Building(a, b, d, e, Mathf.Round(R(hMin, hMax) / 4f) * 4f);
            }
            // Alley clutter: this is where cover is thin.
            for (int n = 0; n < 3; n++)
            {
                Prop("dumpster", xs + R(-1.3f, 1.3f), Mathf.Lerp(z0 + 3f, z1 - 3f, R(.05f, .95f)), Mathf.Round(R(0, 2)) * 90f + R(-8, 8), Pick(MetalTints), 1);
                Prop("crates", Mathf.Lerp(x0 + 3f, x1 - 3f, R(.05f, .95f)), zs + R(-1.3f, 1.3f), R(0, 360), Vary(Color.white, .12f), 1);
            }
            spawns.Add(new Vector3(xs, Curb, Mathf.Lerp(z0, z1, .15f)));
            spawns.Add(new Vector3(xs, Curb, Mathf.Lerp(z0, z1, .85f)));
            spawns.Add(new Vector3(Mathf.Lerp(x0, x1, .15f), Curb, zs));
            spawns.Add(new Vector3(Mathf.Lerp(x0, x1, .85f), Curb, zs));
        }

        void Building(float x0, float z0, float x1, float z1, float height)
        {
            buildingCount++;
            bool glass = height > 34f && R(0, 1) < 0.75f;
            var wall = glass ? facadeGlass : Pick(facadeWall);
            Color tint = Vary(glass ? Pick(GlassTints) : Pick(WallTints), 0.06f);
            tint.a = R(0f, 1f);   // the seed for which windows are lit at night
            float y0 = Curb, y1 = Curb + height;
            float tileW = glass ? 30f : 12f, tileH = glass ? 30f : 12f;   // wall textures show 3-4 floors; the glass grid is finer
            bool shops = R(0, 1) < 0.75f;
            var pos = new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2);
            Color shopTint = Vary(Color.white, 0.1f);
            shopTint.a = R(0f, 1f);

            void Face(Vector3 a, Vector3 b)
            {
                float w = Vector3.Distance(a, b);
                float u = Mathf.Max(1f, Mathf.Round(w / tileW));
                float baseTop = shops ? y0 + 4.5f : y0;
                if (shops)
                    A(storefront, pos).Wall(a, b, y0, baseTop, Mathf.Max(1f, Mathf.Round(w / 20f)), 0.24f, 0.92f, shopTint);
                float vTiles = Mathf.Max(1f, Mathf.Round((y1 - baseTop) / tileH));
                A(wall, pos).Wall(a, b, baseTop, y1 - 0.5f, u, 0f, vTiles, tint);
            }
            Vector3 p00 = new(x0, 0, z0), p10 = new(x1, 0, z0), p11 = new(x1, 0, z1), p01 = new(x0, 0, z1);
            Face(p00, p10); Face(p10, p11); Face(p11, p01); Face(p01, p00);
            // A cornice that stands proud of the walls: separate surfaces with no faces in the same plane, so nothing flickers at the edges.
            const float proud = 0.18f;
            Vector3 c00 = new(x0 - proud, 0, z0 - proud), c10 = new(x1 + proud, 0, z0 - proud), c11 = new(x1 + proud, 0, z1 + proud), c01 = new(x0 - proud, 0, z1 + proud);
            Color band = C(.75f, .75f, .74f);
            foreach (var (ca, cb) in new[] { (c00, c10), (c10, c11), (c11, c01), (c01, c00) })
                A(concrete, pos).Wall(ca, cb, y1 - 0.55f, y1 + 0.1f, Vector3.Distance(ca, cb) / 3f, 0f, 0.2f, band);
            float under = y1 - 0.55f;
            A(concrete, pos).Quad(new Vector3(c00.x, under, c00.z), new Vector3(c10.x, under, c10.z), new Vector3(c11.x, under, c11.z), new Vector3(c01.x, under, c01.z),
                Vector2.zero, Vector2.right, Vector2.one, Vector2.up, band * 0.7f);
            A(roof, pos).Flat(c00.x, c00.z, c11.x, c11.z, y1 + 0.1f, 9f, Vary(Color.white, .1f));

            Box(new Vector3((x0 + x1) / 2, y0 + height / 2, (z0 + z1) / 2), new Vector3(x1 - x0, height, z1 - z0));
            CoverAround(pos + Vector3.up * (y0 + height / 2), (x1 - x0) / 2, (z1 - z0) / 2, 0f, y1, 4.5f);
        }

        void Courtyard(float x0, float z0, float x1, float z1)
        {
            A(grass, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2)).Flat(x0, z0, x1, z1, Curb + .05f, 5f, Vary(Color.white, .1f));
            Color t = Pick(HedgeTints);
            float gap = 2.2f;
            float mx = (x0 + x1) / 2;
            Hedge(new Vector3(x0 + .6f, 0, z0 + .6f), new Vector3(mx - gap, 0, z0 + .6f), 1.1f, .8f, t);
            Hedge(new Vector3(mx + gap, 0, z0 + .6f), new Vector3(x1 - .6f, 0, z0 + .6f), 1.1f, .8f, t);
            Hedge(new Vector3(x0 + .6f, 0, z1 - .6f), new Vector3(mx - gap, 0, z1 - .6f), 1.1f, .8f, t);
            Hedge(new Vector3(mx + gap, 0, z1 - .6f), new Vector3(x1 - .6f, 0, z1 - .6f), 1.1f, .8f, t);
            Hedge(new Vector3(x0 + .6f, 0, z0 + 1.4f), new Vector3(x0 + .6f, 0, z1 - 1.4f), 1.1f, .8f, t);
            Hedge(new Vector3(x1 - .6f, 0, z0 + 1.4f), new Vector3(x1 - .6f, 0, z1 - 1.4f), 1.1f, .8f, t);
            Tree((x0 + x1) / 2 + R(-3, 3), (z0 + z1) / 2 + R(-3, 3));
            Prop("bench", (x0 + x1) / 2 + R(-4, 4), z0 + 3f, 90f, Color.white, 1);
        }

        void Tree(float x, float z)
        {
            float s = R(.85f, 1.25f);
            float ground = GroundY(new Vector3(x, 0, z));
            float yaw = R(0, 360);
            Prop("tree", x, z, yaw, Color.white, 2, s);
            // All the trees share one mesh rather than each being merged into the city: full detail near by, the light version beyond about 70 m.
            if (treeRoot == null)
            {
                treeRoot = new GameObject("Trees").transform;
                treeRoot.SetParent(root, false);
            }
            var tree = new GameObject("Tree");
            tree.transform.SetParent(treeRoot, false);
            tree.transform.SetPositionAndRotation(new Vector3(x, ground, z), Quaternion.Euler(0f, yaw, 0f));
            tree.transform.localScale = Vector3.one * s;
            var mat = treeMats[RI(0, treeMats.Length)];
            MeshRenderer Version(string prop)
            {
                var go = new GameObject(prop);
                go.transform.SetParent(tree.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = props[prop].Mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                return r;
            }
            var near = Version("tree"); var far = Version("tree_lod");
            var lod = tree.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.1f, new Renderer[] { near }), new LOD(0.004f, new Renderer[] { far }) });
            lod.RecalculateBounds();
            treeCount++;
            A(dirt, new Vector3(x, 0, z)).Flat(x - .9f, z - .9f, x + .9f, z + .9f, GroundY(new Vector3(x, 0, z)) + .12f, 2f, C(.55f, .52f, .5f));
        }

        Transform treeRoot;
        int treeCount;

        void ParkBlock(float x0, float z0, float x1, float z1)
        {
            A(grass, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2)).Flat(x0, z0, x1, z1, Curb, 6f, Color.white);
            float mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
            // Cross-shaped paths.
            A(sidewalk, new Vector3(mx, 0, mz)).Flat(mx - 2.5f, z0, mx + 2.5f, z1, Curb + .05f, 3f, Color.white);
            A(sidewalk, new Vector3(mx, 0, mz)).Flat(x0, mz - 2.5f, x1, mz + 2.5f, Curb + .09f, 3f, Color.white);
            // Monument in the middle: a tall plain plinth, the park's tall cover.
            ConcreteBlock(new Vector3(mx, 0, mz), 5f, 3.2f, 5f);

            // Hedge rectangles in the four quarters, each with entrances.
            Color t = Pick(HedgeTints);
            float inner = 3.6f;
            foreach (var sx in new[] { -1f, 1f })
                foreach (var sz in new[] { -1f, 1f })
                {
                    float qx0 = mx + sx * inner, qz0 = mz + sz * inner, qx1 = mx + sx * 21f, qz1 = mz + sz * 21f;
                    Hedge(new Vector3(qx0, 0, qz0), new Vector3(Mathf.Lerp(qx0, qx1, .42f), 0, qz0), 1.15f, .8f, t);
                    Hedge(new Vector3(Mathf.Lerp(qx0, qx1, .66f), 0, qz0), new Vector3(qx1, 0, qz0), 1.15f, .8f, t);
                    Hedge(new Vector3(qx0, 0, qz0), new Vector3(qx0, 0, Mathf.Lerp(qz0, qz1, .42f)), 1.15f, .8f, t);
                    Hedge(new Vector3(qx0, 0, Mathf.Lerp(qz0, qz1, .66f)), new Vector3(qx0, 0, qz1), 1.15f, .8f, t);
                    Hedge(new Vector3(qx1, 0, qz0), new Vector3(qx1, 0, Mathf.Lerp(qz0, qz1, .5f)), 1.15f, .8f, t);
                    Hedge(new Vector3(qx0, 0, qz1), new Vector3(Mathf.Lerp(qx0, qx1, .5f), 0, qz1), 1.15f, .8f, t);
                    Tree(Mathf.Lerp(qx0, qx1, R(.45f, .65f)), Mathf.Lerp(qz0, qz1, R(.45f, .65f)));
                    Tree(Mathf.Lerp(qx0, qx1, R(.75f, .85f)), Mathf.Lerp(qz0, qz1, R(.2f, .35f)));
                    // Benches along the paths.
                    Prop("bench", mx + sx * R(5f, 12f), mz + sz * 3.4f, sz > 0 ? 90f : -90f, Color.white, 1);
                }
            foreach (var s in new[] { -1f, 1f })
            {
                Prop("trashcan", mx + s * 3.1f, mz + 8f, 0, Pick(MetalTints), 3);
                spawns.Add(new Vector3(mx + s * 12f, Curb, mz - s * 12f));
            }
        }

        void ConcreteBlock(Vector3 ground, float w, float h, float d)
        {
            float y0 = GroundY(ground), y1 = y0 + h;
            Vector3 c = new(ground.x, 0, ground.z);
            float hx = w / 2, hz = d / 2;
            var acc = A(concrete, c);
            Color t = Pick(ConcreteTints);
            Vector3 p00 = c + new Vector3(-hx, 0, -hz), p10 = c + new Vector3(hx, 0, -hz), p11 = c + new Vector3(hx, 0, hz), p01 = c + new Vector3(-hx, 0, hz);
            acc.Wall(p00, p10, y0, y1, w / 3f, 0, h / 3f, t); acc.Wall(p10, p11, y0, y1, d / 3f, 0, h / 3f, t);
            acc.Wall(p11, p01, y0, y1, w / 3f, 0, h / 3f, t); acc.Wall(p01, p00, y0, y1, d / 3f, 0, h / 3f, t);
            acc.Flat(c.x - hx, c.z - hz, c.x + hx, c.z + hz, y1, 3f, t);
            Vector3 center = new Vector3(c.x, (y0 + y1) / 2, c.z);
            Box(center, new Vector3(w, h, d));
            CoverAround(center, hx, hz, 0f, y1, h < 1.5f ? 1.9f : 3f);
        }

        void ParkingBlock(float x0, float z0, float x1, float z1)
        {
            A(asphalt, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2)).Flat(x0, z0, x1, z1, Curb, 8f, C(.92f, .92f, .92f));
            Color white = C(1.2f, 1.2f, 1.15f);
            float mx = (x0 + x1) / 2;
            // Rows of bays, hedge islands between, entrance from the road side left open.
            for (int row = 0; row < 3; row++)
            {
                float z = Mathf.Lerp(z0 + 7f, z1 - 7f, row / 2f);
                for (float x = x0 + 4f; x < x1 - 3f; x += 3.1f)
                {
                    Mark(new Vector3(x, Curb, z), Vector3.forward, 5.4f, 0.1f, white);
                    if (R(0, 1) < 0.34f && x + 1.55f < x1 - 3f)
                    {
                        PlaceCar(x + 1.55f, z + R(-.2f, .2f), (R(0, 1) < .5f ? 0f : 180f) + R(-3, 3));
                    }
                }
                if (row == 1) { /* central row keeps the middle lane free */ }
            }
            Color t = Pick(HedgeTints);
            Hedge(new Vector3(x0 + .6f, 0, z0 + 13f), new Vector3(x0 + .6f, 0, z1 - 13f), 1.3f, .9f, t);
            Hedge(new Vector3(x1 - .6f, 0, z0 + 13f), new Vector3(x1 - .6f, 0, z1 - 13f), 1.3f, .9f, t);
            Lamp(mx - 10f, (z0 + z1) / 2, 90f);
            Lamp(mx + 10f, (z0 + z1) / 2, -90f);
            spawns.Add(new Vector3(mx - 14f, Curb, z0 + 13f));
            spawns.Add(new Vector3(mx + 14f, Curb, z1 - 13f));
        }

        void YardBlock(float x0, float z0, float x1, float z1)
        {
            A(concrete, new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2)).Flat(x0, z0, x1, z1, Curb, 5f, C(.8f, .78f, .72f));
            float mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
            Color t = Pick(HedgeTints);
            float gap = 4f;
            // Hedged yard with four gates.
            foreach (var (a, b) in new[]
            {
                (new Vector3(x0 + .8f, 0, z0 + .8f), new Vector3(mx - gap, 0, z0 + .8f)), (new Vector3(mx + gap, 0, z0 + .8f), new Vector3(x1 - .8f, 0, z0 + .8f)),
                (new Vector3(x0 + .8f, 0, z1 - .8f), new Vector3(mx - gap, 0, z1 - .8f)), (new Vector3(mx + gap, 0, z1 - .8f), new Vector3(x1 - .8f, 0, z1 - .8f)),
                (new Vector3(x0 + .8f, 0, z0 + 1.6f), new Vector3(x0 + .8f, 0, mz - gap)), (new Vector3(x0 + .8f, 0, mz + gap), new Vector3(x0 + .8f, 0, z1 - 1.6f)),
                (new Vector3(x1 - .8f, 0, z0 + 1.6f), new Vector3(x1 - .8f, 0, mz - gap)), (new Vector3(x1 - .8f, 0, mz + gap), new Vector3(x1 - .8f, 0, z1 - 1.6f)),
            })
                Hedge(a, b, 2.1f, 1f, t);
            // Cover maze of crates, barriers, containers (plain concrete blocks) and dumpsters.
            for (int ix = 0; ix < 4; ix++)
                for (int iz = 0; iz < 4; iz++)
                {
                    float x = Mathf.Lerp(x0 + 8f, x1 - 8f, ix / 3f) + R(-1.5f, 1.5f);
                    float z = Mathf.Lerp(z0 + 8f, z1 - 8f, iz / 3f) + R(-1.5f, 1.5f);
                    switch (RI(0, 4))
                    {
                        case 0: Prop("crates", x, z, R(0, 360), Vary(Color.white, .14f), 1); Prop("crates", x + 2.2f, z + R(-.4f, .4f), R(0, 360), Vary(Color.white, .14f), 1); break;
                        case 1: Prop("barrier", x, z, R(0, 1) < .5f ? 0f : 90f, Pick(ConcreteTints), 1); Prop("barrier", x + R(-.3f, .3f), z + R(3f, 3.4f), R(0, 1) < .5f ? 0f : 90f, Pick(ConcreteTints), 1); break;
                        case 2: ConcreteBlock(new Vector3(x, 0, z), R(2.5f, 5f), R(1.2f, 2.6f), R(2.5f, 5f)); break;
                        default: Prop("dumpster", x, z, Mathf.Round(R(0, 3)) * 90f, Pick(MetalTints), 1); break;
                    }
                }
            spawns.Add(new Vector3(x0 + 4f, Curb, z0 + 4f));
            spawns.Add(new Vector3(x1 - 4f, Curb, z1 - 4f));
        }

        // ---- procedural hedge ----

        /// <summary>A hedge is a rounded box tube along a line, its surface pushed by noise, tinted per vertex with noise too.</summary>
        void Hedge(Vector3 a, Vector3 b, float height, float thick, Color tint)
        {
            a.y = b.y = GroundY((a + b) / 2);
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            d /= len;
            Vector3 side = Vector3.Cross(Vector3.up, d);
            int sections = Mathf.Max(2, Mathf.CeilToInt(len / 0.5f));
            // Cross section, outline points from bottom left over the top to bottom right.
            var ring = new (float s, float h)[]
            {
                (-.5f, 0f), (-.52f, .35f), (-.5f, .7f), (-.38f, .92f), (-.15f, 1f), (.15f, 1f), (.38f, .92f), (.5f, .7f), (.52f, .35f), (.5f, 0f)
            };
            int rn = ring.Length;
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color>(); var tris = new List<int>();
            float seed = R(0f, 1000f);
            float u = 0f;
            for (int i = 0; i <= sections; i++)
            {
                float t = i / (float)sections;
                float l = t * len;
                // Rounded ends.
                float endShrink = Mathf.Min(1f, Mathf.Min(l, len - l) / 0.35f);
                endShrink = Mathf.Sqrt(Mathf.Max(0.05f, endShrink));
                float arc = 0f;
                for (int r = 0; r < rn; r++)
                {
                    float noise = Mathf.PerlinNoise(seed + l * 1.3f, r * 0.7f + seed) - .5f;
                    float fine = Mathf.PerlinNoise(seed * .5f + l * 4.1f, r * 2.3f) - .5f;
                    float push = (noise * .22f + fine * .08f);
                    float sx = ring[r].s * thick * endShrink;
                    float hy = ring[r].h * height * Mathf.Lerp(.75f, 1f, endShrink);
                    Vector3 outward = new Vector3(0, ring[r].h, 0) + side * ring[r].s * 2f;
                    Vector3 p = a + d * l + side * sx + Vector3.up * hy + outward.normalized * push * (ring[r].h > 0.01f ? 1f : .2f);
                    if (r > 0) arc += Vector2.Distance(new Vector2(ring[r].s * thick, ring[r].h * height), new Vector2(ring[r - 1].s * thick, ring[r - 1].h * height));
                    verts.Add(p);
                    uvs.Add(new Vector2(u, arc) / 1.5f);
                    float shade = Mathf.Lerp(.72f, 1.12f, Mathf.PerlinNoise(seed + l * .9f, ring[r].h * 3f));
                    // Older, darker wood near the ground.
                    shade *= Mathf.Lerp(.72f, 1f, Mathf.Clamp01(ring[r].h * 2f));
                    cols.Add(new Color(tint.r * shade, tint.g * shade, tint.b * shade, 1f));
                }
                u += len / sections;
                if (i == 0) continue;
                int b0 = (i - 1) * rn, b1 = i * rn;
                for (int r = 0; r < rn - 1; r++)
                {
                    tris.Add(b0 + r); tris.Add(b1 + r + 1); tris.Add(b0 + r + 1);
                    tris.Add(b0 + r); tris.Add(b1 + r); tris.Add(b1 + r + 1);
                }
            }
            // Caps.
            for (int r = 1; r < rn - 1; r++)
            {
                tris.Add(0); tris.Add(r); tris.Add(r + 1);
                int e = sections * rn;
                tris.Add(e); tris.Add(e + r + 1); tris.Add(e + r);
            }
            var temp = new Mesh();
            temp.SetVertices(verts); temp.SetUVs(0, uvs); temp.SetColors(cols); temp.SetTriangles(tris, 0);
            temp.RecalculateNormals();
            var acc = A(hedge, (a + b) / 2);
            int start = acc.V.Count;
            acc.Add(temp, Matrix4x4.identity, Color.white);
            for (int i = 0; i < verts.Count; i++) acc.C[start + i] = cols[i];
            Object.DestroyImmediate(temp);

            hedgeMeters += Mathf.RoundToInt(len);
            Vector3 mid = (a + b) / 2;
            float yaw = Quaternion.LookRotation(d).eulerAngles.y;
            Box(mid + Vector3.up * height / 2, new Vector3(thick, height, len), yaw, true);
            if (height < 3f) CoverAround(mid + Vector3.up * height / 2, thick / 2, len / 2, yaw, a.y + height, height < 1.5f ? 1.9f : 3f);
        }

        // ---- street level ----

        void BuildStreetFurniture()
        {
            // Along both sides of every road piece: lamps, trees, bins, parked cars and barriers in the parking strip.
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N - 1; j++)
                {
                    Street(new Vector3(Lines[i], 0, Lines[j] + RoadHalf), new Vector3(Lines[i], 0, Lines[j + 1] - RoadHalf));
                    Street(new Vector3(Lines[j] + RoadHalf, 0, Lines[i]), new Vector3(Lines[j + 1] - RoadHalf, 0, Lines[i]));
                }
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    // Corner spawn and patrol points on the sidewalks.
                    Vector3 p = new Vector3(Lines[i] + 8f * (i < N / 2 ? 1 : -1), Curb, Lines[j] + 8f * (j < N / 2 ? 1 : -1));
                    spawns.Add(p); patrol.Add(p);
                }

            // The start: a sheltered piece of outer sidewalk, with barriers in front, facing into the city.
            Vector3 s = PlayerStartPos;
            Prop("barrier", s.x - 3f, s.z + 3.2f, 90f, Pick(ConcreteTints), 1);
            Prop("barrier", s.x + 3f, s.z + 3.2f, 90f, Pick(ConcreteTints), 1);
            Prop("dumpster", s.x - 7f, s.z + .4f, 90f, Pick(MetalTints), 1);
        }

        static readonly Vector3 PlayerStartPos = new Vector3(0f, Curb + 0.05f, Lines[0] - RoadHalf - 2.4f);

        void Street(Vector3 a, Vector3 b)
        {
            Vector3 d = (b - a).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, d);
            float len = Vector3.Distance(a, b);
            float yawAlong = Quaternion.LookRotation(d).eulerAngles.y;
            foreach (var s in new[] { -1f, 1f })
            {
                // Parked cars and barriers keep to the parking strip, so the lanes stay free for traffic.
                float t = R(9f, 14f);
                while (t < len - 9f)
                {
                    Vector3 p = a + d * t + side * (s * ParkOffset);
                    float pick = (p - PlayerStartPos).Flat().magnitude < 10f ? 1f : R(0, 1);   // keep the start clear
                    if (HasZebra(a, b) && Mathf.Abs(t - len * .5f) < 7f) pick = 1f;              // and keep zebra crossings visible
                    if (pick < 0.32f)
                    {
                        PlaceCar(p.x, p.z, yawAlong + (s > 0 ? 0f : 180f) + R(-1.5f, 1.5f));
                        t += R(6.5f, 13f);
                    }
                    else if (pick < 0.6f)
                    {
                        Prop("barrier", p.x, p.z, yawAlong + R(-3, 3), Pick(ConcreteTints), 1);
                        t += R(4f, 9f);
                    }
                    else t += R(5f, 10f);
                }

                // Sidewalk: lamps with their arm over the road, trees between.
                Vector3 edge = side * (s * (RoadHalf + 0.8f));
                float lampYaw = Quaternion.LookRotation(-side * s).eulerAngles.y;
                float tl = s > 0 ? 8f : 20f;
                while (tl < len - 5f)
                {
                    Vector3 p = a + d * tl + edge;
                    if (OutOfBounds(p) == false) Lamp(p.x, p.z, lampYaw);
                    tl += 24f;
                }
                float tt = s > 0 ? 20f : 8f;
                while (tt < len - 5f)
                {
                    Vector3 p = a + d * tt + side * (s * (RoadHalf + 1.6f));
                    if (R(0, 1) < .8f && !OutOfBounds(p)) Tree(p.x, p.z);
                    tt += 24f;
                }
                Vector3 bin = a + d * R(6f, len - 6f) + side * (s * (RoadHalf + 3f));
                if (R(0, 1) < .6f && !OutOfBounds(bin)) Prop("trashcan", bin.x, bin.z, R(0, 360), Pick(MetalTints), 3);
                Vector3 seat = a + d * R(8f, len - 8f) + side * (s * (RoadHalf + 3.2f));
                if (R(0, 1) < .35f && !OutOfBounds(seat)) Prop("bench", seat.x, seat.z, yawAlong + (s > 0 ? -90f : 90f), Color.white, 1);
            }
        }

        static bool OutOfBounds(Vector3 p) => Mathf.Abs(p.x) > Bound - 1f || Mathf.Abs(p.z) > Bound - 1f;

        // ---- outside ----

        void BuildBoundary()
        {
            Color t = HedgeTints[1];
            float b = Bound;
            var corners = new[] { new Vector3(-b, 0, -b), new Vector3(b, 0, -b), new Vector3(b, 0, b), new Vector3(-b, 0, b) };
            for (int i = 0; i < 4; i++)
            {
                // Hedge pieces, cut to vary colour.
                Vector3 a = corners[i], c = corners[(i + 1) % 4];
                int pieces = 16;
                for (int p = 0; p < pieces; p++)
                    Hedge(Vector3.Lerp(a, c, p / (float)pieces), Vector3.Lerp(a, c, (p + 1) / (float)pieces), 3.2f, 1.3f, Vary(t, .12f));
                // The real wall: tall and invisible.
                Vector3 mid = (a + c) / 2 + Vector3.up * 15f;
                bool alongX = Mathf.Abs(a.x - c.x) > 1f;
                Box(mid, alongX ? new Vector3(2 * b, 30f, 1.3f) : new Vector3(1.3f, 30f, 2 * b), 0f, true);
            }
        }

        void BuildSkyline()
        {
            // Behind the hedge: a ring of towers that are only ever seen from afar.
            A(concrete, new Vector3(-300, 0, 0)).Flat(-720, -720, 720, 720, -0.05f, 6f, C(.45f, .45f, .45f));
            for (int ix = -11; ix <= 11; ix++)
                for (int iz = -11; iz <= 11; iz++)
                {
                    float cx = ix * 44f + R(-3f, 3f), cz = iz * 44f + R(-3f, 3f);
                    if (Mathf.Abs(cx) < Bound + 30f && Mathf.Abs(cz) < Bound + 30f) continue;
                    float wx = R(14f, 30f), wz = R(14f, 30f);
                    float dist = Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cz));
                    float height = Mathf.Round(R(34f, 80f + (dist - 240f) * 0.45f) / 4f) * 4f;
                    SkylineTower(cx - wx / 2, cz - wz / 2, cx + wx / 2, cz + wz / 2, height);
                }
        }

        void SkylineTower(float x0, float z0, float x1, float z1, float height)
        {
            bool glass = R(0, 1) < .6f;
            var wall = glass ? facadeGlass : Pick(facadeWall);
            Color tint = Vary(glass ? Pick(GlassTints) : Pick(WallTints), .08f);
            tint.a = R(0f, 1f);
            float tileW = glass ? 30f : 12f, tileH = glass ? 30f : 12f;
            var pos = new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2);
            void Face(Vector3 a, Vector3 b)
            {
                float w = Vector3.Distance(a, b);
                A(wall, pos).Wall(a, b, 0, height, Mathf.Max(1f, Mathf.Round(w / tileW)), 0, Mathf.Max(1f, Mathf.Round(height / tileH)), tint);
            }
            Vector3 p00 = new(x0, 0, z0), p10 = new(x1, 0, z0), p11 = new(x1, 0, z1), p01 = new(x0, 0, z1);
            Face(p00, p10); Face(p10, p11); Face(p11, p01); Face(p01, p00);
            A(roof, pos).Flat(x0, z0, x1, z1, height, 9f, Color.white);
        }

        // ---- navigation and data ----

        void BakeNavMeshAndData()
        {
            var surfGo = new GameObject("NavMesh");
            surfGo.transform.SetParent(root, false);
            var surf = surfGo.AddComponent<NavMeshSurface>();
            surf.collectObjects = CollectObjects.All;
            surf.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surf.layerMask = Layers.WorldMask;
            surf.overrideVoxelSize = true; surf.voxelSize = 0.11f;
            surf.overrideTileSize = true; surf.tileSize = 256;
            var settings = NavMesh.GetSettingsByID(surf.agentTypeID);
            Debug.Log($"[BUILD] agent radius {settings.agentRadius} height {settings.agentHeight} climb {settings.agentClimb}");
            surf.BuildNavMesh();
            Art.EnsureFolder($"{Art.Gen}/City");
            AssetDatabase.CreateAsset(surf.navMeshData, $"{Art.Gen}/City/navmesh.asset");
            NavMesh.RemoveAllNavMeshData();
            surf.AddData();

            Vector3 start = PlayerStartPos;
            bool startOk = NavMesh.SamplePosition(start, out var startHit, 3f, NavMesh.AllAreas);
            if (!startOk) Debug.LogError("[BUILD] player start is off the navmesh");
            Vector3 anchor = startHit.position;

            bool Reachable(Vector3 p, out Vector3 snapped)
            {
                snapped = p;
                if (!NavMesh.SamplePosition(p, out var h, 2.5f, NavMesh.AllAreas)) return false;
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(anchor, h.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) return false;
                snapped = h.position;
                return true;
            }

            var goodCover = new List<CoverPoint>();
            foreach (var c in cover)
            {
                if (OutOfBounds(c.Position)) continue;
                if (!NavMesh.SamplePosition(c.Position, out var h, 0.5f, NavMesh.AllAreas)) continue;
                if ((h.position - c.Position).Flat().magnitude > 0.3f) continue;
                if (!Reachable(h.position, out var s)) continue;
                goodCover.Add(new CoverPoint { Position = s, Normal = c.Normal, Low = c.Low });
            }
            var goodSpawns = spawns.Select(p => (ok: Reachable(p, out var s), s)).Where(t => t.ok).Select(t => t.s).ToArray();
            var goodPatrol = patrol.Select(p => (ok: Reachable(p, out var s), s)).Where(t => t.ok).Select(t => t.s).ToArray();

            var data = root.gameObject.AddComponent<CityData>();
            data.PlayerStart = PlayerStartPos;
            data.PlayerStartYaw = 0f;
            data.Cover = goodCover.ToArray();
            data.EnemySpawns = goodSpawns;
            data.PatrolPoints = goodPatrol;
            data.Lamps = lamps.ToArray();
            var net = root.GetComponent<RoadNetwork>();
            net.Crossings = crossings.ToArray();
            Debug.Log($"[BUILD] {crossings.Count} mid-block zebra crossings");
            Debug.Log($"[BUILD] nav: cover {goodCover.Count}/{cover.Count} spawns {goodSpawns.Length}/{spawns.Count} patrol {goodPatrol.Length}/{patrol.Count}");
            if (goodCover.Count < 100 || goodSpawns.Length < 8) Debug.LogError("[BUILD] too few reachable cover points or spawns");
        }
    }
}
