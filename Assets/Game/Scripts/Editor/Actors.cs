using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>Animator controllers, weapon assets and the character prefabs.</summary>
    public static class Actors
    {
        public class Gun
        {
            public string Id, DisplayName;
            public WeaponKind Kind;
            public float Length, RateOfFire, Spread, Recoil, Reload, Speed;
            public bool Automatic, FlipZ;
            public int Mag;
            // fractions of the gun's box, measured on its concept image: 0 = rear / bottom, 1 = muzzle / top
            public float GripAlong, GripUp, SupportAlong, SupportUp, MuzzleUp;
        }

        public static readonly Gun[] Guns =
        {
            new Gun { Id="pistol_a", DisplayName="P9 Compact", Kind=WeaponKind.Pistol, Length=0.19f, RateOfFire=420, Automatic=false, Mag=15, Reload=1.3f, Spread=0.45f, Recoil=1.3f, Speed=240,
                GripAlong=0.18f, GripUp=0.42f, SupportAlong=0.28f, SupportUp=0.42f, MuzzleUp=0.85f },
            new Gun { Id="pistol_b", DisplayName="Magnum .50", Kind=WeaponKind.Pistol, Length=0.24f, RateOfFire=170, Automatic=false, Mag=7, Reload=1.8f, Spread=0.25f, Recoil=3.4f, Speed=300,
                GripAlong=0.17f, GripUp=0.42f, SupportAlong=0.28f, SupportUp=0.42f, MuzzleUp=0.85f },
            new Gun { Id="rifle_a", DisplayName="M4 Carbine", Kind=WeaponKind.Rifle, Length=0.88f, RateOfFire=720, Automatic=true, Mag=30, Reload=2.1f, Spread=0.5f, Recoil=0.55f, Speed=420,
                GripAlong=0.33f, GripUp=0.28f, SupportAlong=0.57f, SupportUp=0.55f, MuzzleUp=0.62f },
            new Gun { Id="rifle_b", DisplayName="AK Classic", Kind=WeaponKind.Rifle, Length=0.92f, RateOfFire=560, Automatic=true, Mag=30, Reload=2.4f, Spread=0.75f, Recoil=0.9f, Speed=400,
                GripAlong=0.33f, GripUp=0.28f, SupportAlong=0.58f, SupportUp=0.62f, MuzzleUp=0.7f },
        };

        public static WeaponData[] BuildWeapons()
        {
            Art.EnsureFolder($"{Art.Gen}/Weapons");
            var result = new List<WeaponData>();
            foreach (var g in Guns)
            {
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{Art.Root}/Props/{g.Id}.fbx");
                var mat = Art.ModelMat("Props", g.Id, false, g.Kind == WeaponKind.Pistol ? 0.5f : 0.4f, 0.45f);

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                foreach (var r in inst.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;
                string prefabPath = $"{Art.Gen}/Weapons/{g.Id}.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
                // Long side of the box = along the barrel. The FBX root's own rotation holds the Blender-to-Unity axis change: keep it.
                Quaternion fbxRot = fbx.transform.localRotation;
                inst.transform.SetPositionAndRotation(Vector3.zero, fbxRot);
                inst.transform.localScale = Vector3.one;
                var b0 = Bounds(inst);
                bool longX = b0.size.x > b0.size.z;
                float yaw = (longX ? (g.FlipZ ? -90f : 90f) : (g.FlipZ ? 180f : 0f));
                float scale = g.Length / Mathf.Max(b0.size.x, b0.size.z);
                Quaternion modelRot = Quaternion.Euler(0, yaw, 0) * fbxRot;
                inst.transform.SetPositionAndRotation(Vector3.zero, modelRot);
                inst.transform.localScale = Vector3.one * scale;
                var b = Bounds(inst);
                Vector3 P(float along, float up) => new(b.center.x, b.min.y + b.size.y * up, b.min.z + b.size.z * along);

                string path = $"{Art.Gen}/Weapons/{g.Id}.asset";
                var w = AssetDatabase.LoadAssetAtPath<WeaponData>(path);
                if (w == null) { w = ScriptableObject.CreateInstance<WeaponData>(); AssetDatabase.CreateAsset(w, path); }
                w.DisplayName = g.DisplayName; w.Kind = g.Kind; w.Model = prefab;
                w.RoundsPerMinute = g.RateOfFire; w.Automatic = g.Automatic; w.MagazineSize = g.Mag;
                w.ReloadSeconds = g.Reload; w.SpreadDegrees = g.Spread; w.BulletSpeed = g.Speed; w.RecoilDegrees = g.Recoil;
                w.ModelEuler = modelRot.eulerAngles; w.ModelScale = scale;
                w.GripPoint = P(g.GripAlong, g.GripUp);
                w.SupportPoint = P(g.SupportAlong, g.SupportUp);
                w.MuzzlePoint = P(1.01f, g.MuzzleUp);
                w.SightHeight = b.max.y - w.GripPoint.y;
                w.ShotClips = Art.Clips(g.Id);
                EditorUtility.SetDirty(w);
                Debug.Log($"[BUILD] gun {g.Id}: box {b.size} scale {scale:F3} yaw {yaw}");
                Object.DestroyImmediate(inst);
                result.Add(w);
            }
            return result.ToArray();
        }

        /// <summary>The thrown grenade: the generated model on a small rolling body, on the debris layer like a dropped gun.</summary>
        public static GameObject BuildGrenadePrefab()
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{Art.Root}/Props/grenade.fbx");
            if (fbx == null) { Debug.LogError("[BUILD] no grenade model"); return null; }
            var root = new GameObject("Grenade");
            root.layer = Layers.Debris;
            var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            model.transform.SetParent(root.transform, false);
            var mat = Art.ModelMat("Props", "grenade", false, 0.35f, 0.3f);
            foreach (var r in model.GetComponentsInChildren<Renderer>()) { r.sharedMaterial = mat; r.gameObject.layer = Layers.Debris; }
            // Centre the model on the body's origin, so it spins about its middle.
            var b = Bounds(model);
            model.transform.position -= b.center;
            var col = root.AddComponent<SphereCollider>();
            col.radius = Mathf.Max(0.035f, Mathf.Min(b.size.x, b.size.z) * 0.5f);
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 0.6f; rb.linearDamping = 0.1f; rb.angularDamping = 1.5f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            root.AddComponent<Grenade>();
            Art.EnsureFolder($"{Art.Gen}/Prefabs");
            Debug.Log($"[BUILD] grenade: {b.size.y:F3} m tall, collider radius {col.radius:F3}");
            var p = PrefabUtility.SaveAsPrefabAsset(root, $"{Art.Gen}/Prefabs/Grenade.prefab");
            Object.DestroyImmediate(root);
            return p;
        }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static float TargetHeight(string model) => model is "enemy_c" or "ped_b" ? 1.7f : model == "enemy_b" ? 1.84f : 1.8f;

        /// <summary>The scale that brings a model's mesh to its body height.</summary>
        public static float BodyScale(string model, GameObject fbx)
        {
            var inst = (GameObject)Object.Instantiate(fbx);
            float h = 0f;
            foreach (var r in inst.GetComponentsInChildren<SkinnedMeshRenderer>()) h = r.bounds.size.y;
            Object.DestroyImmediate(inst);
            return TargetHeight(model) / Mathf.Max(h, 0.1f);
        }

        /// <summary>
        /// The ground speed a walking or running clip is made for: how fast a foot travels backwards while it is on the ground, along the
        /// way the model faces (its toes). A planted foot moves backwards relative to the body at exactly the body's speed.
        /// </summary>
        /// <param name="anyDirection">Measure the planted foot's speed whichever way it slides (strafing and backwards clips), not just backwards along the toes.</param>
        /// <summary>
        /// How far (degrees) the upper arms must swing out from the body so the hands and forearms clear the torso and thighs by
        /// <paramref name="clearance"/> metres. Library clips are made for a slim body: on a broad one (a vest, big arms) arms that hang
        /// straight down sink into it. Measured on the skinned mesh through the given clips; 0 when the arms already clear.
        /// </summary>
        public static float ArmSpread(GameObject fbx, AnimationClip[] clips, float scale, float clearance = 0f)
        {
            var inst = (GameObject)Object.Instantiate(fbx);
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
            var map = inst.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            var names = smr.bones.Select(b => b.name).ToArray();
            var dominant = smr.sharedMesh.boneWeights.Select(w => names[w.boneIndex0]).ToArray();
            string[] bodyBones = { "Hips", "Spine", "Spine1", "Spine2", "LeftUpLeg", "RightUpLeg" };
            bool IsBody(string n) => System.Array.IndexOf(bodyBones, n) >= 0;
            float clear = clearance / Mathf.Max(scale, 1e-4f);
            var needs = new List<float>();
            var baked = new Mesh();
            foreach (var clip in clips.Where(c => c != null))
                for (int k = 0; k < 6; k++)
                {
                    clip.SampleAnimation(inst, clip.length * k / 6f);
                    smr.BakeMesh(baked, true);
                    var v = baked.vertices;
                    var tr = smr.transform;
                    Vector3 hips = map["Hips"].position;
                    Vector3 right = Vector3.ProjectOnPlane(map["RightUpLeg"].position - map["LeftUpLeg"].position, Vector3.up).normalized;
                    Vector3 fwd = Vector3.Cross(right, Vector3.up);
                    var body = new List<Vector3>(); var arms = new List<(Vector3 p, int side)>();
                    for (int i = 0; i < v.Length; i++)
                    {
                        Vector3 w = tr.position + tr.rotation * v[i] - hips;
                        var p = new Vector3(Vector3.Dot(w, right), w.y, Vector3.Dot(w, fwd));
                        string n = dominant[i];
                        if (IsBody(n)) body.Add(p);
                        // The hand should rest against the thigh (clearance 0), not sink into it.
                        else if (n.StartsWith("RightHand")) arms.Add((p, 1));
                        else if (n.StartsWith("LeftHand")) arms.Add((p, -1));
                    }
                    foreach (int side in new[] { -1, 1 })
                    {
                        // The hand's inner side (its innermost tenth) against the body's outer edge at the hand's height.
                        var hand = arms.Where(x => x.side == side).Select(x => x.p).ToList();
                        if (hand.Count < 10) continue;
                        float hy = hand.Average(x => x.y), hz = hand.Average(x => x.z);
                        var inner = hand.Select(x => x.x * side).OrderBy(x => x).ToList();
                        float handIn = inner[inner.Count / 10];
                        float span = hand.Max(x => x.y) - hand.Min(x => x.y);
                        var edgeXs = body.Where(q => Mathf.Abs(q.y - hy) < span * 0.5f && Mathf.Abs(q.z - hz) < span).Select(q => q.x * side).OrderBy(x => x).ToList();
                        if (edgeXs.Count < 10) continue;
                        float edge = edgeXs[(int)(edgeXs.Count * 0.9f)];      // not the outermost flare of a skirt
                        float sink = edge + clear - handIn;
                        Vector3 shoulder = map[side > 0 ? "RightArm" : "LeftArm"].position - hips;
                        float drop = Mathf.Max(1e-4f, shoulder.y - hy);
                        float sh = Vector3.Dot(shoulder, right) * side;
                        needs.Add(sink <= 0f ? 0f : (Mathf.Atan2(handIn + sink - sh, drop) - Mathf.Atan2(handIn - sh, drop)) * Mathf.Rad2Deg);
                    }
                }
            Object.DestroyImmediate(baked);
            Object.DestroyImmediate(inst);
            if (needs.Count == 0) return 0f;
            // The typical frame, not the worst: one frame's swing should not set the pose for all.
            needs.Sort();
            return Mathf.Clamp(needs[needs.Count / 2], 0f, 8f);
        }

        public static float ClipPace(GameObject fbx, AnimationClip clip, float scale, bool anyDirection = false)
        {
            var inst = (GameObject)Object.Instantiate(fbx);
            var map = inst.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            const int steps = 120;
            float dt = clip.length / steps;
            var speeds = new List<float>();
            foreach (var (footName, toeName) in new[] { ("LeftFoot", "LeftToeBase"), ("RightFoot", "RightToeBase") })
            {
                var pos = new Vector3[steps + 1];
                Vector3 facing = Vector3.zero;
                float lowest = float.MaxValue, highest = float.MinValue;
                for (int i = 0; i <= steps; i++)
                {
                    clip.SampleAnimation(inst, i * dt);
                    pos[i] = map[footName].position;
                    facing += map[toeName].position - map[footName].position;
                    lowest = Mathf.Min(lowest, pos[i].y);
                    highest = Mathf.Max(highest, pos[i].y);
                }
                facing.y = 0f; facing.Normalize();
                float groundLine = Mathf.Lerp(lowest, highest, 0.35f);
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 slide = pos[i - 1] - pos[i]; slide.y = 0f;
                    float back = anyDirection ? slide.magnitude / dt : Vector3.Dot(slide, facing) / dt;
                    if (pos[i].y < groundLine && back > 0f) speeds.Add(back);
                }
            }
            Object.DestroyImmediate(inst);
            if (speeds.Count == 0) return 0f;
            speeds.Sort();
            return speeds[speeds.Count / 2] * scale;
        }

        /// <summary>Every clip of a character, and the jump's rise baked from them.</summary>
        public static AnimationClip[] AllClips(string model)
        {
            return AssetDatabase.LoadAllAssetsAtPath($"{Art.Root}/Characters/{model}.fbx").OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview"))
                .Concat(new[] { AssetDatabase.LoadAssetAtPath<AnimationClip>($"{Art.Gen}/Clips/{model}/JumpUp.anim") }).Where(c => c != null)
                .OrderBy(c => c.name).ToArray();
        }

        /// <summary>What the builder works out about a character's clips, for its CharacterRig.</summary>
        public class ClipFacts
        {
            public float WalkPace, SprintPace, CrouchPace, ArmedRunPace = 4.5f, ArmedCrouchPace = 1.5f, ArmedWalkPace = 1.6f, ThrowSeconds = 2f, ThrowRelease = 1f, ArmSpread;
            public string[] PunchStates = new string[0];
            public int[] PunchHands = new int[0];
            public float[] PunchOffsets = new float[0];
            public bool ClipArms;
        }

        /// <summary>Directions of the eight-way clips, by the suffix of their role name (x right, y forward).</summary>
        static readonly (string Suffix, Vector2 Dir)[] EightWay =
        {
            ("F", new Vector2(0, 1)), ("FL", new Vector2(-1, 1).normalized), ("L", new Vector2(-1, 0)), ("BL", new Vector2(-1, -1).normalized),
            ("B", new Vector2(0, -1)), ("BR", new Vector2(1, -1).normalized), ("R", new Vector2(1, 0)), ("FR", new Vector2(1, 1).normalized),
        };

        /// <summary>Death states the rig chooses between, by where the shot came from.</summary>
        public static readonly string[] DeathStates = { "Death", "DeathFront", "DeathBack", "DeathHeadFront", "DeathHeadBack", "DeathRight", "DeathCrouch" };
        public const int Gestures = 8;

        static AnimatorController BuildController(string model, float bodyScale, out ClipFacts result)
        {
            Art.EnsureFolder($"{Art.Gen}/Animators");
            string path = $"{Art.Gen}/Animators/{model}.controller";
            AssetDatabase.DeleteAsset(path);
            string fbxPath = $"{Art.Root}/Characters/{model}.fbx";
            var roles = Art.RoleFile(fbxPath);
            var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview")).ToDictionary(c => Art.ClipName(c.name), c => c);
            var modelFbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            // Clips that must not wander off the collider are re-made with the hips held over their spot.
            if (roles != null)
                foreach (var r in roles.clips.Where(r => r.inplace && clips.ContainsKey(r.role)))
                    clips[r.role] = ClipBaker.InPlace(modelFbx, clips[r.role], $"{Art.Gen}/Clips/{model}/{r.role}.anim");
            bool Has(string n) => clips.ContainsKey(n);
            // A clip's own ground speed on this body: the library's speed (its mannequin is 1.8 m) scaled to the character's height.
            float LibrarySpeed(string role)
            {
                var r = roles?.clips.FirstOrDefault(x => x.role == role);
                return r != null && r.speed > 0f ? r.speed * TargetHeight(model) / 1.8f : 0f;
            }
            AnimationClip Clip(string n)
            {
                if (clips.TryGetValue(n, out var c)) return c;
                Debug.LogError($"[BUILD] {model}: clip {n} missing, has: {string.Join(",", clips.Keys)}");
                return null;
            }
            var facts = new ClipFacts { ClipArms = roles != null && roles.clips.Any(r => r.role == "Idle") };
            var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter(new AnimatorControllerParameter { name = "MoveSign", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            ac.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            ac.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
            ac.AddParameter(new AnimatorControllerParameter { name = "ArmedPace", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            ac.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Armed", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Style", AnimatorControllerParameterType.Int);
            ac.AddParameter(new AnimatorControllerParameter { name = "Grounded", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            ac.AddParameter("Roll", AnimatorControllerParameterType.Trigger);
            ac.AddParameter("Driving", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Punch", AnimatorControllerParameterType.Trigger);
            var sm = ac.layers[0].stateMachine;

            var loco = ac.CreateBlendTreeInController("Locomotion", out var locoTree, 0);
            locoTree.blendParameter = "Speed"; locoTree.useAutomaticThresholds = false;
            // Clip paces rise with effort whatever the measure says, so the blend always has a direction.
            float walkPace = Mathf.Clamp(ClipPace(modelFbx, Clip("Walk"), bodyScale), 0.8f, 2.2f);
            float jogPace = Mathf.Max(ClipPace(modelFbx, Clip("Jog"), bodyScale), walkPace + 0.8f);
            // A sprinter's foot whips back at a speed that says little about the body, and most of a stride is in the air: the clip plays as made at the game's sprint speed.
            float sprintPace = Mathf.Max(6.6f, jogPace + 0.8f);
            float crouchPace = Mathf.Clamp(ClipPace(modelFbx, Clip("CrouchWalk"), bodyScale), 0.3f, 2.5f);
            facts.WalkPace = walkPace; facts.SprintPace = sprintPace; facts.CrouchPace = crouchPace;
            facts.ArmSpread = ArmSpread(modelFbx, new[] { Clip("Idle") }, bodyScale);
            Debug.Log($"[BUILD] {model} arms swung out {facts.ArmSpread:F1} degrees to clear the body");
            Debug.Log($"[BUILD] {model} clip paces m/s: walk {walkPace:F2} jog {jogPace:F2} sprint {sprintPace:F2} crouch {crouchPace:F2}");
            locoTree.AddChild(Clip("Idle"), 0f);
            locoTree.AddChild(Clip("Walk"), walkPace);
            locoTree.AddChild(Clip("Jog"), jogPace);
            locoTree.AddChild(Clip("Sprint"), sprintPace);
            // All gaits in step: normalised time 0 is the left foot furthest forward (the arm swing reads this too).
            var gaits = locoTree.children;
            for (int g = 1; g < gaits.Length; g++) gaits[g].cycleOffset = ClipBaker.LeftFootForwardPhase(modelFbx, (AnimationClip)gaits[g].motion);
            locoTree.children = gaits;
            loco.speedParameter = "MoveSign"; loco.speedParameterActive = true;

            var crouch = ac.CreateBlendTreeInController("Crouch", out var crouchTree, 0);
            crouchTree.blendParameter = "Speed"; crouchTree.useAutomaticThresholds = false;
            crouchTree.AddChild(Clip("CrouchIdle"), 0f);
            crouchTree.AddChild(Clip("CrouchWalk"), crouchPace);
            crouch.speedParameter = "MoveSign"; crouch.speedParameterActive = true;

            // A gait style for walking (drunk, injured): its own idle and walk, the walk played at the pace the normal walk has, faster gaits shared.
            AnimatorState StyleTree(string name, string idle, string walk, string run)
            {
                if (!Has(idle) || !Has(walk)) return null;
                var st = ac.CreateBlendTreeInController(name, out var tree, 0);
                tree.blendParameter = "Speed"; tree.useAutomaticThresholds = false;
                tree.AddChild(Clip(idle), 0f);
                tree.AddChild(Clip(walk), walkPace);
                tree.AddChild(run != null && Has(run) ? Clip(run) : Clip("Jog"), jogPace);
                tree.AddChild(Clip("Sprint"), sprintPace);
                var ch = tree.children;
                // A limp or a stagger fools the planted-foot measure: the library's own speed for the clip is better when it has one.
                float walkOwn = LibrarySpeed(walk) > 0f ? LibrarySpeed(walk) : ClipPace(modelFbx, Clip(walk), bodyScale);
                ch[1].timeScale = walkPace / Mathf.Clamp(walkOwn, 0.4f, 2.2f);
                if (run != null && Has(run))
                {
                    float runOwn = LibrarySpeed(run) > 0f ? LibrarySpeed(run) : ClipPace(modelFbx, Clip(run), bodyScale);
                    ch[2].timeScale = jogPace / Mathf.Clamp(runOwn, 1f, 5f);
                }
                Debug.Log($"[BUILD] {model} {name}: walk plays at {ch[1].timeScale:F2}x (own pace {walkOwn:F2} m/s)");
                tree.children = ch;
                st.speedParameter = "MoveSign"; st.speedParameterActive = true;
                return st;
            }
            var drunk = StyleTree("Drunk", "DrunkIdle", "DrunkWalk", "DrunkRun");
            var injured = StyleTree("Injured", "InjuredIdle", "InjuredWalk", null);

            // Gun out: eight-way clips, so strafing and backing off have real steps. The rig sets MoveX / MoveZ to the velocity in body space.
            AnimatorState EightWayTree(string name, string idle, params (string Prefix, float Fallback)[] rings)
            {
                if (!Has(idle)) return null;
                var st = ac.CreateBlendTreeInController(name, out var tree, 0);
                tree.blendType = BlendTreeType.FreeformDirectional2D;
                tree.blendParameter = "MoveX"; tree.blendParameterY = "MoveZ";
                tree.AddChild(Clip(idle), Vector2.zero);
                float outer = 0f;
                foreach (var (prefix, fallback) in rings)
                {
                    // One pace per ring, the median over its clips: the eight directions of a ring then form a circle.
                    var paces = EightWay.Where(e => Has(prefix + e.Suffix)).Select(e => ClipPace(modelFbx, Clip(prefix + e.Suffix), bodyScale, true))
                        .Where(p => p > 0.2f).OrderBy(p => p).ToList();
                    float pace = paces.Count > 0 ? paces[paces.Count / 2] : fallback;
                    pace = Mathf.Max(pace, outer + 0.8f);
                    if (outer == 0f && name == "Armed") facts.ArmedWalkPace = pace;    // the first ring: the walk
                    outer = pace;
                    foreach (var e in EightWay)
                        if (Has(prefix + e.Suffix)) tree.AddChild(Clip(prefix + e.Suffix), e.Dir * pace);
                    Debug.Log($"[BUILD] {model} {prefix} ring pace {pace:F2} m/s from {paces.Count} clips");
                }
                st.speedParameter = "ArmedPace"; st.speedParameterActive = true;
                if (name == "Armed") facts.ArmedRunPace = outer; else facts.ArmedCrouchPace = outer;
                return st;
            }
            var armed = EightWayTree("Armed", "ArmedIdle", ("ArmedWalk", 1.6f), ("ArmedRun", 4f), ("ArmedSprint", 6.5f));
            var armedCrouch = EightWayTree("ArmedCrouch", "ArmedCrouchIdle", ("ArmedCrouch", 1.4f));

            // Rising (timed to the jump's physics), falling, landing.
            var air = sm.AddState("Air"); air.motion = Clip("JumpLoop");
            var jumpUp = sm.AddState("JumpUp");
            jumpUp.motion = ClipBaker.BakeJumpUp(modelFbx, Clip("JumpStart"), CharacterRig.JumpRiseSeconds, $"{Art.Gen}/Clips/{model}/JumpUp.anim");
            var land = sm.AddState("JumpLand"); land.motion = Clip("JumpLand"); land.speed = 1.5f;
            var roll = sm.AddState("Roll"); roll.motion = Clip("Roll");
            foreach (var d in DeathStates)
                if (Has(d)) sm.AddState(d).motion = Clip(d);
            var seat = sm.AddState("Driving"); seat.motion = Clip("Driving");
            sm.defaultState = loco;

            AnimatorStateTransition T(AnimatorState a, AnimatorState b, float d = 0.14f)
            { var t = a.AddTransition(b); t.hasExitTime = false; t.duration = d; return t; }
            AnimatorStateTransition After(AnimatorState a, AnimatorState b, float exit, float d)
            { var t = a.AddTransition(b); t.hasExitTime = true; t.exitTime = exit; t.duration = d; return t; }

            // Transitions are checked in the order they are added: the more specific ones first.
            if (armed != null)
            {
                var toAc = T(loco, armedCrouch ?? crouch, 0.2f); toAc.AddCondition(AnimatorConditionMode.If, 0, "Armed"); toAc.AddCondition(AnimatorConditionMode.If, 0, "Crouch");
                var toArmed = T(loco, armed, 0.2f); toArmed.AddCondition(AnimatorConditionMode.If, 0, "Armed");
            }
            T(loco, crouch).AddCondition(AnimatorConditionMode.If, 0, "Crouch");
            T(crouch, loco).AddCondition(AnimatorConditionMode.IfNot, 0, "Crouch");
            foreach (var g in new[] { loco, armed, drunk, injured })
                if (g != null) T(g, air).AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");
            var upToFall = jumpUp.AddTransition(air); upToFall.hasExitTime = true; upToFall.exitTime = 0.92f; upToFall.duration = 0.08f;
            T(jumpUp, land, 0.06f).AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            T(air, land, 0.05f).AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            T(land, air).AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");
            if (armed != null) After(land, armed, 0.42f, 0.16f).AddCondition(AnimatorConditionMode.If, 0, "Armed");
            After(land, loco, 0.42f, 0.16f);

            foreach (var (st, id) in new[] { (drunk, 1), (injured, 2) })
            {
                if (st == null) continue;
                var tin = T(loco, st, 0.3f); tin.AddCondition(AnimatorConditionMode.Equals, id, "Style"); tin.AddCondition(AnimatorConditionMode.IfNot, 0, "Crouch");
                tin.AddCondition(AnimatorConditionMode.IfNot, 0, "Armed");
                T(st, loco, 0.3f).AddCondition(AnimatorConditionMode.NotEqual, id, "Style");
                T(st, loco, 0.2f).AddCondition(AnimatorConditionMode.If, 0, "Crouch");
                T(st, loco, 0.2f).AddCondition(AnimatorConditionMode.If, 0, "Armed");
            }

            if (armed != null)
            {
                T(armed, loco, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0, "Armed");
                if (armedCrouch != null)
                {
                    // Stopping to crouch with a gun drops to one knee, standing up from it rises; on the move it is a plain blend.
                    if (Has("KneelDown"))
                    {
                        var kneelDown = sm.AddState("KneelDown"); kneelDown.motion = Clip("KneelDown"); kneelDown.speed = 1.5f;
                        var k = T(armed, kneelDown, 0.12f); k.AddCondition(AnimatorConditionMode.If, 0, "Crouch"); k.AddCondition(AnimatorConditionMode.Less, 0.5f, "Speed");
                        T(kneelDown, armed, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0, "Crouch");
                        After(kneelDown, armedCrouch, 0.85f, 0.15f);
                    }
                    T(armed, armedCrouch, 0.2f).AddCondition(AnimatorConditionMode.If, 0, "Crouch");
                    T(armedCrouch, crouch, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0, "Armed");
                    if (Has("KneelUp"))
                    {
                        var kneelUp = sm.AddState("KneelUp"); kneelUp.motion = Clip("KneelUp"); kneelUp.speed = 2f;
                        var k = T(armedCrouch, kneelUp, 0.1f); k.AddCondition(AnimatorConditionMode.IfNot, 0, "Crouch"); k.AddCondition(AnimatorConditionMode.Less, 0.5f, "Speed");
                        After(kneelUp, armed, 0.8f, 0.15f);
                    }
                    T(armedCrouch, armed, 0.2f).AddCondition(AnimatorConditionMode.IfNot, 0, "Crouch");
                    T(crouch, armedCrouch, 0.2f).AddCondition(AnimatorConditionMode.If, 0, "Armed");
                }
            }

            var toSeat = sm.AddAnyStateTransition(seat); toSeat.hasExitTime = false; toSeat.duration = 0.2f; toSeat.canTransitionToSelf = false;
            toSeat.AddCondition(AnimatorConditionMode.If, 0, "Driving");
            T(seat, loco, 0.25f).AddCondition(AnimatorConditionMode.IfNot, 0, "Driving");

            // Punches: whole-body clips (Punch0, Punch1 ... taken in turn). Each starts a wind-up before its strike and plays at the
            // speed that brings the striking fist in when the hit lands (CharacterRig.PunchLandsAfter); the rig's arm IK only nudges
            // that fist onto the other body.
            const float windUp = 0.3f, recover = 0.5f;
            var punchNames = clips.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^Punch\d*$")).OrderBy(k => k).ToList();
            var pStates = new List<string>(); var pHands = new List<int>(); var pOffsets = new List<float>();
            foreach (var pn in punchNames)
            {
                var pc = clips[pn];
                float strike = ClipBaker.StrikePoint(modelFbx, pc, out int hand) * pc.length;
                float offset = Mathf.Max(0f, strike - windUp);
                var ps = sm.AddState(pn); ps.motion = pc;
                ps.speed = Mathf.Clamp((strike - offset) / CharacterRig.PunchLandsAfter, 0.8f, 3f);
                After(ps, loco, Mathf.Clamp((strike + recover) / pc.length, 0.3f, 0.98f), 0.2f);
                pStates.Add(pn); pHands.Add(hand); pOffsets.Add(offset);
                Debug.Log($"[BUILD] {model} {pn}: {(hand > 0 ? "right" : "left")} fist at {strike:F2} s of {pc.length:F2} s, starts at {offset:F2} s, played at {ps.speed:F2}x");
            }
            facts.PunchStates = pStates.ToArray(); facts.PunchHands = pHands.ToArray(); facts.PunchOffsets = pOffsets.ToArray();

            // One-shots the rig starts by name (CrossFade); each returns to walking by itself.
            AnimatorState OneShot(string name, float speed, float exit, float fade)
            {
                if (!Has(name)) return null;
                var st = sm.AddState(name); st.motion = Clip(name); st.speed = speed;
                After(st, loco, exit, fade);
                return st;
            }
            // Knocked over: a fall back, a moment on the ground, then getting up.
            if (Has("KnockFall") && Has("GetUp"))
            {
                var fall = sm.AddState("KnockFall"); fall.motion = Clip("KnockFall"); fall.speed = 1.2f;
                var getUp = OneShot("GetUp", 1.25f, 0.9f, 0.3f);
                After(fall, getUp, 0.62f, 0.45f);
            }
            if (OneShot("ThrowGrenade", 1.4f, 0.88f, 0.25f) != null)
            {
                facts.ThrowSeconds = Clip("ThrowGrenade").length / 1.4f;
                facts.ThrowRelease = ClipBaker.ReleasePoint(modelFbx, Clip("ThrowGrenade")) * facts.ThrowSeconds;
                Debug.Log($"[BUILD] {model} grenade throw {facts.ThrowSeconds:F2} s, leaves the hand at {facts.ThrowRelease:F2} s");
            }
            OneShot("DodgeLeft", 1.3f, 0.75f, 0.2f);
            OneShot("DodgeRight", 1.3f, 0.75f, 0.2f);
            for (int g = 0; g < Gestures; g++)
            {
                var st = OneShot($"Gesture{g}", 1f, 0.92f, 0.3f);
                if (st != null) T(st, loco, 0.25f).AddCondition(AnimatorConditionMode.Greater, 0.6f, "Speed");    // walking off cuts it short
            }

            var tr = roll.AddTransition(loco); tr.hasExitTime = true; tr.exitTime = 0.85f; tr.duration = 0.15f;
            var anyRoll = sm.AddAnyStateTransition(roll); anyRoll.hasExitTime = false; anyRoll.duration = 0.1f; anyRoll.canTransitionToSelf = false;
            anyRoll.AddCondition(AnimatorConditionMode.If, 0, "Roll");
            EditorUtility.SetDirty(ac);
            result = facts;
            return ac;
        }

        // ---- vehicles ----

        public class CarSpec
        {
            public string Id; public Vector3 Size; public float WheelRadius, AxleFront, AxleRear, HalfTrack, SeatZ, EyeHeight;
        }

        public static readonly CarSpec[] Cars =
        {
            new CarSpec { Id = "car", Size = new Vector3(1.85f, 1.45f, 4.5f), WheelRadius = 0.33f, AxleFront = 0.31f, AxleRear = -0.3f, HalfTrack = 0.84f, SeatZ = -0.1f, EyeHeight = 1.13f },
            new CarSpec { Id = "car_b", Size = new Vector3(1.95f, 1.72f, 4.6f), WheelRadius = 0.38f, AxleFront = 0.31f, AxleRear = -0.31f, HalfTrack = 0.89f, SeatZ = -0.05f, EyeHeight = 1.42f },
            // Real cars, at their factory sizes: Dacia 1310 saloon (4.35 m, wheelbase 2.44 m), Oltcit Club (3.73 m, 2.37 m),
            // Trabant 601 (3.56 m, 2.02 m).
            new CarSpec { Id = "dacia", Size = new Vector3(1.64f, 1.44f, 4.35f), WheelRadius = 0.29f, AxleFront = 0.29f, AxleRear = -0.27f, HalfTrack = 0.74f, SeatZ = -0.1f, EyeHeight = 1.12f },
            new CarSpec { Id = "oltcit", Size = new Vector3(1.54f, 1.43f, 3.73f), WheelRadius = 0.28f, AxleFront = 0.33f, AxleRear = -0.3f, HalfTrack = 0.7f, SeatZ = -0.05f, EyeHeight = 1.12f },
            new CarSpec { Id = "trabant", Size = new Vector3(1.5f, 1.44f, 3.56f), WheelRadius = 0.28f, AxleFront = 0.29f, AxleRear = -0.28f, HalfTrack = 0.68f, SeatZ = -0.1f, EyeHeight = 1.1f },
        };

        /// <summary>A car's mesh brought to its real size, standing on y = 0, with the bonnet along +Z (the roof sits towards the back).</summary>
        static Mesh BakeCarMesh(string fbxName, Vector3 size, string assetPath)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{Art.Root}/Props/{fbxName}.fbx");
            var mf = fbx.GetComponentInChildren<MeshFilter>();
            var src = mf.sharedMesh;
            var toRoot = mf.transform.localToWorldMatrix;
            var pts = src.vertices.Select(v => toRoot.MultiplyPoint3x4(v)).ToArray();

            float bestYaw = 0f, bestArea = float.MaxValue;
            for (float y = 0; y < 90f; y += 1f)
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
            float minY0 = rp.Min(p => p.y), h = rp.Max(p => p.y) - minY0;
            float cz = (rp.Min(p => p.z) + rp.Max(p => p.z)) * .5f;
            float roofZ = rp.Where(p => p.y > minY0 + h * 0.8f).Average(p => p.z) - cz;
            if (roofZ > 0f) { rot = Quaternion.Euler(0, 180, 0) * rot; rp = pts.Select(p => rot * p).ToArray(); }
            // Generated cars can come out pitched or rolled: level the body on its four tyres.
            Vector2 tilt0 = TyreTilt(rp);
            for (int pass = 0; pass < 2; pass++)
            {
                Vector2 t = TyreTilt(rp);
                rot = Quaternion.Euler(t.x, 0, t.y) * rot;
                rp = pts.Select(p => rot * p).ToArray();
            }
            Vector2 tilt1 = TyreTilt(rp);
            Debug.Log($"[BUILD] car mesh {fbxName}: levelled pitch {tilt0.x:F2} -> {tilt1.x:F2} deg, roll {tilt0.y:F2} -> {tilt1.y:F2} deg");
            Bounds b = new Bounds(rp[0], Vector3.zero);
            foreach (var p in rp) b.Encapsulate(p);
            var scale = new Vector3(size.x / b.size.x, size.y / b.size.y, size.z / b.size.z);
            var norm = Matrix4x4.Scale(scale) * Matrix4x4.Translate(new Vector3(-b.center.x, -b.min.y, -b.center.z)) * Matrix4x4.Rotate(rot) * toRoot;

            var acc = new MeshAccum();
            acc.Add(src, norm, Color.white);
            var mesh = acc.Build(fbxName);
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(mesh, assetPath);
            Debug.Log($"[BUILD] car mesh {fbxName}: roof offset {roofZ:F2} ({(roofZ > 0 ? "turned round" : "already facing +Z")}), {src.triangles.Length / 3} tris");
            return mesh;
        }

        /// <summary>
        /// The rotation (x = pitch, z = roll, degrees) that brings the bottoms of a car's four tyres onto one level plane. Each
        /// tyre's bottom is the mean of the lowest points in its quarter of the body, away from the bumpers and the middle,
        /// so a low spoiler or sill does not count. Points: bonnet along +Z.
        /// </summary>
        static Vector2 TyreTilt(Vector3[] p)
        {
            float minX = p.Min(q => q.x), maxX = p.Max(q => q.x), minZ = p.Min(q => q.z), maxZ = p.Max(q => q.z);
            float cx = (minX + maxX) * .5f, cz = (minZ + maxZ) * .5f, hw = (maxX - minX) * .5f, hl = (maxZ - minZ) * .5f;
            var bottom = new Vector3[4];
            for (int w = 0; w < 4; w++)
            {
                float sx = w % 2 == 0 ? -1f : 1f, sz = w < 2 ? 1f : -1f;
                var low = p.Where(q => (q.x - cx) * sx > hw * 0.3f && (q.z - cz) * sz > hl * 0.3f && (q.z - cz) * sz < hl * 0.85f)
                           .OrderBy(q => q.y).Take(8).ToArray();
                if (low.Length == 0) return Vector2.zero;
                bottom[w] = new Vector3(low.Average(q => q.x), low.Average(q => q.y), low.Average(q => q.z));
            }
            Vector3 front = (bottom[0] + bottom[1]) * .5f, rear = (bottom[2] + bottom[3]) * .5f;
            Vector3 left = (bottom[0] + bottom[2]) * .5f, right = (bottom[1] + bottom[3]) * .5f;
            // Positive x tips the bonnet down, positive z lifts the right side: turn each slope back to level.
            float pitch = Mathf.Atan2(front.y - rear.y, front.z - rear.z) * Mathf.Rad2Deg;
            float roll = -Mathf.Atan2(right.y - left.y, right.x - left.x) * Mathf.Rad2Deg;
            return new Vector2(pitch, roll);
        }

        static Mesh WheelMesh(string path)
        {
            // A flat short cylinder, axis along X, radius 1, width 1: the texture sits on both faces.
            var acc = new MeshAccum();
            const int n = 20;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2f, a1 = (i + 1) / (float)n * Mathf.PI * 2f;
                Vector3 c0 = new(0, Mathf.Cos(a0), Mathf.Sin(a0)), c1 = new(0, Mathf.Cos(a1), Mathf.Sin(a1));
                Vector3 hx = new(0.5f, 0, 0);
                // The tyre ends at 0.915 of the picture's radius, and what lies beyond it would show as a grey ring on an opaque disc.
                const float tyre = 0.915f * .5f;
                Vector2 uv0 = new Vector2(c0.z, c0.y) * tyre + Vector2.one * .5f, uv1 = new Vector2(c1.z, c1.y) * tyre + Vector2.one * .5f, uvc = Vector2.one * .5f;
                // Each face turned outwards, or the near end is culled and one looks into a hollow drum at the far end.
                int caps = acc.V.Count;
                acc.Quad(hx, c0 + hx, c1 + hx, hx, uvc, uv0, uv1, uvc, Color.white);
                acc.Quad(-hx, c1 - hx, c0 - hx, -hx, uvc, uv1, uv0, uvc, Color.white);
                // A cap's quad closes on its first corner, which leaves it no normal of its own.
                for (int k = 0; k < 4; k++) { acc.N[caps + k] = Vector3.right; acc.N[caps + 4 + k] = Vector3.left; }
                Vector2 tread = new(0.02f, 0.5f);
                acc.Quad(c0 - hx, c1 - hx, c1 + hx, c0 + hx, tread, tread, tread, tread, new Color(.12f, .12f, .12f));
            }
            var mesh = acc.Build("Wheel");
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        const float WheelDistance = 0.51f;       // from the driver's eye, along the car: where the hands hold the steering wheel
        static float WheelHeight(CarSpec spec) => spec.EyeHeight - 0.3f;

        /// <summary>
        /// The lights are the car's own lamp triangles, found by their painted colour: red at the back, and at the front whatever
        /// is not body paint in the lamp zone. So they sit exactly where the picture of the car has them.
        /// </summary>
        static Mesh LampMesh(CarSpec spec, Mesh body, string path, out Vector3 headCenter, out Vector3 tailCenter)
        {
            var tex = new Texture2D(2, 2);
            tex.LoadImage(System.IO.File.ReadAllBytes($"{Art.Root}/Props/{spec.Id}.png"));
            var v = body.vertices; var uv = body.uv; var n = body.normals; var t = body.triangles;
            Vector3 s = spec.Size;
            float hw = s.x * .5f, hl = s.z * .5f;
            var acc = new MeshAccum();
            Vector3 hc = Vector3.zero, tc = Vector3.zero;
            int heads = 0, tails = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 mid = (v[a] + v[b] + v[c]) / 3f;
                Vector3 face = Vector3.Cross(v[b] - v[a], v[c] - v[a]).normalized;
                Vector2 m = (uv[a] + uv[b] + uv[c]) / 3f;
                Color px = tex.GetPixelBilinear(m.x, m.y);
                float lum = px.grayscale;
                bool tail = mid.z < -hl * 0.72f && face.z < 0.1f && mid.y > s.y * 0.36f && mid.y < s.y * 0.82f && px.r > 0.22f && px.r > px.g * 1.7f && px.r > px.b * 1.7f;
                bool head = mid.z > hl * 0.72f && face.z > 0.5f && mid.y > s.y * 0.38f && mid.y < s.y * 0.72f && Mathf.Abs(mid.x) > hw * 0.42f && lum < 0.72f;
                if (!tail && !head) continue;
                Color col = tail ? new Color(1f, .05f, .03f, 0f) : new Color(1f, .96f, .85f, 1f);
                int start = acc.V.Count;
                foreach (int k in new[] { a, b, c })
                {
                    acc.V.Add(v[k] + n[k] * 0.012f);
                    acc.N.Add(n[k]); acc.UV.Add(uv[k]); acc.C.Add(col);
                }
                acc.T.Add(start); acc.T.Add(start + 1); acc.T.Add(start + 2);
                if (tail) { tc += mid; tails++; } else { hc += mid; heads++; }
            }
            Object.DestroyImmediate(tex);
            headCenter = heads > 0 ? hc / heads : new Vector3(0, s.y * .5f, hl);
            tailCenter = tails > 0 ? tc / tails : new Vector3(0, s.y * .6f, -hl);
            Debug.Log($"[BUILD] {spec.Id} lamps: {heads} head and {tails} tail triangles, head height {headCenter.y:F2}, tail height {tailCenter.y:F2}");
            if (heads < 2 || tails < 2) Debug.LogWarning($"[BUILD] {spec.Id}: few lamp triangles found");
            var mesh = acc.Build("Lamps_" + spec.Id);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        /// <summary>
        /// Each wheel from its own corner of the shell, since a generated mesh is not symmetric: the axle from the corner's lowest
        /// vertices, and the outside from the bodywork around the arch, so that no wheel stands out from its side of the car.
        /// </summary>
        static Vector3[] FindWheels(Mesh body, CarSpec spec)
        {
            var v = body.vertices;
            var result = new Vector3[4];
            const float halfWidth = 0.09f;
            for (int w = 0; w < 4; w++)
            {
                float sx = w % 2 == 0 ? -1f : 1f, sz = w < 2 ? 1f : -1f;
                float sum = 0f; int n = 0;
                foreach (var p in v)
                    if (p.y < 0.1f && p.x * sx > 0f && p.z * sz > 0f) { sum += p.z; n++; }
                float z = n > 0 ? sum / n : spec.Size.z * (w < 2 ? spec.AxleFront : spec.AxleRear);
                float side = 0f;
                foreach (var p in v)
                    if (p.x * sx > 0f && p.y > spec.WheelRadius * 0.9f && p.y < spec.WheelRadius * 1.9f && Mathf.Abs(p.z - z) < spec.WheelRadius * 1.5f)
                        side = Mathf.Max(side, p.x * sx);
                if (side < 0.5f) side = spec.Size.x * .5f;
                result[w] = new Vector3(sx * (side - 0.015f - halfWidth), 0f, z);
                Debug.Log($"[BUILD] {spec.Id} wheel {w}: axle z {z:F2}, bodywork side x {side:F2}");
            }
            return result;
        }

        /// <summary>For one side of a car: where a ray from outside first meets the shell, at a given length and height along the body.</summary>
        static System.Func<float, float, float> SideSkin(Mesh body, float sx, float edge)
        {
            var v = body.vertices; var t = body.triangles;
            const float strip = 0.1f;
            var strips = new Dictionary<int, List<(Vector3 a, Vector3 b, Vector3 c)>>();
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 p = v[t[i]], q = v[t[i + 1]], r = v[t[i + 2]];
                if ((p.x + q.x + r.x) / 3f * sx < edge) continue;
                int z0 = Mathf.FloorToInt(Mathf.Min(p.z, Mathf.Min(q.z, r.z)) / strip), z1 = Mathf.FloorToInt(Mathf.Max(p.z, Mathf.Max(q.z, r.z)) / strip);
                for (int k = z0; k <= z1; k++)
                {
                    if (!strips.TryGetValue(k, out var list)) strips[k] = list = new List<(Vector3, Vector3, Vector3)>();
                    list.Add((p, q, r));
                }
            }
            return (z, y) =>
            {
                float best = float.MinValue;
                if (!strips.TryGetValue(Mathf.FloorToInt(z / strip), out var faces)) return best;
                foreach (var f in faces)
                {
                    // Barycentric, seen along x.
                    float d = (f.b.y - f.c.y) * (f.a.z - f.c.z) + (f.c.z - f.b.z) * (f.a.y - f.c.y);
                    if (Mathf.Abs(d) < 1e-7f) continue;
                    float u = ((f.b.y - f.c.y) * (z - f.c.z) + (f.c.z - f.b.z) * (y - f.c.y)) / d;
                    float w = ((f.c.y - f.a.y) * (z - f.c.z) + (f.a.z - f.c.z) * (y - f.c.y)) / d;
                    if (u < 0f || w < 0f || u + w > 1f) continue;
                    float x = (u * f.a.x + w * f.b.x + (1f - u - w) * f.c.x) * sx;
                    if (x > best) best = x;
                }
                return best;
            };
        }

        /// <summary>
        /// The shell of a car in use: round holes where the moulded wheels were, and the front doors as parts of their own. Also the
        /// dark insides seen through those holes.
        /// </summary>
        static void CutShell(Mesh whole, CarSpec spec, Vector3[] wheels, out Mesh cut, out Mesh[] doors, out Vector3[] hinges, out Mesh wells)
        {
            var cutter = new ShellCutter(whole);
            var inside = new MeshAccum();
            Color dark = new(.025f, .025f, .028f);
            float holeR = spec.WheelRadius * 1.14f;     // room for the front wheels to steer
            foreach (var w in wheels)
            {
                float sx = Mathf.Sign(w.x), outer = Mathf.Abs(w.x) + 0.12f;
                System.Func<ShellCutter.Vert[], bool> onThisSide = t => ((t[0].P + t[1].P + t[2].P) / 3f).x * sx > outer - 0.42f;
                cutter.Take(ShellCutter.Circle(w.z, spec.WheelRadius, holeR), onThisSide);
                // The moulded tyre is wider than the hole near the road: nothing is left hanging below the arch.
                cutter.Take(ShellCutter.Rect(w.z - holeR * 1.3f, -0.3f, w.z + holeR * 1.3f, spec.WheelRadius * 0.55f), t => ((t[0].P + t[1].P + t[2].P) / 3f).x * sx > outer - 0.7f);
                // A dark drum behind the hole. It ends on the bodywork itself, all the way round, and only where there is bodywork:
                // nothing of it may stand out from the side of the car or hang below it.
                const int n = 24;
                float inner = outer - 0.36f, floorY = spec.WheelRadius * 0.55f;
                float below = Mathf.Asin(Mathf.Clamp01((spec.WheelRadius - floorY) / holeR));
                var skin = SideSkin(whole, sx, 0.3f);
                var rim = new float[n + 1];
                var angle = new float[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    angle[i] = Mathf.Lerp(-below, Mathf.PI + below, i / (float)n);
                    float x = skin(w.z + Mathf.Cos(angle[i]) * holeR * 1.03f, spec.WheelRadius + Mathf.Sin(angle[i]) * holeR * 1.03f);
                    rim[i] = x > inner + 0.05f ? Mathf.Min(x - 0.004f, outer) : float.MinValue;
                }
                Vector3 P(float a, float x) => new Vector3(x * sx, spec.WheelRadius + Mathf.Sin(a) * holeR, w.z + Mathf.Cos(a) * holeR);
                Vector2 z0 = Vector2.zero;
                Vector3 mid = new(inner * sx, spec.WheelRadius, w.z);
                int first = -1, last = -1;
                for (int i = 0; i <= n; i++)
                    if (rim[i] > float.MinValue) { if (first < 0) first = i; last = i; }
                for (int i = 0; i < n; i++)
                {
                    if (rim[i] == float.MinValue || rim[i + 1] == float.MinValue) continue;
                    inside.Quad(P(angle[i], inner), P(angle[i], rim[i]), P(angle[i + 1], rim[i + 1]), P(angle[i + 1], inner), z0, z0, z0, z0, dark);
                    inside.Quad(mid, P(angle[i], inner), P(angle[i + 1], inner), mid, z0, z0, z0, z0, dark);
                }
                if (first >= 0 && last > first)
                {
                    // The back wall is closed along the bottom too. No floor: it would run across the wheel.
                    inside.Quad(mid, P(angle[last], inner), P(angle[first], inner), mid, z0, z0, z0, z0, dark);
                }
                Debug.Log($"[BUILD] {spec.Id} wheel arch at z {w.z:F2}: drum on the bodywork over {last - first}/{n} segments, wheel face x {Mathf.Abs(w.x) + 0.09f:F2}, bodywork x {(first >= 0 ? rim[(first + last) / 2] : 0f):F2}");
            }

            float front = wheels[0].z - spec.WheelRadius * 1.14f - 0.05f;
            float back = spec.SeatZ - 0.45f;
            float sill = spec.WheelRadius * 0.95f, top = spec.Size.y * 0.86f;
            doors = new Mesh[2];
            hinges = new Vector3[2];
            for (int d = 0; d < 2; d++)
            {
                float sx = d == 0 ? -1f : 1f, edge = spec.Size.x * 0.5f * 0.55f;
                // Everything on this side within the outline goes, the inner walls too, or they stay hanging in the opening. The door is
                // what faced outwards. The front edge leans back with the windscreen pillar.
                float belt = spec.Size.y * 0.62f;
                var outline = new[] { new Vector2(back, sill), new Vector2(front, sill), new Vector2(front, belt), new Vector2(front - 0.42f, top), new Vector2(back, top) };
                var skin = SideSkin(whole, sx, edge);
                // A piece is skin if a ray from outside meets it first, at any of a few points on it.
                bool OnSkin(IList<ShellCutter.Vert> poly)
                {
                    Vector3 mid = Vector3.zero;
                    foreach (var q in poly) mid += q.P;
                    mid /= poly.Count;
                    if (System.Math.Abs(mid.x * sx - skin(mid.z, mid.y)) < 0.015f) return true;
                    foreach (var q in poly)
                    {
                        Vector3 p = Vector3.Lerp(mid, q.P, 0.7f);
                        if (System.Math.Abs(p.x * sx - skin(p.z, p.y)) < 0.015f) return true;
                    }
                    return false;
                }
                var taken = cutter.Take(outline, t => ((t[0].P + t[1].P + t[2].P) / 3f).x * sx > edge);
                var parts = new List<ShellCutter.Vert[]>();
                foreach (var poly in taken)
                    if (Vector3.Cross(poly[1].P - poly[0].P, poly[2].P - poly[0].P).x * sx > 0f && OnSkin(poly)) parts.Add(poly);
                // The generated shell is full of odds and ends behind its skin. Around the opening they would hang in the air once the door swings out.
                cutter.Take(ShellCutter.Rect(back - 0.12f, -0.3f, front + 0.08f, top + 0.08f), t =>
                    ((t[0].P + t[1].P + t[2].P) / 3f).x * sx > edge && (Vector3.Cross(t[1].P - t[0].P, t[2].P - t[0].P).x * sx <= 0f || !OnSkin(t)));
                float outer = 0f;
                foreach (var poly in parts) foreach (var q in poly) outer = Mathf.Max(outer, q.P.x * sx);
                hinges[d] = new Vector3(outer * sx, 0f, front);
                doors[d] = ShellCutter.ToMesh(parts, hinges[d], $"Door{d}_{spec.Id}", new Vector3(-0.035f * sx, 0f, 0f));
                Debug.Log($"[BUILD] {spec.Id} door {d}: {parts.Count} pieces, side x {outer:F2}");
                if (parts.Count < 12) Debug.LogError($"[BUILD] {spec.Id}: too few door triangles");
                // Behind the opening, a dark hollow: its walls follow the bodywork from just inside the skin, so nothing sticks out and nothing shows through.
                float inner = edge;
                float Rim(float z, float y) { float k = skin(z, y); return (k > edge ? Mathf.Max(inner + 0.02f, k - 0.03f) : inner + 0.02f) * sx; }
                Vector2 none = Vector2.zero;
                var ring = new List<Vector2>();
                for (int e = 0; e < outline.Length; e++)
                {
                    Vector2 from = outline[e], to = outline[(e + 1) % outline.Length];
                    for (int k = 0; k < 6; k++) ring.Add(Vector2.Lerp(from, to, k / 6f));
                }
                Vector2 hollowMid = Vector2.zero;
                foreach (var q in ring) hollowMid += q;
                hollowMid /= ring.Count;
                for (int k = 0; k < ring.Count; k++)
                {
                    Vector2 p = ring[k], q = ring[(k + 1) % ring.Count];
                    Vector3 pIn = new(inner * sx, p.y, p.x), qIn = new(inner * sx, q.y, q.x);
                    Vector3 pOut = new(Rim(p.x, p.y), p.y, p.x), qOut = new(Rim(q.x, q.y), q.y, q.x);
                    inside.Quad(pIn, pOut, qOut, qIn, none, none, none, none, dark);
                    Vector3 centre = new(inner * sx, hollowMid.y, hollowMid.x);
                    inside.Quad(centre, pIn, qIn, centre, none, none, none, none, dark);
                }
            }
            cut = cutter.Remainder("Cut_" + spec.Id);
            wells = inside.Build("Wells_" + spec.Id);
        }

        /// <summary>The cars, one prefab per model; paint is a material swap at placement.</summary>
        public static Dictionary<string, GameObject> BuildVehiclePrefabs()
        {
            Art.EnsureFolder($"{Art.Gen}/Vehicles");
            var result = new Dictionary<string, GameObject>();
            var wheelMesh = WheelMesh($"{Art.Gen}/Vehicles/wheel.asset");
            var wheelMat = Art.Plain("m_wheel", Shader.Find("GreatHeistAuto/TintLit"));
            wheelMat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art.Root}/UI/wheel.png"));
            wheelMat.SetFloat("_SmoothLight", 0.45f); wheelMat.SetFloat("_SmoothDark", 0.2f); wheelMat.SetFloat("_Metallic", 0.4f);
            EditorUtility.SetDirty(wheelMat);
            var lampMat = Art.Plain("m_car_lamps", Shader.Find("GreatHeistAuto/CarLamps"));
            var interior = Art.Plain("m_cockpit", Shader.Find("GreatHeistAuto/TintLit"));
            interior.SetFloat("_SmoothLight", 0.3f); interior.SetFloat("_SmoothDark", 0.3f); interior.SetFloat("_Cull", 0f);
            interior.SetTexture("_BaseMap", null);
            interior.SetColor("_Color", new Color(1f, 1f, 1f, 1f));
            EditorUtility.SetDirty(interior);

            foreach (var spec in Cars)
            {
                var lod0 = BakeCarMesh(spec.Id, spec.Size, $"{Art.Gen}/Vehicles/{spec.Id}_lod0.asset");
                var lod1 = BakeCarMesh(spec.Id + "_lod", spec.Size, $"{Art.Gen}/Vehicles/{spec.Id}_lod1.asset");
                var paint = Art.CarPaint(spec.Id, 0, Color.white);
                var charred = Art.CarPaint(spec.Id, 99, new Color(.05f, .045f, .04f), 0.1f, 0f);

                var root = new GameObject(spec.Id);
                root.layer = Layers.World;
                var box = root.AddComponent<BoxCollider>();
                box.center = new Vector3(0, spec.Size.y * .5f + .14f, 0);
                box.size = new Vector3(spec.Size.x, spec.Size.y - .28f, spec.Size.z);
                var rb = root.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.mass = 1250f;
                var obstacle = root.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box; obstacle.size = spec.Size; obstacle.center = Vector3.up * spec.Size.y * .5f;
                obstacle.carving = true; obstacle.enabled = false;

                MeshRenderer Part(string name, Mesh m, Material mat, Transform parent, bool shadows)
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(parent, false);
                    go.AddComponent<MeshFilter>().sharedMesh = m;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = mat;
                    r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                    return r;
                }

                var r0 = Part("LOD0", lod0, paint, root.transform, true);
                var r1 = Part("LOD1", lod1, paint, root.transform, true);
                var lod = root.AddComponent<LODGroup>();
                var nearParts = new List<Renderer> { r0 };

                var wheels = new Transform[4];
                Vector3[] wheelPos = FindWheels(lod0, spec);
                CutShell(lod0, spec, wheelPos, out Mesh cutBody, out Mesh[] doorMeshes, out Vector3[] hinges, out Mesh wellsMesh);
                void Save(Mesh m, string file) { AssetDatabase.DeleteAsset($"{Art.Gen}/Vehicles/{file}.asset"); AssetDatabase.CreateAsset(m, $"{Art.Gen}/Vehicles/{file}.asset"); }
                Save(cutBody, spec.Id + "_cut"); Save(doorMeshes[0], spec.Id + "_door_l"); Save(doorMeshes[1], spec.Id + "_door_r"); Save(wellsMesh, spec.Id + "_wells");
                var doors = new Transform[2];
                for (int d = 0; d < 2; d++)
                {
                    var door = Part(d == 0 ? "DoorL" : "DoorR", doorMeshes[d], paint, root.transform, true);
                    door.transform.localPosition = hinges[d];
                    door.gameObject.SetActive(false);
                    doors[d] = door.transform;
                    nearParts.Add(door);
                }
                var wellsPart = Part("Wells", wellsMesh, interior, root.transform, false);
                wellsPart.gameObject.SetActive(false);
                nearParts.Add(wellsPart);
                for (int i = 0; i < 4; i++)
                {
                    // The wheels, in the holes of the near shell.
                    var holder = new GameObject("Wheel" + i).transform;
                    holder.SetParent(root.transform, false);
                    holder.localPosition = wheelPos[i] + Vector3.up * spec.WheelRadius;
                    var disc = Part("Disc", wheelMesh, wheelMat, holder, false);
                    disc.transform.localScale = new Vector3(0.18f, spec.WheelRadius * 1.02f, spec.WheelRadius * 1.02f);
                    holder.gameObject.SetActive(false);
                    wheels[i] = holder;
                    nearParts.Add(disc);
                }
                // Up close a car is the cut shell with its wheels and doors, whether it is parked or driven; far off, the simple shell alone.
                lod.SetLODs(new[] { new LOD(0.22f, nearParts.ToArray()), new LOD(0.012f, new Renderer[] { r1 }) });
                lod.RecalculateBounds();

                Vector3 steeringLocal = new(-0.38f, WheelHeight(spec), spec.SeatZ + 0.05f + WheelDistance);

                var lamps = Part("Lamps", LampMesh(spec, lod0, $"{Art.Gen}/Vehicles/{spec.Id}_lamps.asset", out Vector3 headCenter, out Vector3 tailCenter), lampMat, root.transform, false);
                lamps.enabled = false;

                var lights = new Light[2];
                for (int i = 0; i < 2; i++)
                {
                    var go = new GameObject("Headlight" + i);
                    go.transform.SetParent(root.transform, false);
                    go.transform.localPosition = new Vector3((i == 0 ? -1 : 1) * (spec.Size.x * .5f - .34f), headCenter.y, spec.Size.z * .5f);
                    go.transform.localRotation = Quaternion.Euler(7f, 0, 0);
                    var l = go.AddComponent<Light>();
                    l.type = LightType.Spot; l.spotAngle = 62f; l.innerSpotAngle = 25f; l.range = 42f; l.intensity = 110f; l.color = new Color(1f, .95f, .82f);
                    l.shadows = LightShadows.None; l.enabled = false;
                    lights[i] = l;
                }

                var v = root.AddComponent<Vehicle>();
                v.SeatLocal = new Vector3(-0.38f, spec.EyeHeight - 1.5f, spec.SeatZ);
                v.SteeringLocal = steeringLocal;
                v.ExitLocal = new Vector3(-spec.Size.x * .5f - 0.75f, 0.1f, spec.SeatZ);
                v.ExhaustLocal = new Vector3(0.45f, 0.3f, -spec.Size.z * .5f - 0.05f);
                v.WheelLocal = wheelPos;
                v.WheelRadius = spec.WheelRadius;
                v.BodySize = spec.Size;
                v.WheelVisuals = wheels;
                v.CutBody = cutBody;
                v.Doors = doors;
                v.Wells = wellsPart.gameObject;
                v.LampRenderer = lamps;
                v.BodyRenderers = new Renderer[] { r0, r1 };
                v.CharredMaterial = charred;
                v.Headlights = lights;

                result[spec.Id] = PrefabUtility.SaveAsPrefabAsset(root, $"{Art.Gen}/Vehicles/{spec.Id}.prefab");
                Object.DestroyImmediate(root);
            }
            return result;
        }

        /// <summary>
        /// root (movement) -> Body (yawed so the toes point along +Z, holds CharacterRig) -> Model (the FBX with its Animator, its own
        /// rotation untouched). The rig reads forward and up from Body, so they are true world directions.
        /// </summary>
        static GameObject BuildBody(string model, Transform parent, int team, bool selective, float smoothness)
        {
            var holder = new GameObject("Body");
            holder.transform.SetParent(parent, false);
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{Art.Root}/Characters/{model}.fbx");
            var body = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            body.name = "Model";
            body.transform.SetParent(holder.transform, false);
            var mat = Art.ModelMat("Characters", model, selective, smoothness);
            float height = 0f;
            foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                r.sharedMaterial = mat;
                r.updateWhenOffscreen = false;
                height = r.bounds.size.y;
                r.localBounds = new Bounds(r.localBounds.center, r.localBounds.size * 1.6f);
            }
            // Face +Z: the toes tell which way the model looks.
            var map = body.GetComponentsInChildren<Transform>().GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            Vector3 toes = (map["LeftToeBase"].position - map["LeftFoot"].position + map["RightToeBase"].position - map["RightFoot"].position);
            toes.y = 0;
            float yaw = Vector3.SignedAngle(toes.normalized, Vector3.forward, Vector3.up);
            yaw = Mathf.Round(yaw / 90f) * 90f;
            holder.transform.localRotation = Quaternion.Euler(0, yaw, 0);
            holder.transform.localScale = Vector3.one * (TargetHeight(model) / Mathf.Max(height, 0.1f));

            var anim = body.GetComponent<Animator>();
            if (anim == null) anim = body.AddComponent<Animator>();
            float bodyScale = TargetHeight(model) / Mathf.Max(height, 0.1f);
            anim.runtimeAnimatorController = BuildController(model, bodyScale, out var facts);
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var rig = holder.AddComponent<CharacterRig>();
            rig.Animator = anim;
            rig.Team = team;
            rig.WalkClipSpeed = facts.WalkPace;
            rig.SprintClipSpeed = facts.SprintPace;
            rig.CrouchClipSpeed = facts.CrouchPace;
            rig.ArmedRunClipSpeed = facts.ArmedRunPace;
            rig.ArmedCrouchClipSpeed = facts.ArmedCrouchPace;
            rig.ArmedWalkClipSpeed = facts.ArmedWalkPace;
            rig.ThrowSeconds = facts.ThrowSeconds;
            rig.ThrowRelease = facts.ThrowRelease;
            rig.ClipArms = facts.ClipArms;
            rig.ArmSpread = facts.ArmSpread;
            rig.PunchStates = facts.PunchStates;
            rig.PunchHands = facts.PunchHands;
            rig.PunchOffsets = facts.PunchOffsets;
            var smr = body.GetComponentInChildren<SkinnedMeshRenderer>();
            Debug.Log($"[BUILD] {model}: height {height:F2} root rotation {fbx.transform.localEulerAngles} facing yaw {yaw} bones {smr.bones.Length} clips {anim.runtimeAnimatorController.animationClips.Length}");
            return holder;
        }

        public static GameObject BuildPlayerPrefab(WeaponData[] weapons)
        {
            Art.EnsureFolder($"{Art.Gen}/Prefabs");
            var root = new GameObject("Player");
            root.layer = Layers.Character;
            root.AddComponent<Health>();
            var cc = root.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.32f; cc.center = new Vector3(0, 0.92f, 0); cc.stepOffset = 0.35f; cc.slopeLimit = 50f;
            BuildBody("player", root.transform, 0, false, 0.3f);
            var pc = root.AddComponent<PlayerController>();
            pc.Weapons = weapons;
            var p = PrefabUtility.SaveAsPrefabAsset(root, $"{Art.Gen}/Prefabs/Player.prefab");
            Object.DestroyImmediate(root);
            return p;
        }

        public static GameObject BuildPedestrianPrefab(string model)
        {
            var root = new GameObject("Pedestrian_" + model);
            root.layer = Layers.Character;
            root.AddComponent<Health>();
            var ag = root.AddComponent<NavMeshAgent>();
            ag.radius = 0.35f; ag.height = 1.8f; ag.speed = 1.5f; ag.angularSpeed = 300f; ag.acceleration = 8f; ag.stoppingDistance = 0.3f;
            ag.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.height = 1.8f; capsule.radius = 0.3f; capsule.center = new Vector3(0, 0.9f, 0); capsule.isTrigger = true;
            BuildBody(model, root.transform, 2, true, 0.25f);
            root.AddComponent<PedestrianAI>();
            var p = PrefabUtility.SaveAsPrefabAsset(root, $"{Art.Gen}/Prefabs/Pedestrian_{model}.prefab");
            Object.DestroyImmediate(root);
            return p;
        }

        public static GameObject BuildEnemyPrefab(string model)
        {
            var root = new GameObject("Enemy_" + model);
            root.layer = Layers.Character;
            root.AddComponent<Health>();
            var ag = root.AddComponent<NavMeshAgent>();
            ag.radius = 0.35f; ag.height = 1.8f; ag.speed = 5f; ag.angularSpeed = 720f; ag.acceleration = 26f; ag.stoppingDistance = 0.2f; ag.autoBraking = true;
            ag.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.height = 1.8f; capsule.radius = 0.3f; capsule.center = new Vector3(0, 0.9f, 0); capsule.isTrigger = true;
            BuildBody(model, root.transform, 1, true, 0.25f);
            root.AddComponent<EnemyAI>();
            var p = PrefabUtility.SaveAsPrefabAsset(root, $"{Art.Gen}/Prefabs/Enemy_{model}.prefab");
            Object.DestroyImmediate(root);
            return p;
        }
    }
}
