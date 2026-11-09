using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace GreatHeistAuto
{
    /// <summary>
    /// The animation test bench (scene AnimLab, button on the main menu). Every character, every clip, and any combination of gait, direction,
    /// crouch, gun, hold and view, on a floor that scrolls at the walking speed so sliding feet show. With -ghalab it plays through the
    /// combinations by itself, measures them and saves pictures (tools/verify_anim.py).
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class AnimLab : MonoBehaviour
    {
        public const string SceneName = "AnimLab";

        [Serializable]
        public class Entry
        {
            public string Name;
            public GameObject Prefab;
            public AnimationClip[] Clips;
            public Color Tint = Color.white;
        }

        public Entry[] Characters;
        public WeaponData[] Weapons;
        public GameObject CarPrefab;
        public Camera Cam;
        public Renderer Floor;

        enum Hold { Aim, Sights, Lowered }

        static readonly string[] Gaits = { "Idle", "Walk", "Jog", "Sprint" };
        static readonly string[] Directions = { "Fwd", "Back", "Left", "Right", "Diag" };
        static readonly string[] Holds = { "Aim", "Sights", "Lowered" };
        static readonly float[] TimeScales = { 1f, 0.5f, 0.25f, 0.1f };
        static readonly (string name, Vector2 orbit, float dist, float height)[] Views =
        {
            ("3/4", new Vector2(-145f, 8f), 4.2f, 1f), ("Front", new Vector2(180f, 4f), 4.2f, 1f), ("Side", new Vector2(90f, 4f), 4.2f, 1f), ("Back", new Vector2(0f, 8f), 4.2f, 1f),
            ("Hands", new Vector2(150f, 6f), 1.7f, 1.05f), ("Feet", new Vector2(100f, 6f), 2.2f, 0.25f), ("Top", new Vector2(180f, 72f), 4.2f, 1f),
        };

        // What is being shown.
        int character, gait = 1, direction, weapon = -1, hold, timeScaleIndex, rawClip = -1, style, deaths, gestures;
        float customSpeed = -1f;
        bool crouch, allCharacters, driving, inAir, paused, stepOnce;

        class Actor
        {
            public Entry Entry;
            public GameObject Root;
            public CharacterRig Rig;
            public Vector3 Home;
            public PlayableGraph Graph;
            public AnimationClipPlayable Playable;
            public bool HasGraph;
            public Vehicle Car;
        }

        readonly List<Actor> actors = new();
        readonly List<Action> refreshers = new();
        Transform inactive;
        Vector2 orbit = new(-145f, 8f);
        float distance = 4.2f, targetHeight = 1f, reloadUntil, rawStart, airUntil;
        Vector2 floorScroll;
        Material floorMat;
        Text readout;
        Canvas canvas;
        bool testing;

        // ---- set-up ----

        void Awake()
        {
            inactive = new GameObject("Spawning").transform;
            inactive.gameObject.SetActive(false);
            floorMat = Floor.material;
            floorMat.SetTexture("_BaseMap", GridTexture());
            floorMat.SetTextureScale("_BaseMap", new Vector2(60f, 60f));
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Start()
        {
            BuildUi();
            Respawn();
            testing = Environment.GetCommandLineArgs().Contains("-ghalab");
            if (testing) StartCoroutine(RunTest());
        }

        static Texture2D GridTexture()
        {
            const int S = 128;
            var t = new Texture2D(S, S, TextureFormat.RGB24, true) { filterMode = FilterMode.Trilinear, anisoLevel = 8, wrapMode = TextureWrapMode.Repeat };
            var c = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    bool line = x < 3 || y < 3;
                    bool half = x % (S / 2) < 2 || y % (S / 2) < 2;
                    bool checker = (x < S / 2) ^ (y < S / 2);
                    float v = line ? 0.08f : half ? 0.2f : checker ? 0.42f : 0.5f;
                    c[y * S + x] = new Color(v, v, v * 1.04f);
                }
            t.SetPixels(c);
            t.Apply();
            return t;
        }

        void Respawn()
        {
            foreach (var a in actors) Remove(a);
            actors.Clear();
            if (dummy != null) { Remove(dummy); dummy = null; }
            var list = allCharacters ? Characters : new[] { Characters[character] };
            for (int i = 0; i < list.Length; i++)
                actors.Add(Spawn(list[i], new Vector3((i - (list.Length - 1) * .5f) * 1.5f, 0f, 0f)));
            ApplyWeapon();
            ApplyRaw();
            ApplyDriving();
            ApplyView();
        }

        Actor Spawn(Entry e, Vector3 pos)
        {
            var go = Instantiate(e.Prefab, pos, Quaternion.identity, inactive);
            // Only the body: nothing that walks by itself.
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c is EnemyAI || c is PedestrianAI || c is PlayerController) DestroyImmediate(c);
            foreach (var type in new[] { typeof(UnityEngine.AI.NavMeshAgent), typeof(CharacterController), typeof(CapsuleCollider) })
                if (go.TryGetComponent(type, out var comp)) DestroyImmediate(comp);
            go.transform.SetParent(null, true);
            var rig = go.GetComponentInChildren<CharacterRig>(true);
            rig.SetTint(e.Tint);
            return new Actor { Entry = e, Root = go, Rig = rig, Home = pos };
        }

        void Remove(Actor a)
        {
            if (a.HasGraph) { a.Graph.Destroy(); a.HasGraph = false; }
            if (a.Car != null) Destroy(a.Car.gameObject);
            if (a.Root == null) return;
            Destroy(a.Root);
        }

        void ApplyWeapon()
        {
            foreach (var a in actors)
            {
                if (weapon >= 0) a.Rig.SetWeapon(Weapons[weapon]);
                a.Rig.Holstered = weapon < 0;
            }
            Refresh();
        }

        void ApplyRaw()
        {
            foreach (var a in actors)
            {
                if (a.HasGraph) { a.Graph.Destroy(); a.HasGraph = false; }
                a.Rig.RawPlayback = rawClip >= 0;
                if (rawClip < 0) continue;
                var clip = ClipOf(a.Entry);
                if (clip == null) continue;
                a.Playable = AnimationPlayableUtilities.PlayClip(a.Rig.Animator, clip, out a.Graph);
                a.Graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                a.HasGraph = true;
            }
            if (rawClip >= 0 && weapon >= 0) { weapon = -1; ApplyWeapon(); }
            rawStart = Time.time;
            Refresh();
        }

        AnimationClip ClipOf(Entry e)
        {
            // Pedestrians have two clips more: the names decide, not the positions.
            string name = Characters[character].Clips[Mathf.Clamp(rawClip, 0, Characters[character].Clips.Length - 1)].name;
            return e.Clips.FirstOrDefault(c => c.name == name);
        }

        void ApplyDriving()
        {
            foreach (var a in actors)
            {
                if (driving && a.Car == null)
                {
                    var car = Instantiate(CarPrefab, a.Home, Quaternion.identity);
                    a.Car = car.GetComponent<Vehicle>();
                    a.Car.SetDrivenVisuals(true);
                    a.Root.transform.SetParent(car.transform, false);
                    a.Root.transform.SetLocalPositionAndRotation(a.Car.SeatLocal, Quaternion.identity);
                    a.Rig.EnterSeat(a.Car);
                }
                else if (!driving && a.Car != null)
                {
                    a.Rig.LeaveSeat();
                    a.Root.transform.SetParent(null, true);
                    a.Root.transform.SetPositionAndRotation(a.Home, Quaternion.identity);
                    Destroy(a.Car.gameObject);
                    a.Car = null;
                }
            }
            Refresh();
        }

        void ApplyView()
        {
            Cam.fieldOfView = 40f;
            Refresh();
        }

        // ---- each frame ----

        float SpeedFor(Actor a)
        {
            if (customSpeed >= 0f) return customSpeed;
            if (crouch) return gait == 0 ? 0f : a.Rig.CrouchClipSpeed;
            return gait switch { 0 => 0f, 1 => 1.7f, 2 => 3.8f, _ => 6.6f };
        }

        Vector3 Heading => direction switch
        {
            1 => Vector3.back, 2 => Vector3.left, 3 => Vector3.right, 4 => new Vector3(1f, 0f, 1f).normalized, _ => Vector3.forward
        };

        void Update()
        {
            ReadCameraInput();

            if (paused && !stepOnce) Time.timeScale = 0f;
            else Time.timeScale = TimeScales[timeScaleIndex];
            stepOnce = false;

            bool air = inAir || Time.time < airUntil;
            float speed = 0f;
            foreach (var a in actors)
            {
                var rig = a.Rig;
                speed = driving ? 0f : SpeedFor(a);
                rig.Velocity = a.Root.transform.TransformDirection(Heading) * speed;
                rig.Crouching = crouch;
                rig.Grounded = !air;
                rig.AimingDownSights = hold == (int)Hold.Sights;
                rig.AimWeightTarget = hold == (int)Hold.Lowered ? 0f : 1f;
                rig.Sprinting = gait == 3;
                rig.Style = (CharacterRig.Gait)style;
                rig.AimPoint = a.Root.transform.position + a.Root.transform.forward * 25f + Vector3.up * 1.35f;
                rig.ReloadWeight = Time.time < reloadUntil ? Mathf.Clamp01(Mathf.Min(reloadUntil - Time.time, 1.8f - (reloadUntil - Time.time)) * 4f) : 0f;

                if (a.HasGraph)
                {
                    var clip = a.Playable.GetAnimationClip();
                    double t = clip.isLooping ? Time.time - rawStart : Mathf.Repeat(Time.time - rawStart, clip.length + 0.6f);
                    a.Playable.SetTime(t);
                }
            }

            // The jump itself, as the player's physics has it.
            // The player's jump: up at JumpSpeed, down under its gravity, for the time that takes.
            float airTime = 2f * CharacterRig.JumpSpeed / CharacterRig.JumpGravity, since = airTime - (airUntil - Time.time);
            float hop = Time.time < airUntil ? CharacterRig.JumpSpeed * since - 0.5f * CharacterRig.JumpGravity * since * since : 0f;
            foreach (var a in actors) if (a.Car == null) a.Root.transform.position = a.Home + Vector3.up * Mathf.Max(0f, hop);

            floorScroll += new Vector2(Heading.x, Heading.z) * (speed * Time.deltaTime);
            floorMat.SetTextureOffset("_BaseMap", new Vector2(Mathf.Repeat(floorScroll.x, 1f), Mathf.Repeat(floorScroll.y, 1f)));

            if (readout != null) readout.text = Describe(speed);
        }

        void LateUpdate()
        {
            Vector3 centre = focus != null ? focus.position : Vector3.up * targetHeight;
            if (driving && actors.Count > 0) centre = actors[0].Home + Vector3.up * 0.9f;
            Quaternion q = Quaternion.Euler(orbit.y, orbit.x, 0f);
            float zoom = allCharacters ? distance * 1.9f : distance;
            Cam.transform.position = centre - q * Vector3.forward * zoom;
            Cam.transform.rotation = q;
        }

        void ReadCameraInput()
        {
            var m = Mouse.current;
            if (m == null || testing) return;
            float panel = PanelWidth * canvas.scaleFactor;
            if (m.position.ReadValue().x < panel) return;
            if (m.rightButton.isPressed || m.middleButton.isPressed || m.leftButton.isPressed)
            {
                Vector2 d = m.delta.ReadValue() * 0.25f;
                orbit.x += d.x;
                orbit.y = Mathf.Clamp(orbit.y - d.y, -30f, 85f);
            }
            float s = m.scroll.ReadValue().y;
            if (Mathf.Abs(s) > 0.1f) distance = Mathf.Clamp(distance * (s > 0f ? 0.9f : 1.1f), 0.7f, 14f);
        }

        string Describe(float speed)
        {
            var sb = new StringBuilder();
            if (actors.Count == 0) return "";
            var a = actors[0];
            sb.Append(a.Entry.Name).Append("   ").Append(speed.ToString("F1")).Append(" m/s   ");
            if (rawClip >= 0) { var c = ClipOf(a.Entry); sb.Append("raw clip: ").Append(c != null ? c.name : "none on this model"); }
            else
            {
                sb.Append("clips: ");
                foreach (var i in a.Rig.Animator.GetCurrentAnimatorClipInfo(0).OrderByDescending(i => i.weight))
                    if (i.weight > 0.02f) sb.Append(i.clip.name).Append(' ').Append((i.weight * 100f).ToString("F0")).Append("%  ");
                sb.Append("  play speed ").Append(a.Rig.Animator.GetFloat("MoveSign").ToString("F2"));
            }
            sb.Append("   palms R ").Append(a.Rig.PalmFacing(1).ToString("F2")).Append(" L ").Append(a.Rig.PalmFacing(-1).ToString("F2"));
            return sb.ToString();
        }

        // ---- actions ----

        void Fire() { foreach (var a in actors) a.Rig.Recoil(3f); }
        void Reload() => reloadUntil = Time.time + 1.8f;
        void Flinch() { foreach (var a in actors) a.Rig.Flinch(a.Root.transform.forward); }
        void Roll() { foreach (var a in actors) a.Rig.PlayRoll(); }
        void Punch() { foreach (var a in actors) a.Rig.PlayPunch(); }
        void Jump()
        {
            airUntil = Time.time + 2f * CharacterRig.JumpSpeed / CharacterRig.JumpGravity;
            foreach (var a in actors) a.Rig.PlayJump();
        }
        void Door() { foreach (var a in actors) if (a.Car != null) a.Car.OpenDoor(0, 1.2f); }
        void Knock() { foreach (var a in actors) a.Rig.KnockDown(); }
        void Throw() { foreach (var a in actors) a.Rig.PlayThrow(); }
        void DodgeLeft() { foreach (var a in actors) a.Rig.PlayDodge(-1); }
        void DodgeRight() { foreach (var a in actors) a.Rig.PlayDodge(1); }
        void Gesture() { gestures++; foreach (var a in actors) a.Rig.PlayGesture(gestures); }

        Actor dummy;
        Transform focus;

        /// <summary>A punch at someone standing in front, to see that the fist stops at the body.</summary>
        void PunchDummy()
        {
            if (dummy == null)
            {
                dummy = Spawn(Characters[4], actors[0].Home + Vector3.forward * Melee.StandOff);
                dummy.Root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                dummy.Rig.Holstered = true;
            }
            actors[0].Rig.PlayPunch(dummy.Rig);
            StartCoroutine(HitDummy());
        }

        IEnumerator HitDummy()
        {
            yield return new WaitForSeconds(CharacterRig.PunchLandsAfter);
            if (dummy == null) yield break;
            dummy.Rig.Flinch(Vector3.forward);
            Knockback.Apply(dummy.Root, Vector3.forward, 0.5f);
            yield return new WaitForSeconds(0.9f);
            if (dummy != null) dummy.Root.transform.position = actors[0].Home + Vector3.forward * Melee.StandOff;
        }

        /// <summary>Each press a shot from another side: the front, behind, the right.</summary>
        void Die()
        {
            Vector3[] shots = { Vector3.back, Vector3.forward, Vector3.left };
            Vector3 shot = shots[deaths++ % shots.Length];
            foreach (var a in actors) a.Rig.Die(a.Root.transform.TransformDirection(shot));
            StartCoroutine(ReviveLater());
        }

        IEnumerator ReviveLater()
        {
            yield return new WaitForSeconds(3.2f);
            foreach (var a in actors)
            {
                a.Rig.Revive();
                a.Rig.Holstered = weapon < 0;
            }
        }

        // ---- panel ----

        const float PanelWidth = 390f;

        void Refresh() { foreach (var r in refreshers) r(); }

        void BuildUi()
        {
            canvas = UiKit.MakeCanvas("Lab");
            UiKit.EnsureEventSystem();
            var root = canvas.transform;
            var bg = UiKit.Img(root, "Panel", new Vector2(0f, .5f), Vector2.zero, new Vector2(PanelWidth, 1080f), new Color(.04f, .05f, .07f, .8f));
            bg.rectTransform.pivot = new Vector2(0f, .5f);
            bg.raycastTarget = true;
            var top = UiKit.Img(root, "Bar", new Vector2(.5f, 1f), new Vector2(PanelWidth * .5f, -22f), new Vector2(1920f - PanelWidth, 44f), new Color(.04f, .05f, .07f, .7f));
            top.rectTransform.pivot = new Vector2(.5f, .5f);
            readout = UiKit.Label(root, "Readout", new Vector2(.5f, 1f), new Vector2(PanelWidth * .5f, -22f), new Vector2(1500f, 40f), "", 21, TextAnchor.MiddleCenter, Color.white);
            readout.rectTransform.pivot = new Vector2(.5f, .5f);

            float y = -14f;
            void Section(string title)
            {
                var t = UiKit.Label(root, title, new Vector2(0f, 1f), new Vector2(12f, y), new Vector2(PanelWidth - 20f, 22f), title, 17, TextAnchor.MiddleLeft, new Color(.75f, .8f, .9f));
                t.rectTransform.pivot = new Vector2(0f, 1f);
                y -= 23f;
            }
            void Row(string[] labels, Func<int, bool> on, Action<int> click, int perRow = 4, float height = 31f)
            {
                float w = (PanelWidth - 20f) / perRow;
                for (int i = 0; i < labels.Length; i++)
                {
                    int index = i;
                    var b = UiKit.MakeButton(root, labels[i], new Vector2(0f, 1f), new Vector2(10f + (i % perRow) * w, y - (i / perRow) * (height + 2f)), new Vector2(w - 3f, height), labels[i], 16);
                    b.GetComponent<RectTransform>().pivot = new Vector2(0f, 1f);
                    b.onClick.AddListener(() => { click(index); Refresh(); });
                    var image = b.GetComponent<Image>();
                    refreshers.Add(() => { image.color = on(index) ? new Color(.86f, .55f, .12f) : new Color(.2f, .23f, .28f); });
                }
                y -= Mathf.Ceil(labels.Length / (float)perRow) * (height + 2f) + 3f;
            }

            Section("Character");
            Row(Characters.Select(c => c.Name).ToArray(), i => character == i && !allCharacters, i => { character = i; allCharacters = false; Respawn(); }, 3);
            Row(new[] { "All six side by side" }, _ => allCharacters, _ => { allCharacters = !allCharacters; Respawn(); }, 1);
            Section("Gait");
            Row(Gaits, i => gait == i && customSpeed < 0f && rawClip < 0, i => { gait = i; customSpeed = -1f; Raw(-1); });
            Row(new[] { "-0.5 m/s", "+0.5 m/s" }, _ => false, i => { customSpeed = Mathf.Clamp((customSpeed >= 0f ? customSpeed : SpeedFor(actors[0])) + (i == 0 ? -.5f : .5f), 0f, 9f); Raw(-1); }, 2);
            Section("Direction");
            Row(Directions, i => direction == i, i => direction = i, 5);
            Row(new[] { "Normal", "Drunk", "Injured" }, i => style == i, i => { style = i; Raw(-1); }, 3);
            Row(new[] { "Crouch", "In the air", "Driving" }, i => i == 0 ? crouch : i == 1 ? inAir : driving, i =>
            {
                if (i == 0) crouch = !crouch;
                else if (i == 1) inAir = !inAir;
                else { driving = !driving; ApplyDriving(); }
                Raw(-1);
            }, 3);
            Section("Gun");
            Row(new[] { "None" }.Concat(Weapons.Select(w => w.name)).ToArray(), i => weapon == i - 1, i => { weapon = i - 1; Raw(-1); ApplyWeapon(); }, 3);
            Row(Holds, i => hold == i, i => hold = i, 3);
            Section("Actions");
            Row(new[] { "Fire", "Reload", "Flinch", "Roll", "Punch", "Punch dummy", "Jump", "Die", "Car door", "Knocked over", "Grenade", "Dodge left", "Dodge right", "Gesture" }, _ => false, i =>
            {
                Raw(-1);
                new Action[] { Fire, Reload, Flinch, Roll, Punch, PunchDummy, Jump, Die, Door, Knock, Throw, DodgeLeft, DodgeRight, Gesture }[i]();
            });
            Section("Single clip, nothing on top");
            Row(Characters[0].Clips.Concat(Characters[4].Clips).Select(c => c.name).Distinct().ToArray(), i => rawClip == RawIndexOf(i), i => Raw(RawIndexOf(i)), 3, 26f);
            Section("View");
            Row(Views.Select(v => v.name).ToArray(), _ => false, i => SetView(i), 4);
            Section("Time");
            Row(TimeScales.Select(t => t + "x").ToArray(), i => timeScaleIndex == i, i => timeScaleIndex = i);
            Row(new[] { "Pause", "One frame", "Main menu" }, i => i == 0 && paused, i =>
            {
                if (i == 0) paused = !paused;
                else if (i == 1) stepOnce = true;
                else { Time.timeScale = 1f; SceneManager.LoadScene(0); }
            }, 3);
            Refresh();
        }

        int RawIndexOf(int shown)
        {
            var names = Characters[0].Clips.Concat(Characters[4].Clips).Select(c => c.name).Distinct().ToArray();
            return Array.FindIndex(Characters[character].Clips, c => c.name == names[shown]);
        }

        void Raw(int index)
        {
            if (index == rawClip) return;
            rawClip = index;
            ApplyRaw();
        }

        void SetView(int i)
        {
            orbit = Views[i].orbit;
            distance = Views[i].dist;
            targetHeight = Views[i].height;
        }

        // ---- the automatic run ----

        /// <summary>Tests the menu's button with a real mouse click through the input system, so the path a person takes is covered.</summary>
        public static IEnumerator ClickFromMenu(Button button)
        {
            yield return new WaitForSecondsRealtime(1.5f);
            var mouse = Mouse.current ?? InputSystem.AddDevice<Mouse>();
            var corners = new Vector3[4];
            button.GetComponent<RectTransform>().GetWorldCorners(corners);
            Vector2 at = RectTransformUtility.WorldToScreenPoint(null, (corners[0] + corners[2]) * .5f);
            Debug.Log($"[LAB] clicking the menu button at {at}");
            InputSystem.QueueStateEvent(mouse, new MouseState { position = at });
            yield return new WaitForSecondsRealtime(0.2f);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = at }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left));
            yield return new WaitForSecondsRealtime(0.2f);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = at });
            yield return new WaitForSecondsRealtime(2.5f);
            Debug.Log("[LAB] MENUCLICK not delivered, opening the lab directly");
            SceneManager.LoadScene(SceneName);
        }

        /// <summary>Fast wobble in millimetres: what is left after a local quadratic fit over 0.1 s at 120 samples a second, which keeps a stride's curve.</summary>
        public static float Tremor(IReadOnlyList<Vector3> p)
        {
            const int m = 6;
            if (p.Count < 2 * m + 1) return 0f;
            float norm = (2 * m + 3) * (2 * m + 1) * (2 * m - 1) / 3f;
            double sum = 0; int n = 0;
            for (int i = m; i < p.Count - m; i++)
            {
                Vector3 fit = Vector3.zero;
                for (int j = -m; j <= m; j++) fit += p[i + j] * ((3 * m * m + 3 * m - 1 - 5 * j * j) / norm);
                sum += (p[i] - fit).sqrMagnitude; n++;
            }
            return Mathf.Sqrt((float)(sum / n)) * 1000f;
        }

        struct Numbers { public float Head, Hand, Gun, PalmR, PalmL, Slide; public string Clip; }

        /// <summary>Records the current combination for 'seconds' of game time at 120 frames a second.</summary>
        IEnumerator Measure(float seconds, Action<Numbers> done)
        {
            var a = actors[0];
            var body = a.Root.transform;
            var head = new List<Vector3>(); var hand = new List<Vector3>(); var gun = new List<Vector3>();
            var feet = new[] { a.Root.GetComponentsInChildren<Transform>().First(t => t.name == "LeftFoot"), a.Root.GetComponentsInChildren<Transform>().First(t => t.name == "RightFoot") };
            var footPos = new[] { new List<Vector3>(), new List<Vector3>() };
            float palmR = 0f, palmL = 0f; int n = 0;
            float end = Time.time + seconds;
            var clipTally = new Dictionary<string, int>();
            while (Time.time < end)
            {
                yield return new WaitForEndOfFrame();
                head.Add(body.InverseTransformPoint(a.Rig.Bone("Head").position));
                hand.Add(body.InverseTransformPoint(a.Rig.Bone("RightHand").position));
                if (a.Rig.Muzzle != null) gun.Add(body.InverseTransformPoint(a.Rig.Muzzle.position));
                for (int k = 0; k < 2; k++) footPos[k].Add(body.InverseTransformPoint(feet[k].position));
                palmR += a.Rig.PalmFacing(1); palmL += a.Rig.PalmFacing(-1); n++;
                string c = a.Rig.MainClip();
                clipTally[c] = clipTally.TryGetValue(c, out int v) ? v + 1 : 1;
            }
            // A foot on the ground should move back past the body at the body's speed (the same measure as the builder's clip pace).
            float speed = a.Rig.Velocity.magnitude;
            var backs = new List<float>();
            Vector3 along = speed > 0.01f ? body.InverseTransformDirection(a.Rig.Velocity / speed) : Vector3.forward;
            foreach (var track in footPos)
            {
                float line = Mathf.Lerp(track.Min(p => p.y), track.Max(p => p.y), 0.35f);
                for (int i = 1; i < track.Count; i++)
                {
                    float back = Vector3.Dot(track[i - 1] - track[i], along) * 120f;
                    if (track[i].y < line && back > 0f && speed > 0.01f) backs.Add(back);
                }
            }
            backs.Sort();
            done(new Numbers
            {
                Head = Tremor(head), Hand = Tremor(hand), Gun = Tremor(gun), PalmR = palmR / n, PalmL = palmL / n,
                Slide = backs.Count > 0 ? Mathf.Abs(backs[backs.Count / 2] - speed) : 0f,
                Clip = clipTally.OrderByDescending(kv => kv.Value).First().Key,
            });
        }

        IEnumerator Shot(string dir, string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, name + ".jpg"), tex.EncodeToJPG(85));
            Destroy(tex);
        }

        IEnumerator RunTest()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-ghashots");
            string dir = at >= 0 ? args[at + 1] : Application.persistentDataPath;
            Directory.CreateDirectory(dir);
            Debug.Log("[LAB] scene open");
            yield return null;

            // The panel, with a real click on it: "Jog".
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Shot(dir, "00_panel");
            var mouse = Mouse.current ?? InputSystem.AddDevice<Mouse>();
            var jog = canvas.GetComponentsInChildren<Button>().First(b => b.name == "Jog");
            var corners = new Vector3[4];
            jog.GetComponent<RectTransform>().GetWorldCorners(corners);
            Vector2 click = RectTransformUtility.WorldToScreenPoint(null, (corners[0] + corners[2]) * .5f);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = click });
            yield return new WaitForSecondsRealtime(0.2f);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = click }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left));
            yield return new WaitForSecondsRealtime(0.2f);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = click });
            yield return new WaitForSecondsRealtime(0.3f);
            Debug.Log($"[LAB] PANELCLICK {(gait == 2 ? "ok" : "not delivered")}");
            gait = 1;

            canvas.enabled = false;
            Time.captureDeltaTime = 1f / 120f;
            var report = new StringBuilder();

            IEnumerator One(string label, float seconds = 2f)
            {
                yield return new WaitForSeconds(0.7f);
                Numbers r = default;
                yield return Measure(seconds, x => r = x);
                string line = $"[LAB] {label} clip={r.Clip} tremor_mm head={r.Head:F1} hand={r.Hand:F1} gun={r.Gun:F1} palmR={r.PalmR:F2} palmL={r.PalmL:F2} slide={r.Slide:F2}";
                report.AppendLine(line);
                Debug.Log(line);
            }

            // Every character on foot, hands empty.
            for (int c = 0; c < Characters.Length; c++)
            {
                character = c; weapon = -1; hold = 0; direction = 0; crouch = false; Respawn();
                for (int g = 0; g < Gaits.Length; g++)
                {
                    gait = g;
                    yield return One($"{Characters[c].Name} {Gaits[g]} unarmed");
                    SetView(0);
                    yield return Shot(dir, $"char_{Characters[c].Name}_{Gaits[g]}");
                }
            }

            // The player: every gait, gun and hold.
            character = 0; Respawn();
            for (int w = 0; w < Weapons.Length; w++)
                for (int h = 0; h < Holds.Length; h++)
                    for (int g = 0; g < Gaits.Length; g++)
                    {
                        weapon = w; hold = h; gait = g; ApplyWeapon();
                        yield return One($"player {Gaits[g]} {Weapons[w].name} {Holds[h]}", 1.2f);
                        SetView(0);
                        yield return Shot(dir, $"gun_{Weapons[w].name}_{Holds[h]}_{Gaits[g]}");
                    }

            // Directions and crouch.
            weapon = -1; hold = 0; ApplyWeapon();
            for (int d = 0; d < Directions.Length; d++)
                foreach (int g in new[] { 1, 3 })
                {
                    direction = d; gait = g;
                    yield return One($"player {Gaits[g]} {Directions[d]} unarmed", 1.2f);
                    yield return Shot(dir, $"dir_{Directions[d]}_{Gaits[g]}");
                }
            // Gun out: the eight-way clips, walking and running every way.
            foreach (int w in new[] { 0, 2 })
                for (int d = 0; d < Directions.Length; d++)
                    foreach (int g in new[] { 1, 2 })
                    {
                        weapon = w; direction = d; gait = g; hold = 0; ApplyWeapon();
                        yield return One($"player {Gaits[g]} {Directions[d]} {Weapons[w].name}", 1.2f);
                        yield return Shot(dir, $"dir_{Weapons[w].name}_{Directions[d]}_{Gaits[g]}");
                    }
            weapon = -1; ApplyWeapon();
            // Drunk and injured, standing and walking: on a pedestrian, the people that have both.
            direction = 0;
            int ped = Array.FindIndex(Characters, c => c.Name.StartsWith("ped"));
            character = ped < 0 ? 0 : ped; Respawn();
            foreach (int st in new[] { 1, 2 })
                foreach (int g in new[] { 0, 1 })
                {
                    style = st; gait = g;
                    yield return One($"{Characters[character].Name} {Gaits[g]} {(st == 1 ? "drunk" : "injured")} unarmed", 1.5f);
                    yield return Shot(dir, $"style_{(st == 1 ? "drunk" : "injured")}_{Gaits[g]}");
                }
            style = 0; character = 0; Respawn();
            direction = 0; crouch = true;
            foreach (int w in new[] { -1, 0, 2 })
                foreach (int g in new[] { 0, 1 })
                {
                    weapon = w; gait = g; ApplyWeapon();
                    yield return One($"player crouch{(g == 0 ? "Idle" : "Walk")} {(w < 0 ? "unarmed" : Weapons[w].name)}", 1.5f);
                    yield return Shot(dir, $"crouch_{(w < 0 ? "none" : Weapons[w].name)}_{g}");
                }
            crouch = false; weapon = -1; ApplyWeapon();

            // Strips through a stride, for looking at hands and legs.
            foreach (var (g, name) in new[] { (1, "walk"), (2, "jog"), (3, "sprint"), (9, "runclip") })
            {
                gait = g == 9 ? 0 : g; direction = 0;
                if (g == 9) { rawClip = Array.FindIndex(Characters[character].Clips, c => c.name == "Run"); ApplyRaw(); }
                yield return new WaitForSeconds(0.8f);
                foreach (int v in new[] { 1, 2, 4 })
                {
                    SetView(v);
                    for (int f = 0; f < 6; f++)
                    {
                        yield return new WaitForSeconds(g == 1 ? 0.12f : 0.056f);
                        yield return Shot(dir, $"strip_{name}_{Views[v].name}_{f}");
                    }
                }
            }
            rawClip = -1; ApplyRaw();

            // The hand on each gun, close up, and the roll left in the wrist.
            foreach (int w in new[] { 0, 1, 2, 3 })
            {
                weapon = w; hold = 0; gait = 0; ApplyWeapon();
                yield return new WaitForSeconds(0.5f);
                focus = actors[0].Rig.Bone("LeftHand");
                distance = 0.75f;
                foreach (var (angle, tag) in new[] { (new Vector2(-40f, 25f), "a"), (new Vector2(40f, -5f), "b"), (new Vector2(130f, 10f), "c") })
                {
                    orbit = angle;
                    yield return new WaitForSeconds(0.02f);
                    yield return Shot(dir, $"hand_{Weapons[w].name}_{tag}");
                }
                focus = null;
                Debug.Log($"[LAB] wrist {Weapons[w].name}: left {actors[0].Rig.WristRoll(-1):F0} right {actors[0].Rig.WristRoll(1):F0} degrees");
            }
            weapon = -1; ApplyWeapon();
            SetView(0);

            // A punch at someone, through the moment of contact, unarmed.
            gait = 0; SetView(2); distance = 2.4f; weapon = -1; ApplyWeapon();
            for (int f = 0; f < 6; f++)
            {
                PunchDummy();
                yield return new WaitForSeconds(0.06f + f * 0.05f);
                yield return Shot(dir, $"punch_{f}");
                yield return new WaitForSeconds(1.2f);
            }
            if (dummy != null) { Remove(dummy); dummy = null; }

            // Everything else once: actions, raw clips, driving, first person, all six.
            SetView(0); gait = 0;
            yield return new WaitForSeconds(0.2f);
            string[] actionNames = { "Fire", "Reload", "Flinch", "Roll", "Jump", "Punch", "DodgeLeft", "DodgeRight", "Gesture", "Throw", "Knock" };
            Action[] actionCalls = { Fire, Reload, Flinch, Roll, Jump, Punch, DodgeLeft, DodgeRight, Gesture, Throw, Knock };
            weapon = 0; ApplyWeapon();
            for (int i = 0; i < actionCalls.Length; i++)
            {
                if (actionNames[i] == "Gesture") { weapon = -1; ApplyWeapon(); }
                actionCalls[i]();
                // The longer ones photographed through their course.
                int shots = actionNames[i] is "Throw" or "Knock" or "Gesture" ? 4 : 1;
                for (int k = 0; k < shots; k++)
                {
                    yield return new WaitForSeconds(shots == 1 ? 0.35f : 0.7f);
                    yield return Shot(dir, shots == 1 ? $"action_{actionNames[i]}" : $"action_{actionNames[i]}_{k}");
                }
                yield return new WaitForSeconds(shots == 1 ? 0.6f : 1.5f);
                if (actionNames[i] == "Gesture") { weapon = 0; ApplyWeapon(); }
            }
            // A death from each side.
            for (int k = 0; k < 3; k++)
            {
                Die();
                yield return new WaitForSeconds(1.4f);
                yield return Shot(dir, $"action_Die_{k}");
                yield return new WaitForSeconds(2.2f);
            }
            yield return new WaitForSeconds(1f);
            weapon = -1; ApplyWeapon();
            var clips = Characters[4].Clips;
            for (int i = 0; i < clips.Length; i++)
            {
                character = 4; rawClip = i; if (i == 0) Respawn(); else ApplyRaw();
                yield return new WaitForSeconds(Mathf.Min(0.35f, clips[i].length * .5f));
                yield return Shot(dir, $"raw_{clips[i].name}");
            }
            rawClip = -1; ApplyRaw(); character = 0; Respawn();
            driving = true; ApplyDriving();
            orbit = new Vector2(-60f, 20f); distance = 5f;
            yield return new WaitForSeconds(0.6f);
            yield return Shot(dir, "driving");
            Door();
            yield return new WaitForSeconds(0.45f);
            yield return Shot(dir, "driving_door_open");
            orbit = new Vector2(-90f, 4f); distance = 3f;
            yield return new WaitForSeconds(0.1f);
            yield return Shot(dir, "driving_door_side");
            orbit = new Vector2(90f, 4f);
            yield return new WaitForSeconds(0.1f);
            yield return Shot(dir, "driving_right_side");
            yield return new WaitForSeconds(1.2f);
            foreach (var (angle, tag) in new[] { (new Vector2(130f, 12f), "front"), (new Vector2(50f, 12f), "behind"), (new Vector2(90f, 50f), "above"), (new Vector2(90f, 4f), "side") })
            {
                Door();
                orbit = angle; distance = 3.6f;
                yield return new WaitForSeconds(0.13f);
                yield return Shot(dir, $"driving_door_mid_{tag}");
                yield return new WaitForSeconds(0.25f);
                yield return Shot(dir, $"driving_door_full_{tag}");
                yield return new WaitForSeconds(1.8f);
            }
            driving = false; ApplyDriving();
            SetView(0);
            weapon = -1; allCharacters = true; Respawn();
            yield return new WaitForSeconds(0.6f);
            yield return Shot(dir, "all_six");

            File.WriteAllText(Path.Combine(dir, "lab_report.txt"), report.ToString());
            Debug.Log("[LAB] RESULT done");
            Application.Quit(0);
        }
    }
}
