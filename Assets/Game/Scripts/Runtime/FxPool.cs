using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// Every short-lived effect: muzzle flash, bullet holes (gone after a second), impact smoke and sparks, all the blood,
    /// and the fire and explosions of cars. Textures come from LAGP; materials are assigned by the builder.
    /// </summary>
    public class FxPool : MonoBehaviour
    {
        public static FxPool Instance { get; private set; }

        public Material flashMat, smokeMat, bloodMat, holeMat, fireMat, explosionMat, burnSmokeMat, exhaustMat, sparkMat;
        public Material[] splatMats;
        public Material poolMat, chunkMat, scorchMat, skidMat;
        readonly List<Transform> live = new();
        public float HoleLifetime = 1f;
        public float SplatLifetime = 60f;

        ParticleSystem smoke, bloodMist, bloodDrops, bloodJets, sparks, blast, blastSmoke;
        class Timed { public Transform T; public float End, Life; }
        readonly List<Timed> timed = new();
        /// <summary>Road markings lie 2.5 cm above the asphalt: everything spilled or scorched on the road lies above them.</summary>
        const float AboveMarkings = 0.03f;
        const float DecalFadeSeconds = 2f, HoleFadeSeconds = 0.35f;
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        MaterialPropertyBlock decalBlock;

        void SetDecalFade(Transform t, float amount)
        {
            decalBlock ??= new MaterialPropertyBlock();
            var r = t.GetComponent<Renderer>();
            r.GetPropertyBlock(decalBlock);
            decalBlock.SetFloat(FadeId, amount);
            r.SetPropertyBlock(decalBlock);
        }

        /// <summary>Fades in the last moments of its life; true when it is over.</summary>
        bool Age(Timed d, float now, float fadeSeconds)
        {
            float left = d.End - now;
            if (left <= 0f) return true;
            if (left < fadeSeconds) SetDecalFade(d.T, left / fadeSeconds);
            return false;
        }
        readonly List<Timed> holes = new();
        int nextHole;
        readonly List<Timed> splats = new();
        int nextSplat;
        readonly List<(Transform t, Light l, float end)> flashes = new();
        readonly Stack<(Transform t, Light l)> freeFlash = new();
        readonly List<(Transform t, float start, float dur, float size)> growing = new();
        readonly List<(Rigidbody rb, float end)> chunksLive = new();
        readonly Queue<Rigidbody> chunksFree = new();
        Mesh quad, chunkMesh;
        Light blastLight;
        float blastLightEnd, lastNight = -1f;
        Color burnBase, smokeBase, exhaustBase;

        void Awake()
        {
            Instance = this;
            quad = MakeQuad();
            burnBase = burnSmokeMat.GetColor("_BaseColor"); smokeBase = smokeMat.GetColor("_BaseColor"); exhaustBase = exhaustMat.GetColor("_BaseColor");
            smoke = MakeBurst("ImpactSmoke", smokeMat, 0.35f, 1.1f, 1.4f, 0.02f, 300);
            bloodMist = MakeBurst("BloodMist", bloodMat, 0.55f, 0.65f, 1.6f, 0.15f, 1500);
            bloodDrops = MakeBurst("BloodDrops", bloodMat, 0.09f, 0.9f, 6.5f, 1.3f, 3000);
            bloodJets = MakeBurst("BloodJets", bloodMat, 0.2f, 0.5f, 10f, 0.9f, 1000);
            sparks = MakeBurst("Sparks", sparkMat, 0.05f, 0.45f, 9f, 1.1f, 500);
            blast = MakeBurst("Blast", explosionMat, 5.5f, 0.75f, 7f, -0.1f, 120);
            blastSmoke = MakeBurst("BlastSmoke", burnSmokeMat, 3.6f, 4.5f, 5f, -0.06f, 150);

            for (int i = 0; i < 80; i++) holes.Add(new Timed { T = MakeDecal("Hole", holeMat) });
            for (int i = 0; i < 130; i++) splats.Add(new Timed { T = MakeDecal("Splat", splatMats[i % splatMats.Length]) });

            chunkMesh = MakeChunkMesh();
            for (int i = 0; i < 48; i++)
            {
                var go = new GameObject("Chunk") { layer = Layers.Debris };
                go.transform.SetParent(transform);
                go.AddComponent<MeshFilter>().sharedMesh = chunkMesh;
                var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = chunkMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.AddComponent<SphereCollider>().radius = 0.5f;
                var rb = go.AddComponent<Rigidbody>(); rb.mass = 0.4f; rb.linearDamping = 0.2f;
                go.SetActive(false);
                chunksFree.Enqueue(rb);
            }

            gameObject.AddComponent<SkidMarks>().Setup(skidMat);

            var lightGo = new GameObject("BlastLight");
            lightGo.transform.SetParent(transform);
            blastLight = lightGo.AddComponent<Light>();
            blastLight.type = LightType.Point; blastLight.range = 50f; blastLight.color = new Color(1f, .6f, .25f);
            blastLight.shadows = LightShadows.None; blastLight.enabled = false;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        Transform MakeDecal(string name, Material m)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go.transform;
        }

        static Mesh MakeQuad()
        {
            var m = new Mesh { name = "FxQuad" };
            m.vertices = new[] { new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f), new Vector3(.5f, .5f), new Vector3(-.5f, .5f) };
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Mesh MakeChunkMesh()
        {
            // A lumpy little blob for gore and debris.
            var v = new List<Vector3>(); var t = new List<int>();
            for (int y = 0; y <= 4; y++)
                for (int x = 0; x < 6; x++)
                {
                    float a = x / 6f * Mathf.PI * 2f, b = y / 4f * Mathf.PI;
                    float r = 0.42f + 0.12f * Mathf.Sin(a * 2f + b * 3f);
                    v.Add(new Vector3(Mathf.Cos(a) * Mathf.Sin(b), Mathf.Cos(b) * .8f, Mathf.Sin(a) * Mathf.Sin(b)) * r);
                }
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 6; x++)
                {
                    int a = y * 6 + x, b = y * 6 + (x + 1) % 6, c = a + 6, d = b + 6;
                    t.Add(a); t.Add(b); t.Add(c); t.Add(b); t.Add(d); t.Add(c);
                }
            var m = new Mesh { name = "Chunk" };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        ParticleSystem MakeBurst(string name, Material mat, float size, float life, float speed, float gravity, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureParticles(p, mat, size, life, speed, gravity, max, ParticleSystemSimulationSpace.World);
            var em = p.emission; em.enabled = false;
            return p;
        }

        static void ConfigureParticles(ParticleSystem p, Material mat, float size, float life, float speed, float gravity, int max, ParticleSystemSimulationSpace space)
        {
            var main = p.main;
            main.loop = false; main.playOnAwake = false; main.maxParticles = max;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * .6f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * .3f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(size * .6f, size);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.gravityModifier = gravity;
            main.simulationSpace = space;
            var shape = p.shape; shape.enabled = false;
            var color = p.colorOverLifetime; color.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, .6f), new GradientAlphaKey(0, 1) });
            color.color = g;
            var sz = p.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, .6f, 1, 1.6f));
            var r = p.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static void Emit(ParticleSystem p, Vector3 pos, Vector3 dir, int count, float cone, float speedScale = 1f)
        {
            var e = new ParticleSystem.EmitParams { position = pos, applyShapeToPosition = false };
            var sp = p.main.startSpeed;
            for (int i = 0; i < count; i++)
            {
                e.velocity = (dir + Random.insideUnitSphere * cone).normalized * (Random.Range(sp.constantMin, sp.constantMax) * speedScale);
                p.Emit(e, 1);
            }
        }

        // ---- bullets ----

        public void Impact(Vector3 pos, Vector3 normal)
        {
            Emit(smoke, pos + normal * 0.03f, normal, 5, 0.7f);
            float size = Surface.Fit(pos, normal, Random.Range(0.07f, 0.11f));
            if (size <= 0f) return;
            var hole = holes[nextHole]; nextHole = (nextHole + 1) % holes.Count;
            hole.T.gameObject.SetActive(true);
            hole.T.position = pos + normal * AboveMarkings;
            SetDecalFade(hole.T, 1f);
            hole.Life = HoleLifetime;
            hole.T.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0, 360f));
            hole.T.localScale = Vector3.one * size;
            hole.End = Time.time + HoleLifetime;
        }

        public void Sparks(Vector3 pos, Vector3 normal) => Emit(sparks, pos, normal, 18, 0.9f);

        public void VehicleHit(Vector3 pos, Vector3 normal)
        {
            Emit(sparks, pos, normal, 10, 0.7f);
            Emit(smoke, pos, normal, 3, 0.6f);
        }

        public void MuzzleFlash(Vector3 pos, Vector3 forward)
        {
            (Transform t, Light l) f;
            if (freeFlash.Count > 0) f = freeFlash.Pop();
            else
            {
                var go = new GameObject("Flash");
                go.transform.SetParent(transform);
                var cross = new GameObject("Quad");
                cross.transform.SetParent(go.transform, false);
                cross.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = cross.AddComponent<MeshRenderer>();
                r.sharedMaterial = flashMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var light = go.AddComponent<Light>();
                light.type = LightType.Point; light.range = 8f; light.intensity = 9f;
                light.color = new Color(1f, .72f, .4f); light.shadows = LightShadows.None;
                f = (go.transform, light);
            }
            f.t.gameObject.SetActive(true);
            f.t.SetPositionAndRotation(pos, Quaternion.LookRotation(-forward) * Quaternion.Euler(0, 0, Random.Range(0, 360f)));
            f.t.localScale = Vector3.one * Random.Range(0.28f, 0.42f);
            flashes.Add((f.t, f.l, Time.time + 0.045f));
        }

        // ---- blood ----

        /// <summary>A hit. Power 1 is a body shot; more is a head shot or being run over.</summary>
        public void Blood(Vector3 pos, Vector3 exitDir, Vector3 entryDir, float power)
        {
            int mist = Mathf.RoundToInt(10 * power), drops = Mathf.RoundToInt(45 * power), jets = Mathf.RoundToInt(12 * power);
            Emit(bloodMist, pos, Random.onUnitSphere, mist, 1.2f);
            Emit(bloodDrops, pos, exitDir + Vector3.up * .25f, drops, 0.65f);
            Emit(bloodDrops, pos, entryDir, drops / 2, 0.45f, 1.5f);
            Emit(bloodJets, pos, entryDir + Vector3.up * .1f, jets, 0.25f, Mathf.Clamp(power, 1f, 1.6f));
        }

        /// <summary>Blood thrown through the victim paints the wall behind, and always the ground below.</summary>
        public void BloodOnSurfaces(Vector3 pos, Vector3 dir, float power)
        {
            if (Physics.Raycast(pos, dir, out var wall, 7f, Layers.WorldMask, QueryTriggerInteraction.Ignore) && Surface.Paintable(wall.collider))
                BloodSplat(wall.point, wall.normal, Mathf.Lerp(1.5f, .7f, wall.distance / 7f) * Mathf.Min(power, 2f));
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = pos + Random.insideUnitSphere.normalized * Random.Range(0.3f, 1.8f);
                p.y = pos.y;
                if (Surface.FindPaintable(p, Vector3.down, 4f, out var g))
                    BloodSplat(g.point, g.normal, Random.Range(0.5f, 1.05f) * Mathf.Min(power, 2f));
            }
        }

        public void BloodSplat(Vector3 pos, Vector3 normal, float size)
        {
            size = Surface.Fit(pos, normal, size);
            if (size <= 0f) return;
            var s = splats[nextSplat]; nextSplat = (nextSplat + 1) % splats.Count;
            s.T.gameObject.SetActive(true);
            SetDecalFade(s.T, 1f);
            s.Life = SplatLifetime;
            s.T.SetPositionAndRotation(pos + normal * (AboveMarkings + 0.002f + nextSplat * 0.00003f), Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0, 360f)));
            s.T.localScale = Vector3.one * size;
            s.End = Time.time + SplatLifetime;
        }

        /// <summary>A pool that spreads slowly under a corpse.</summary>
        public void BloodPool(Vector3 ground)
        {
            // On the surface under this point; a pool only spreads as far as that surface goes.
            if (!Surface.FindPaintable(ground + Vector3.up * 0.3f, Vector3.down, 1.5f, out var under)) return;
            float size = Surface.Fit(under.point, under.normal, Random.Range(1.7f, 2.7f));
            if (size < 0.35f) return;
            ground = under.point;
            var go = new GameObject("BloodPool");
            go.transform.SetParent(transform);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = poolMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.SetPositionAndRotation(ground + Vector3.up * (AboveMarkings + 0.001f), Quaternion.Euler(90f, Random.Range(0, 360f), 0));
            go.transform.localScale = Vector3.one * 0.2f;
            growing.Add((go.transform, Time.time, 5f, size));
            live.Add(go.transform);
            timed.Add(new Timed { T = go.transform, End = Time.time + 45f, Life = 45f });
        }

        /// <summary>Lumps of the victim, flying.</summary>
        public void Chunks(Vector3 pos, Vector3 dir, int count, float speed)
        {
            for (int i = 0; i < count && chunksFree.Count > 0; i++)
            {
                var rb = chunksFree.Dequeue();
                rb.gameObject.SetActive(true);
                rb.transform.position = pos + Random.insideUnitSphere * 0.2f;
                rb.transform.localScale = Vector3.one * Random.Range(0.05f, 0.16f);
                rb.linearVelocity = (dir + Random.insideUnitSphere * 0.9f).normalized * Random.Range(speed * .4f, speed);
                rb.angularVelocity = Random.insideUnitSphere * 12f;
                chunksLive.Add((rb, Time.time + Random.Range(5f, 9f)));
            }
        }

        // ---- fire and explosions ----

        public ParticleSystem MakeFire(Transform parent, Vector3 local, float scale)
        {
            var go = new GameObject("Fire");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureParticles(p, fireMat, 1.3f * scale, 0.8f, 2.8f, -0.12f, 200, ParticleSystemSimulationSpace.World);
            var main = p.main; main.loop = true;
            var em = p.emission; em.enabled = true; em.rateOverTime = 42f * scale;
            var sh = p.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(1.2f, 0.3f, 1.2f) * scale;
            p.Play();
            var lightGo = new GameObject("FireLight");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localPosition = Vector3.up * 0.8f;
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Point; l.range = 12f * scale; l.intensity = 14f; l.color = new Color(1f, .55f, .2f); l.shadows = LightShadows.None;
            lightGo.AddComponent<FireFlicker>();
            return p;
        }

        public ParticleSystem MakeBurnSmoke(Transform parent, Vector3 local)
        {
            var go = new GameObject("BurnSmoke");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureParticles(p, burnSmokeMat, 3.6f, 5f, 3.2f, -0.05f, 240, ParticleSystemSimulationSpace.World);
            var main = p.main; main.loop = true;
            var em = p.emission; em.enabled = true; em.rateOverTime = 26f;
            var sh = p.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 15f; sh.radius = 0.6f;
            sh.rotation = new Vector3(-90f, 0, 0);
            p.Play();
            return p;
        }

        public ParticleSystem MakeExhaust(Transform parent, Vector3 local)
        {
            var go = new GameObject("Exhaust");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.transform.localRotation = Quaternion.LookRotation(Vector3.back);
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureParticles(p, exhaustMat, 0.75f, 1.5f, 1.6f, -0.02f, 200, ParticleSystemSimulationSpace.World);
            var main = p.main; main.loop = true;
            var em = p.emission; em.enabled = true; em.rateOverTime = 20f;
            var sh = p.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 12f; sh.radius = 0.04f;
            p.Play();
            return p;
        }

        public void Explosion(Vector3 pos)
        {
            var e = new ParticleSystem.EmitParams { position = pos, applyShapeToPosition = false };
            for (int i = 0; i < 26; i++)
            {
                e.position = pos + Random.insideUnitSphere * 1.6f;
                e.velocity = Random.onUnitSphere * Random.Range(2f, 9f) + Vector3.up * 3f;
                blast.Emit(e, 1);
            }
            for (int i = 0; i < 26; i++)
            {
                e.position = pos + Random.insideUnitSphere * 2f + Vector3.up;
                e.velocity = Random.onUnitSphere * Random.Range(1f, 4f) + Vector3.up * Random.Range(2f, 6f);
                blastSmoke.Emit(e, 1);
            }
            Emit(sparks, pos, Vector3.up, 120, 1.3f, 1.7f);
            if (Surface.FindPaintable(pos + Vector3.up * .5f, Vector3.down, 4f, out var g))
                Scorch(g.point, g.normal);
            blastLight.transform.position = pos + Vector3.up * 2f;
            blastLight.intensity = 110f;
            blastLight.enabled = true;
            blastLightEnd = Time.time + 0.55f;
        }

        void Scorch(Vector3 pos, Vector3 normal)
        {
            float size = Surface.Fit(pos, normal, 9f);
            if (size <= 0f) return;
            var go = new GameObject("Scorch");
            go.transform.SetParent(transform);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = scorchMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.SetPositionAndRotation(pos + normal * AboveMarkings, Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0, 360f)));
            go.transform.localScale = Vector3.one * size;
            live.Add(go.transform);
            timed.Add(new Timed { T = go.transform, End = Time.time + 90f, Life = 90f });
        }

        /// <summary>
        /// Decals showing now and how many have any part of them off their surface: the automated test. Their edges are checked at
        /// 40 % of the size, the blot inside the texture; a corner of a square quad is transparent anyway.
        /// </summary>
        public int CountFloating(out int count)
        {
            int bad = 0, total = 0;
            void Check(Transform t)
            {
                total++;
                Vector3 n = -t.forward;
                float r = t.localScale.x * 0.4f;
                foreach (var d in new[] { Vector3.zero, t.right, -t.right, t.up, -t.up })
                {
                    Vector3 q = t.position + d * r;
                    if (!Surface.FindPaintable(q + n * 0.2f, -n, 0.3f, out var hit) || Mathf.Abs(Vector3.Dot(hit.point - t.position, n)) > 0.06f) { bad++; return; }
                }
            }
            foreach (var h in holes) if (h.T.gameObject.activeSelf) Check(h.T);
            foreach (var s in splats) if (s.T.gameObject.activeSelf) Check(s.T);
            live.RemoveAll(t => t == null);
            foreach (var t in live) Check(t);
            count = total;
            return bad;
        }

        public void TyreSmoke(Vector3 pos) => Emit(smoke, pos, Vector3.up, 1, 0.5f, 0.5f);

        void Update()
        {
            float now = Time.time;
            // Particles are unlit: dim them with the daylight.
            float night = DayNightCycle.NightAmount;
            if (Mathf.Abs(night - lastNight) > 0.01f)
            {
                lastNight = night;
                Color dim = Color.Lerp(Color.white, new Color(.3f, .3f, .36f, 1f), night);
                bloodMat.SetColor("_BaseColor", dim);
                burnSmokeMat.SetColor("_BaseColor", burnBase * dim);
                smokeMat.SetColor("_BaseColor", smokeBase * dim);
                exhaustMat.SetColor("_BaseColor", exhaustBase * dim);
            }
            for (int i = flashes.Count - 1; i >= 0; i--)
                if (now >= flashes[i].end)
                {
                    flashes[i].t.gameObject.SetActive(false);
                    freeFlash.Push((flashes[i].t, flashes[i].l));
                    flashes.RemoveAt(i);
                }
            foreach (var h in holes)
                if (h.T.gameObject.activeSelf && Age(h, now, HoleFadeSeconds)) h.T.gameObject.SetActive(false);
            foreach (var s in splats)
                if (s.T.gameObject.activeSelf && Age(s, now, DecalFadeSeconds)) s.T.gameObject.SetActive(false);
            for (int i = timed.Count - 1; i >= 0; i--)
                if (timed[i].T == null) timed.RemoveAt(i);
                else if (Age(timed[i], now, DecalFadeSeconds)) { Destroy(timed[i].T.gameObject); timed.RemoveAt(i); }
            for (int i = growing.Count - 1; i >= 0; i--)
            {
                var g = growing[i];
                if (g.t == null) { growing.RemoveAt(i); continue; }
                float k = Mathf.Clamp01((now - g.start) / g.dur);
                g.t.localScale = Vector3.one * Mathf.Lerp(0.2f, g.size, Mathf.Sqrt(k));
                if (k >= 1f) growing.RemoveAt(i);
            }
            for (int i = chunksLive.Count - 1; i >= 0; i--)
            {
                // Lumps shrink away in their last half second: too small and too quick to need more.
                float chunkLeft = chunksLive[i].end - now;
                if (chunkLeft > 0f && chunkLeft < 0.5f) chunksLive[i].rb.transform.localScale *= Mathf.Pow(0.002f, Time.deltaTime);
                if (now >= chunksLive[i].end)
                {
                    chunksLive[i].rb.gameObject.SetActive(false);
                    chunksFree.Enqueue(chunksLive[i].rb);
                    chunksLive.RemoveAt(i);
                }
            }
            if (blastLight.enabled)
            {
                blastLight.intensity = Mathf.MoveTowards(blastLight.intensity, 0f, Time.deltaTime * 200f);
                if (now >= blastLightEnd) blastLight.enabled = false;
            }
        }
    }
}
