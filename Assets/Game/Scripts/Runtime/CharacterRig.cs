using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace GreatHeistAuto
{
    /// <summary>
    /// Drives one character model (player or enemy). The Animator plays the body clips (Generic rig, exact LAGP playback);
    /// everything to do with the gun is procedural, in LateUpdate: legs twist towards the walking direction, the spine turns to
    /// the aim, the gun is placed in front of the shoulder, and two-bone IK puts both hands on it.
    /// When sprinting the arms let go of the aim pose: a pistol stays in the swinging right hand with the left arm free, a rifle is
    /// carried across the chest. Also handles sitting in a car and all the gore that happens to the body.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public class CharacterRig : MonoBehaviour
    {
        public Animator Animator;
        public int Team;
        public Health Health;

        // Set every frame by the player / AI:
        public Vector3 AimPoint;
        public Vector3 Velocity;
        public bool Crouching, Grounded = true, Sprinting, AimingDownSights;
        public float AimWeightTarget = 1f;

        public Transform Muzzle { get; private set; }
        public WeaponData Weapon { get; private set; }
        public float ReloadWeight { get; set; }

        // How fast each clip's feet really move, measured by the builder: the Animator plays them to match the ground speed.
        public float WalkClipSpeed = 1.2f, SprintClipSpeed = 6f, CrouchClipSpeed = 1.6f;
        /// <summary>The outer ring of the eight-way gun-out clips (sprint) and of the crouched ones: beyond it they play faster.</summary>
        public float ArmedRunClipSpeed = 4.5f, ArmedCrouchClipSpeed = 1.5f;
        /// <summary>The gun-out walk ring's own pace: slower than it, the walk clip plays slower instead of blending towards standing.</summary>
        public float ArmedWalkClipSpeed = 1.6f;
        /// <summary>The grenade throw: how long it plays, and when the grenade leaves the hand, in seconds.</summary>
        public float ThrowSeconds = 2f, ThrowRelease = 1f;
        /// <summary>The clips move the arms themselves (library idle and walk): no procedural arm swing on top.</summary>
        public bool ClipArms;
        /// <summary>Degrees the upper arms swing out so hands and forearms clear this body (measured by the builder: library clips are
        /// made for a slim body, and on a broad one the arms would sink into it).</summary>
        public float ArmSpread;
        float spreadOn = 1f;
        /// <summary>The punch clips, taken in turn: state name, the hand each strikes with (1 right, -1 left) and where in the clip it
        /// starts (seconds; a wind-up before the strike). Empty = the procedural punch, alternating hands.</summary>
        public string[] PunchStates = new string[0];
        public int[] PunchHands = new int[0];
        public float[] PunchOffsets = new float[0];
        int punchNext;
        bool punchClip;

        /// <summary>The jump's take-off speed (m/s) under the player's gravity of 16 m/s2: 0.86 m high. The rise clip is timed to it.</summary>
        public const float JumpSpeed = 5.25f, JumpGravity = 16f;
        public const float JumpRiseSeconds = JumpSpeed / JumpGravity;

        /// <summary>How this person walks when the gun is away: drunk and injured have clips of their own.</summary>
        public enum Gait { Normal = 0, Drunk = 1, Injured = 2 }
        public Gait Style;
        /// <summary>Up to this speed the body uses the walk clip alone, just quicker: no running legs on someone walking.</summary>
        public const float WalkGaitLimit = 2.3f;
        /// <summary>The lab plays any clip straight onto the bones: nothing procedural on top.</summary>
        public bool RawPlayback;
        public Vehicle Seat { get; private set; }

        /// <summary>Weapon put away (or none at all): the arms just play the animation.</summary>
        public bool Holstered
        {
            get => holstered;
            set
            {
                holstered = value;
                if (gunRoot != null) gunRoot.gameObject.SetActive(!value && Seat == null);
            }
        }
        bool holstered;

        /// <summary>Gore happened here, this hard: the HUD uses it to splash the screen when it was close to the camera.</summary>
        public static event Action<Vector3, float> GoreAt;

        Transform hips, spine, spine1, spine2, neck, head, shoulderR, shoulderL;
        Quaternion shoulderRRest, shoulderLRest;
        Arm right, left;
        Transform gunRoot, gunSupport, gunModel;
        float aimWeight = 1f, legYaw, recoilBack, recoilPitch, flinch, footTimer, ikWeight = 1f;
        Vector3 flinchAxis;
        bool dead, headless;
        Renderer[] renderers;
        MaterialPropertyBlock block;
        Vector3 headScale, chestFwdLocal, headFwdLocal;
        static readonly int TintId = Shader.PropertyToID("_TintColor");
        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int MoveSignId = Animator.StringToHash("MoveSign");
        static readonly int CrouchId = Animator.StringToHash("Crouch");
        static readonly int GroundedId = Animator.StringToHash("Grounded");
        static readonly int MoveXId = Animator.StringToHash("MoveX");
        static readonly int MoveZId = Animator.StringToHash("MoveZ");
        static readonly int ArmedPaceId = Animator.StringToHash("ArmedPace");
        static readonly int ArmedId = Animator.StringToHash("Armed");
        static readonly int StyleId = Animator.StringToHash("Style");
        static readonly int RollId = Animator.StringToHash("Roll");
        static readonly int DrivingId = Animator.StringToHash("Driving");

        class Arm
        {
            public Transform Upper, Lower, Hand, Finger, Thumb;
            public float UpperLen, LowerLen;
            public Vector3 HandFwd, HandBack, CurlAxis;
            public Quaternion HandRest;
            public int Side;
            public void Measure(Transform root, int side)
            {
                Side = side;
                UpperLen = Vector3.Distance(Upper.position, Lower.position);
                LowerLen = Vector3.Distance(Lower.position, Hand.position);
                // Rest pose: palm down, fingers pointing along the arm (the concept's T-pose).
                HandFwd = Hand.InverseTransformDirection((Finger.position - Hand.position).normalized);
                HandBack = Hand.InverseTransformDirection(root.up);
                HandBack = (HandBack - Vector3.Dot(HandBack, HandFwd) * HandFwd).normalized;
                CurlAxis = Vector3.Cross(HandFwd, -HandBack).normalized;
                HandRest = Hand.localRotation;
            }
        }

        void Awake()
        {
            var map = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) map[t.name] = t;
            Transform B(string n) => map.TryGetValue(n, out var t) ? t : throw new MissingReferenceException($"{name}: no bone {n}");
            hips = B("Hips"); spine = B("Spine"); spine1 = B("Spine1"); spine2 = B("Spine2"); neck = B("Neck"); head = B("Head");
            shoulderR = B("RightShoulder"); shoulderL = B("LeftShoulder");
            shoulderRRest = shoulderR.localRotation; shoulderLRest = shoulderL.localRotation;
            right = new Arm { Upper = B("RightArm"), Lower = B("RightForeArm"), Hand = B("RightHand"), Finger = B("RightHandMiddle1"), Thumb = B("RightHandThumb1") };
            left = new Arm { Upper = B("LeftArm"), Lower = B("LeftForeArm"), Hand = B("LeftHand"), Finger = B("LeftHandMiddle1"), Thumb = B("LeftHandThumb1") };
            right.Measure(transform, 1);
            left.Measure(transform, -1);
            headScale = head.localScale;
            chestFwdLocal = spine2.InverseTransformDirection(transform.forward);
            headFwdLocal = head.InverseTransformDirection(transform.forward);
            renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            block = new MaterialPropertyBlock();
            if (Health == null) Health = GetComponentInParent<Health>();
            Health.Damaged += OnDamaged;
            Health.Died += OnDied;
            BuildHitboxes(map);
            if (Animator != null && Animator.runtimeAnimatorController != null) FindStates();
        }

        void BuildHitboxes(Dictionary<string, Transform> map)
        {
            var rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null) { rb = gameObject.AddComponent<Rigidbody>(); }
            rb.isKinematic = true; rb.useGravity = false;

            float headR = Mathf.Max(0.105f, 0.62f * Vector3.Distance(neck.position, head.position));
            var h = AddBox(head, HitZone.Head);
            var sphere = h.AddComponent<SphereCollider>();
            sphere.isTrigger = true; sphere.radius = headR;
            sphere.center = h.transform.InverseTransformPoint(head.position + transform.up * headR * 0.65f);

            Limb(hips, spine2, 0.19f);
            Limb(spine2, neck, 0.17f);
            foreach (var (a, b, r) in new[] {
                ("LeftArm","LeftForeArm",.06f), ("LeftForeArm","LeftHand",.05f),
                ("RightArm","RightForeArm",.06f), ("RightForeArm","RightHand",.05f),
                ("LeftUpLeg","LeftLeg",.085f), ("LeftLeg","LeftFoot",.065f),
                ("RightUpLeg","RightLeg",.085f), ("RightLeg","RightFoot",.065f) })
                Limb(map[a], map[b], r);
        }

        GameObject AddBox(Transform bone, HitZone zone)
        {
            var go = new GameObject(zone + "Box");
            go.layer = Layers.Hitbox;
            go.transform.SetParent(bone, false);
            var box = go.AddComponent<Hitbox>();
            box.Zone = zone; box.Owner = Health; box.Team = Team;
            return go;
        }

        void Limb(Transform a, Transform b, float radius)
        {
            var go = AddBox(a, HitZone.Body);
            go.transform.position = (a.position + b.position) * 0.5f;
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, (b.position - a.position).normalized);
            var c = go.AddComponent<CapsuleCollider>();
            c.isTrigger = true; c.direction = 1; c.radius = radius;
            c.height = Vector3.Distance(a.position, b.position) + radius * 2f;
        }

        public void SetTint(Color c)
        {
            foreach (var r in renderers)
            {
                r.GetPropertyBlock(block);
                block.SetColor(TintId, c);
                r.SetPropertyBlock(block);
            }
        }

        public void SetWeapon(WeaponData data)
        {
            if (gunRoot != null) Destroy(gunRoot.gameObject);
            Weapon = data;
            gunRoot = new GameObject("Gun_" + data.name).transform;
            gunRoot.SetParent(transform.parent, true);
            var go = Instantiate(data.Model, gunRoot);
            gunModel = go.transform;
            gunModel.localScale = Vector3.one * data.ModelScale;
            gunModel.localRotation = Quaternion.Euler(data.ModelEuler);
            gunModel.localPosition = Vector3.zero;
            // GripPoint is relative to the rotated, scaled model; the root sits exactly on the grip.
            gunModel.localPosition = -data.GripPoint;
            gunSupport = new GameObject("Support").transform;
            gunSupport.SetParent(gunRoot, false);
            gunSupport.localPosition = data.SupportPoint - data.GripPoint;
            Muzzle = new GameObject("Muzzle").transform;
            Muzzle.SetParent(gunRoot, false);
            Muzzle.localPosition = data.MuzzlePoint - data.GripPoint;
            if (Seat != null || holstered) gunRoot.gameObject.SetActive(false);
        }

        public void Recoil(float degrees)
        {
            recoilBack = Mathf.Min(recoilBack + 0.035f, 0.09f);
            recoilPitch = Mathf.Min(recoilPitch + degrees * 2.2f, 14f);
        }

        public void Flinch(Vector3 bulletDir)
        {
            flinch = 1f;
            flinchAxis = Vector3.Cross(Vector3.up, bulletDir).normalized;
        }

        public void PlayRoll() { if (!dead) Animator.SetTrigger(RollId); }

        // ---- one-shot clips ----

        float downUntil, throwUntil, knockSeconds = 4f;
        readonly HashSet<string> states = new();

        /// <summary>The controller has this state (a character without the combat or street clips lacks some).</summary>
        public bool HasState(string state) => states.Contains(state);

        bool Play(string state, float fade = 0.15f)
        {
            if (dead || Seat != null || !HasState(state)) return false;
            Animator.CrossFadeInFixedTime(state, fade, 0);
            return true;
        }

        /// <summary>On the ground or getting up after being knocked over: no walking, no gun.</summary>
        public bool Down => Time.time < downUntil;
        /// <summary>In the middle of a grenade throw: the gun is put aside for it.</summary>
        public bool Throwing => Time.time < throwUntil;

        /// <summary>Knocked over (a car's slow nudge): falls back, lies a moment, gets up. False if it cannot happen now.</summary>
        public bool KnockDown()
        {
            if (Down || Throwing || !Play("KnockFall", 0.1f)) return false;
            downUntil = Time.time + knockSeconds;
            punchUntil = 0f;
            return true;
        }

        /// <summary>The grenade throw. Returns the seconds until the grenade leaves the hand, or a negative number if it cannot be thrown now.</summary>
        public float PlayThrow()
        {
            if (Down || Throwing || !Play("ThrowGrenade", 0.2f)) return -1f;
            throwUntil = Time.time + ThrowSeconds * 0.9f;
            return ThrowRelease;
        }

        /// <summary>A sidestep out of the line of fire; side -1 left, 1 right. Falls back to the roll without the clips.</summary>
        public void PlayDodge(int side)
        {
            if (Down || Throwing) return;
            if (!Play(side < 0 ? "DodgeLeft" : "DodgeRight", 0.1f)) PlayRoll();
        }

        public int GestureCount { get; private set; }

        /// <summary>One of the standing gestures (talking with the hands, nodding, looking around); index wraps.</summary>
        public void PlayGesture(int index)
        {
            if (GestureCount == 0 || Down || Throwing || Punching) return;
            Play($"Gesture{((index % GestureCount) + GestureCount) % GestureCount}", 0.3f);
        }

        void FindStates()
        {
            float fall = 0f, getUp = 0f;
            foreach (var c in Animator.runtimeAnimatorController.animationClips)
            {
                states.Add(c.name);
                if (c.name == "KnockFall") fall = c.length;
                if (c.name == "GetUp") getUp = c.length;
            }
            // The controller plays these at 1.2 and 1.25 and moves on at 62 % and 90 % (Actors.BuildController).
            if (fall > 0f && getUp > 0f) knockSeconds = fall * 0.62f / 1.2f + getUp * 0.9f / 1.25f;
            for (int g = 0; HasState($"Gesture{g}"); g++) GestureCount = g + 1;
        }
        // ---- jumping ----

        float armsFree = 1f;

        static readonly int JumpUpState = Animator.StringToHash("JumpUp");

        /// <summary>Called the moment the feet leave the ground: the rise is timed to the real jump, the fall and the landing follow by themselves.</summary>
        public void PlayJump()
        {
            if (dead || Seat != null) return;
            Animator.CrossFadeInFixedTime(JumpUpState, 0.05f, 0);
        }

        /// <summary>Name of the body's state now: for the automated test.</summary>
        public string BodyState
        {
            get
            {
                var i = Animator.GetCurrentAnimatorStateInfo(0);
                foreach (var n in new[] { "JumpUp", "Air", "JumpLand", "Locomotion", "Crouch", "Driving", "Armed", "ArmedCrouch", "KneelDown", "KneelUp", "Drunk", "Injured", "KnockFall", "GetUp", "ThrowGrenade" })
                    if (i.IsName(n)) return n;
                if (i.shortNameHash == deathState) return "Death";
                return "?";
            }
        }

        // ---- punching ----

        public const float PunchSeconds = 0.46f;
        /// <summary>When in the punch the fist arrives: land the hit then.</summary>
        public const float PunchLandsAfter = PunchSeconds * 0.4f;
        const float BodyRadius = 0.2f, FistLength = 0.115f;
        float punchStart, punchUntil;
        int punchSide = -1;
        CharacterRig punchTarget;
        Vector3 fistNow;

        public bool Punching => Time.time < punchUntil;
        /// <summary>The knuckles of the punching hand, for the automated test.</summary>
        public Vector3 FistPoint => fistNow;
        public Vector3 ChestPoint => spine2.position;

        /// <summary>A punch, drawn here rather than from a clip: every frame the fist aims at the other body and stops at its surface.</summary>
        public void PlayPunch(CharacterRig target = null)
        {
            if (dead || Seat != null) return;
            punchStart = Time.time;
            punchUntil = Time.time + PunchSeconds;
            // With a punch clip the whole body throws it, and the IK aims the fist the clip strikes with.
            punchClip = PunchStates != null && PunchStates.Length > 0;
            if (punchClip)
            {
                int i = punchNext++ % PunchStates.Length;
                punchSide = PunchHands[i];
                Animator.CrossFadeInFixedTime(PunchStates[i], 0.06f, 0, PunchOffsets[i]);
            }
            else punchSide = -punchSide;
            punchTarget = target;
        }

        /// <summary>
        /// With a punch clip the clip moves the body, both arms and the fist's turn; this only shifts the striking wrist towards the
        /// other body around the moment of the hit, so the fist reaches it and stops at its surface. The elbow bends the way the clip
        /// has it, so the arm cannot flip.
        /// </summary>
        void PunchClipReach()
        {
            Arm a = punchSide > 0 ? right : left;
            Curl(a, 88f, 55f, 1f);
            fistNow = a.Hand.position + a.Hand.TransformDirection(a.HandFwd) * FistLength;
            if (!Punching || punchTarget == null || punchTarget.dead) return;
            // Along the clip's own arm line: where would the knuckles meet the other body (a sphere round its chest)? Only a fist that
            // would go in is shortened to stop at the surface; the arm is never turned towards anything (an uppercut rising past the
            // head was dragged down to the chest and broke at the elbow).
            Vector3 shoulder = a.Upper.position, wrist = a.Hand.position;
            Vector3 dir = (wrist - shoulder).normalized;
            float reach = Vector3.Distance(shoulder, wrist) + FistLength;
            Vector3 toChest = punchTarget.ChestPoint - shoulder;
            float along = Vector3.Dot(toChest, dir), off = (toChest - dir * along).magnitude;
            if (off >= BodyRadius || along <= 0f) return;
            float surface = along - Mathf.Sqrt(BodyRadius * BodyRadius - off * off);
            if (surface >= reach) return;
            Vector3 goal = shoulder + dir * Mathf.Max(0.12f, surface - FistLength);
            Solve(a, goal, a.Hand.rotation, a.Lower.position, 1f, false);
            fistNow = a.Hand.position + a.Hand.TransformDirection(a.HandFwd) * FistLength;
        }

        void PunchPose()
        {
            if (!Punching) return;
            if (punchClip) { PunchClipReach(); return; }
            float t = (Time.time - punchStart) / PunchSeconds;
            float w = Mathf.SmoothStep(0f, 1f, t / 0.14f) * (1f - Mathf.SmoothStep(0f, 1f, (t - 0.68f) / 0.32f));
            float strike = t < 0.18f ? 0f : t < 0.4f ? Mathf.SmoothStep(0f, 1f, (t - 0.18f) / 0.22f) : 1f - Mathf.SmoothStep(0f, 1f, (t - 0.46f) / 0.5f);
            Vector3 up = Vector3.up, fwd = Vector3.ProjectOnPlane(transform.forward, up).normalized, side = transform.right * punchSide;

            // The shoulder of the punching arm comes through.
            Quaternion turn = Quaternion.AngleAxis(-punchSide * 24f * strike * w, up);
            spine1.rotation = Quaternion.Slerp(Quaternion.identity, turn, 0.5f) * spine1.rotation;
            spine2.rotation = Quaternion.Slerp(Quaternion.identity, turn, 0.5f) * spine2.rotation;

            Arm a = punchSide > 0 ? right : left, other = punchSide > 0 ? left : right;
            Vector3 shoulder = a.Upper.position;
            float longest = a.UpperLen + a.LowerLen - 0.03f;
            Vector3 aim = fwd;
            float dist = longest;
            if (punchTarget != null && !punchTarget.dead)
            {
                Vector3 to = punchTarget.ChestPoint - shoulder;
                aim = to.normalized;
                // The wrist stops a fist's length short of the other body.
                dist = Mathf.Min(longest, Mathf.Max(0.12f, to.magnitude - BodyRadius - FistLength));
            }
            Vector3 guard = shoulder + fwd * 0.26f - up * 0.1f - side * 0.1f;
            Vector3 wrist = Vector3.Lerp(guard, shoulder + aim * dist, strike);
            Vector3 fingers = Vector3.Slerp((fwd + up * 0.6f).normalized, aim, strike);
            Vector3 hint = shoulder - up * 0.4f + side * 0.5f - fwd * 0.2f;
            Solve(a, wrist, HandRot(a, fingers, Vector3.ProjectOnPlane(up, fingers).normalized), hint, w);
            Curl(a, 88f, 55f, w);
            fistNow = a.Hand.position + a.Hand.TransformDirection(a.HandFwd) * FistLength;

            // The other hand up, by the chin.
            Vector3 oShoulder = other.Upper.position;
            Vector3 oGuard = oShoulder + fwd * 0.27f - up * 0.07f + side * 0.12f;
            Vector3 oFingers = (fwd * 0.3f + up * 0.75f + side * 0.3f).normalized;
            Solve(other, oGuard, HandRot(other, oFingers, Vector3.ProjectOnPlane(fwd * -1f, oFingers).normalized), oShoulder - up * 0.4f - side * 0.5f - fwd * 0.2f, w);
            Curl(other, 85f, 50f, w);
        }

        /// <summary>
        /// The generated Idle and Walk clips keep the arms in the rest pose, held out from the body, so with empty hands they are brought
        /// down here and swung with the stride. The library's run clips animate the arms properly, so this fades out with speed.
        /// </summary>
        void RelaxArms()
        {
            float speed = new Vector3(Velocity.x, 0, Velocity.z).magnitude;
            // In the air and on landing the clips move the arms themselves.
            var body = Animator.GetCurrentAnimatorStateInfo(0);
            armsFree = Mathf.MoveTowards(armsFree, body.IsName("Locomotion") || body.IsName("Crouch") ? 1f : 0f, Time.deltaTime * 7f);
            float w = (1f - Mathf.InverseLerp(2.4f, 3.6f, speed)) * armsFree;
            if (ClipArms)
            {
                // The library's idle, walk and jog move the arms well; only the older sprint clip needs its fists turned in.
                float sprint = Mathf.InverseLerp(4.5f, 6f, speed) * armsFree;
                TurnPalmIn(right, 1f, sprint);
                TurnPalmIn(left, -1f, sprint);
                return;
            }
            // The run clips carry the fists turned over: thumbs up, palms towards the body.
            TurnPalmIn(right, 1f, armsFree - w);
            TurnPalmIn(left, -1f, armsFree - w);
            if (w <= 0.001f) return;
            // In step with the legs: the right arm comes forward with the left foot. Normalised time 0 has the left foot furthest forward.
            float stride = Animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            float swing = Mathf.Cos(stride * 2f * Mathf.PI) * Mathf.Clamp01(speed / 1.2f) + Mathf.Sin(Time.time * 0.9f) * 0.05f;
            HangArm(right, 1f, swing, w);
            HangArm(left, -1f, -swing, w);
        }

        /// <summary>Both upper arms swung out from the body by ArmSpread degrees (times weight), before anything else moves them.</summary>
        void SpreadArms(float weight)
        {
            // Only on the walking / standing clips, faded in and out: a punch, a gesture or a throw place the arms themselves.
            var body = Animator.GetCurrentAnimatorStateInfo(0);
            bool walkClips = !Punching && (body.IsName("Locomotion") || body.IsName("Crouch") || body.IsName("Drunk") || body.IsName("Injured"));
            spreadOn = Mathf.MoveTowards(spreadOn, walkClips ? 1f : 0f, Time.deltaTime * 6f);
            float deg = ArmSpread * weight * spreadOn;
            if (deg <= 0.01f) return;
            Spread(right, 1f, deg);
            Spread(left, -1f, deg);
        }

        void Spread(Arm a, float side, float deg)
        {
            Vector3 outward = Vector3.ProjectOnPlane(transform.right * side, Vector3.up).normalized;
            a.Upper.rotation = Quaternion.AngleAxis(deg, Vector3.Cross(Vector3.down, outward)) * a.Upper.rotation;
        }

        void TurnPalmIn(Arm a, float side, float weight)
        {
            if (weight <= 0.001f) return;
            Vector3 fingers = a.Hand.TransformDirection(a.HandFwd), palm = a.Hand.TransformDirection(-a.HandBack);
            Vector3 want = Vector3.ProjectOnPlane(-transform.right * side, fingers);
            if (want.sqrMagnitude < 0.01f) return;
            a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(palm, want.normalized), weight) * a.Hand.rotation;
        }

        void HangArm(Arm a, float side, float swing, float w)
        {
            Vector3 fwd = transform.forward, down = -transform.up, across = transform.right * side;
            Vector3 upperDir = (down + across * 0.14f + fwd * (swing * 0.26f + 0.02f)).normalized;
            Quaternion upper = Quaternion.FromToRotation(a.Lower.position - a.Upper.position, upperDir) * a.Upper.rotation;
            a.Upper.rotation = Quaternion.Slerp(a.Upper.rotation, upper, w);
            Vector3 lowerDir = (upperDir + fwd * (0.2f + Mathf.Max(0f, swing) * 0.25f) - across * 0.03f).normalized;
            Quaternion lower = Quaternion.FromToRotation(a.Hand.position - a.Lower.position, lowerDir) * a.Lower.rotation;
            a.Lower.rotation = Quaternion.Slerp(a.Lower.rotation, lower, w);
            // Fingers point down the arm, palm towards the thigh.
            Quaternion hand = Quaternion.LookRotation(lowerDir, across);
            hand = hand * Quaternion.Inverse(Quaternion.LookRotation(a.HandFwd, a.HandBack));
            a.Hand.rotation = Quaternion.Slerp(a.Hand.rotation, hand, w);
        }

        // ---- seat ----

        public void EnterSeat(Vehicle v)
        {
            Seat = v;
            Velocity = Vector3.zero;
            Crouching = false;
            Grounded = true;
            ReloadWeight = 0f;
            if (gunRoot != null) gunRoot.gameObject.SetActive(false);
            ShowBody(false);      // nobody is drawn inside a car
            punchUntil = 0f;
            Animator.SetBool(DrivingId, true);
        }

        public void LeaveSeat()
        {
            Seat = null;
            ShowBody(true);
            Animator.SetBool(DrivingId, false);
            if (gunRoot != null && !dead && !holstered) gunRoot.gameObject.SetActive(true);
        }

        // ---- life and death ----

        public void Revive()
        {
            dead = false;
            headless = false;
            downUntil = throwUntil = 0f;
            ikWeight = 1f;
            Seat = null;
            ShowBody(true);
            punchUntil = 0f;
            var grounded = transform.parent != null ? transform.parent.GetComponent<StayGrounded>() : null;
            if (grounded != null) Destroy(grounded);
            transform.localRotation = baseLocalRotation;
            Animator.Rebind();
            Animator.Update(0f);
            if (Weapon != null) SetWeapon(Weapon);
        }

        Quaternion baseLocalRotation = Quaternion.identity;

        void Start() => baseLocalRotation = transform.localRotation;

        int deathState = Animator.StringToHash("Death");

        /// <summary>Limp straight away: whatever is thrown through the air or falls is no longer standing.</summary>
        public void GoLimp() { if (dead) Animator.Play(deathState, 0, 0.42f); }

        /// <summary>At the end of the death pose, on its back.</summary>
        public void LieDown() { if (dead) Animator.Play(deathState, 0, 1f); }

        /// <summary>
        /// Which fall fits the killing blow: shot from the front, the back or the right, in the head or not, crouched. A car or a blast
        /// throws the body (CorpseFling) and uses the plain fall. <paramref name="bulletDir"/> is the way the shot travelled.
        /// </summary>
        string DeathFor(Vector3 bulletDir)
        {
            var cause = Health != null ? Health.LastCause : DamageCause.Bullet;
            if (cause == DamageCause.Vehicle || cause == DamageCause.Explosion) return "Death";
            bool headShot = Health != null && Health.LastZone == HitZone.Head;
            if (Crouching && HasState("DeathCrouch")) return "DeathCrouch";
            Vector3 local = transform.InverseTransformDirection(Vector3.ProjectOnPlane(bulletDir, Vector3.up));
            bool fromFront = local.z <= 0f;    // travelling towards the back: it came from in front
            string pick = Mathf.Abs(local.x) > Mathf.Abs(local.z) && local.x < 0f && !headShot ? "DeathRight"
                        : headShot ? (fromFront ? "DeathHeadFront" : "DeathHeadBack")
                        : (fromFront ? "DeathFront" : "DeathBack");
            return HasState(pick) ? pick : "Death";
        }

        /// <summary>How high the hips are above this point: for the automated test.</summary>
        public float HipsAbove(float y) => hips.position.y - y;

        /// <summary>1 when the chest looks straight up: for the automated test.</summary>
        public float ChestUp => Vector3.Dot(Vector3.Cross(right.Upper.position - left.Upper.position, head.position - hips.position).normalized, Vector3.up);

        public void Die(Vector3 bulletDir)
        {
            if (dead) return;
            dead = true;
            punchUntil = 0f;
            if (transform.parent != null && transform.parent.GetComponent<StayGrounded>() == null) transform.parent.gameObject.AddComponent<StayGrounded>();
            Animator.SetBool(DrivingId, false);
            Animator.SetBool(ArmedId, false);
            string fall = DeathFor(bulletDir);
            deathState = Animator.StringToHash(fall);
            Animator.CrossFadeInFixedTime(deathState, 0.1f, 0);
            if (gunRoot != null && holstered)
            {
                Destroy(gunRoot.gameObject);    // it was put away: nothing falls
                gunRoot = null;
                Muzzle = null;
            }
            if (gunRoot != null)
            {
                // The gun falls out of the hands as a physics object.
                gunRoot.gameObject.SetActive(true);
                gunRoot.SetParent(null, true);
                var rb = gunRoot.gameObject.AddComponent<Rigidbody>();
                gunRoot.gameObject.layer = Layers.Debris;
                var box = gunRoot.gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(0.05f, 0.14f, Weapon.Kind == WeaponKind.Rifle ? 0.8f : 0.2f);
                box.center = new Vector3(0, 0, Weapon.Kind == WeaponKind.Rifle ? 0.2f : 0.05f);
                rb.AddForce(bulletDir * 1.5f + Vector3.up, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * 4f, ForceMode.VelocityChange);
                var dropped = gunRoot.gameObject;
                gunRoot = null;
                Muzzle = null;
                Fader.OutAndDestroy(dropped, 7.2f, 0.8f);
            }
        }

        // ---- gore ----

        void OnDamaged(HitZone zone, Vector3 point, Vector3 dir, Health source)
        {
            var fx = FxPool.Instance;
            if (fx == null || Seat != null) return;
            float power = Health.LastCause == DamageCause.Bullet ? (zone == HitZone.Head ? 2.2f : 1f) : Health.LastCause == DamageCause.Melee ? 0.3f : 1.4f;
            fx.Blood(point, dir, -dir, power);
            fx.BloodOnSurfaces(point, dir, power);
            GoreAt?.Invoke(point, power);
            var a = GameAudio.Instance;
            if (Health.LastCause != DamageCause.Melee) a?.PlayAt(a.Gore, point, 0.55f);    // a punch has its own sound
        }

        void OnDied(HitZone zone, Health source)
        {
            var fx = FxPool.Instance;
            if (fx == null) return;
            Vector3 torso = spine1.position;
            var cause = Health.LastCause;
            var a = GameAudio.Instance;
            a?.PlayAt(a.Gore, torso, 1f);

            if (cause == DamageCause.Bullet && zone == HitZone.Head)
            {
                // The head goes: a burst, flying lumps, then the neck keeps pumping.
                Vector3 hp = head.position;
                headless = true;
                fx.Blood(hp, Vector3.up, Random.onUnitSphere, 4f);
                fx.Chunks(hp, Vector3.up, 9, 7f);
                fx.BloodOnSurfaces(hp, -transform.forward, 2f);
                GoreAt?.Invoke(hp, 4f);
                StartCoroutine(Fountain(1.8f, 0.8f));
            }
            else if (cause == DamageCause.Bullet || cause == DamageCause.Melee)
            {
                fx.Blood(torso, Vector3.up, Random.onUnitSphere, 2.2f);
                fx.Chunks(torso, Vector3.up, 3, 4f);
                StartCoroutine(Fountain(0.8f, 0.45f));
            }
            else
            {
                // A car or a blast: the body is thrown, and comes apart a bit.
                fx.Blood(torso, Vector3.up, Random.onUnitSphere, 5.5f);
                fx.Blood(torso, Health.LastImpulse.normalized, -Health.LastImpulse.normalized, 3f);
                fx.Chunks(torso, Vector3.up + Health.LastImpulse.normalized, 14, 9f);
                fx.BloodOnSurfaces(torso, Health.LastImpulse.normalized, 2.2f);
                GoreAt?.Invoke(torso, 6f);
                var root = transform.parent;
                if (root != null && Seat == null)
                {
                    var cc = root.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    var fling = root.gameObject.AddComponent<CorpseFling>();
                    fling.Begin(transform, Health.LastImpulse);
                }
                StartCoroutine(Fountain(1.2f, 0.7f));
            }
            StartCoroutine(PoolWhenDown());
        }

        IEnumerator Fountain(float seconds, float power)
        {
            float end = Time.time + seconds;
            while (Time.time < end && this != null)
            {
                var fx = FxPool.Instance;
                if (fx == null) yield break;
                fx.Blood(neck.position, neck.up + Vector3.up, Vector3.up, power * Mathf.Clamp01((end - Time.time) / seconds + .2f));
                yield return new WaitForSeconds(0.07f);
            }
        }

        IEnumerator PoolWhenDown()
        {
            yield return new WaitForSeconds(1.1f);
            var fx = FxPool.Instance;
            if (fx == null || transform.parent != null && transform.parent.GetComponent<CorpseFling>() != null) yield break;
            if (Surface.FindPaintable(hips.position + Vector3.up * .5f, Vector3.down, 4f, out var g) && hips.position.y - g.point.y < 1f)
                fx.BloodPool(g.point);
        }

        // ---- frame ----

        void Update()
        {
            if (Animator == null) return;
            var flat = new Vector3(Velocity.x, 0, Velocity.z);
            float speed = flat.magnitude;
            float along = Vector3.Dot(flat, transform.forward);
            bool walkGait = !Crouching && speed > WalkClipSpeed && speed <= WalkGaitLimit;
            Animator.SetFloat(SpeedId, walkGait ? WalkClipSpeed : speed, 0.08f, Time.deltaTime);
            // Backwards plays the clip in reverse; beyond a clip's own pace it speeds up, so feet keep to the ground.
            float pace = Crouching ? (speed > 0.3f ? Mathf.Clamp(speed / Mathf.Max(0.2f, CrouchClipSpeed), 0.6f, 2.6f) : 1f)
                       : walkGait ? speed / WalkClipSpeed
                       : (speed > SprintClipSpeed ? speed / Mathf.Max(0.5f, SprintClipSpeed) : 1f);
            Animator.SetFloat(MoveSignId, (along < -0.5f ? -1f : 1f) * pace);
            Animator.SetBool(CrouchId, Crouching);
            Animator.SetBool(GroundedId, Grounded);

            // Gun out (and not sprinting): the eight-way clips, steered by the velocity in body space.
            armedMode = Weapon != null && !holstered && !Sprinting && Seat == null && !dead;
            Animator.SetBool(ArmedId, armedMode);
            Vector3 body = transform.InverseTransformDirection(flat);
            // Slower than the walk clips (aiming, a slow step): their full stride in the direction of travel, played slower. Blended
            // towards standing, the steps shrank but did not slow, and the feet slid over the ground.
            float walkRing = Crouching ? ArmedCrouchClipSpeed : ArmedWalkClipSpeed;
            float armedPace = 1f;
            if (speed > 0.25f && speed < walkRing)
            {
                body = body.normalized * walkRing;
                armedPace = Mathf.Max(0.3f, speed / walkRing);
            }
            Animator.SetFloat(MoveXId, body.x, 0.1f, Time.deltaTime);
            Animator.SetFloat(MoveZId, body.z, 0.1f, Time.deltaTime);
            float ring = Crouching ? ArmedCrouchClipSpeed : ArmedRunClipSpeed;
            if (speed > ring) armedPace = speed / Mathf.Max(0.5f, ring);
            Animator.SetFloat(ArmedPaceId, armedPace);
            Animator.SetInteger(StyleId, (int)Style);

            if (Grounded && speed > 0.8f && !dead)
            {
                footTimer -= Time.deltaTime * Mathf.Lerp(1.6f, 3.6f, speed / 6.5f);
                if (footTimer <= 0f)
                {
                    footTimer = 1f;
                    var a = GameAudio.Instance;
                    if (a != null) a.PlayAt(a.Footstep, transform.position, Crouching ? 0.12f : 0.3f, 3f);
                }
            }
        }

        bool armedMode;

        void LateUpdate()
        {
            if (Animator == null) return;
            // Hands busy (a throw) or on the ground: the gun is put aside and nothing procedural pulls on the body.
            bool busy = Down || Throwing;
            if (gunRoot != null && !dead) gunRoot.gameObject.SetActive(!busy && !holstered && Seat == null);
            float dt = Time.deltaTime;
            ikWeight = Mathf.MoveTowards(ikWeight, dead ? 0f : 1f, dt * 4f);
            aimWeight = Mathf.MoveTowards(aimWeight, dead || holstered ? 0f : AimWeightTarget, dt * 5f);
            recoilBack = Mathf.MoveTowards(recoilBack, 0f, dt * 0.4f);
            recoilPitch = Mathf.Lerp(recoilPitch, 0f, dt * 12f);
            flinch = Mathf.MoveTowards(flinch, 0f, dt * 4f);

            head.localScale = headless ? Vector3.zero : headScale;
            if (dead || RawPlayback) return;

            if (Seat != null || busy) return;

            TwistAndAim(dt);
            // Standing only: walking, the clip's own swing passes the body.
            float standing = 1f - Mathf.InverseLerp(0.3f, 1.2f, new Vector3(Velocity.x, 0f, Velocity.z).magnitude);
            if (holstered || Weapon == null) { SpreadArms(standing); RelaxArms(); PunchPose(); return; }
            // The clips roll the shoulders in, which reads as a narrow chest once the arms come forward: back to their full width.
            shoulderR.localRotation = Quaternion.Slerp(shoulderR.localRotation, shoulderRRest, aimWeight);
            shoulderL.localRotation = Quaternion.Slerp(shoulderL.localRotation, shoulderLRest, aimWeight);
            if (Weapon.Kind == WeaponKind.Pistol)
            {
                // Running with a pistol lowered: the same hands as running empty, and the gun follows the hand.
                TurnPalmIn(right, 1f, 1f - aimWeight);
                TurnPalmIn(left, -1f, 1f - aimWeight);
            }
            PlaceGun();

            // A pistol swings with the running arm; a rifle stays in both hands across the chest.
            bool pistol = Weapon != null && Weapon.Kind == WeaponKind.Pistol;
            float armWeight = pistol ? aimWeight : 1f;
            SpreadArms((1f - armWeight) * standing);
            Solve(right, RightTargetPos(), RightTargetRot(), RightElbowHint(), armWeight);
            Solve(left, LeftTargetPos(), LeftTargetRot(), LeftElbowHint(), armWeight);
            Curl(right, 70f, 35f, 1f);
            Curl(left, ReloadWeight > 0.5f ? 25f : 40f, 18f, armWeight);
        }

        /// <summary>How much of this palm faces the way the body faces: 1 = straight ahead, 0 = towards the thigh, -1 = behind. side: 1 right, -1 left.</summary>
        public float PalmFacing(int side)
        {
            var a = side > 0 ? right : left;
            return Vector3.Dot(a.Hand.TransformDirection(-a.HandBack), transform.forward);
        }

        public Transform Bone(string name) => name switch
        {
            "Head" => head, "Chest" => spine2, "Hips" => hips, "RightHand" => right.Hand, "LeftHand" => left.Hand, _ => null
        };

        /// <summary>The clip with most weight right now.</summary>
        public string MainClip()
        {
            var infos = Animator.GetCurrentAnimatorClipInfo(0);
            string best = ""; float w = -1f;
            foreach (var i in infos) if (i.weight > w && i.clip != null) { w = i.weight; best = i.clip.name; }
            return best;
        }

        /// <summary>False while sitting in a car.</summary>
        public bool BodyShown { get; private set; } = true;

        void ShowBody(bool show)
        {
            BodyShown = show;
            foreach (var r in renderers) r.enabled = show;
        }

        void TwistAndAim(float dt)
        {
            Vector3 up = Vector3.up;
            Vector3 aimDir = AimPoint - spine2.position;
            if (aimDir.sqrMagnitude < 0.01f) aimDir = transform.forward;
            aimDir.Normalize();
            Vector3 flatAim = Vector3.ProjectOnPlane(aimDir, up);
            if (flatAim.sqrMagnitude < 1e-4f) flatAim = transform.forward;
            flatAim.Normalize();
            Vector3 aimRight = Vector3.Cross(up, flatAim);
            float pitch = Mathf.Clamp(Vector3.SignedAngle(flatAim, aimDir, -aimRight), -70f, 70f);

            // Legs follow the direction of travel; backwards uses the reversed forward clip, so only sideways twists.
            var flat = new Vector3(Velocity.x, 0, Velocity.z);
            float speed = flat.magnitude;
            float targetLeg = 0f;
            // The eight-way clips step in the direction of travel themselves.
            if (speed > 0.5f && !armedMode)
            {
                float a = Vector3.SignedAngle(transform.forward, flat, up);
                if (Mathf.Abs(a) > 110f) a = a - Mathf.Sign(a) * 180f;   // moving backwards: legs follow the mirror direction
                targetLeg = Mathf.Clamp(a, -65f, 65f);
            }
            legYaw = Mathf.MoveTowardsAngle(legYaw, targetLeg, dt * 300f);
            hips.rotation = Quaternion.AngleAxis(legYaw, up) * hips.rotation;

            // The chest is turned to a target direction in absolute terms, whatever the clip did with the spine: a crouch's forward
            // hunch, a walk's sway. A bladed stance brings the left shoulder forward, within reach of the support grip, and opens up
            // with speed, because nobody runs side-on.
            float w = aimWeight;
            if (w > 0.001f)
            {
                float stance = (Weapon != null && Weapon.Kind == WeaponKind.Rifle ? 30f : 12f) * Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(speed / 5f));
                Vector3 chestGoal = Quaternion.AngleAxis(stance, up) * Quaternion.AngleAxis(-pitch * 0.6f, aimRight) * flatAim;
                Transform[] chain = { spine, spine1, spine2 };
                for (int k = 0; k < 3; k++)
                {
                    Vector3 cur = spine2.TransformDirection(chestFwdLocal);
                    Quaternion q = Quaternion.FromToRotation(cur, chestGoal);
                    chain[k].rotation = Quaternion.Slerp(Quaternion.identity, q, w / (3 - k)) * chain[k].rotation;
                }
                // The head sets itself level on the aim.
                Vector3 headCur = head.TransformDirection(headFwdLocal);
                head.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(headCur, aimDir), w * 0.9f) * head.rotation;
            }
            if (flinch > 0f) spine1.rotation = Quaternion.AngleAxis(flinch * 14f, flinchAxis) * spine1.rotation;
        }

        void PlaceGun()
        {
            if (gunRoot == null) return;
            bool rifle = Weapon.Kind == WeaponKind.Rifle;
            Vector3 up = Vector3.up;
            Vector3 shoulderMid = (right.Upper.position + left.Upper.position) * 0.5f;
            Vector3 aimDir = (AimPoint - shoulderMid);
            if (aimDir.sqrMagnitude < 0.25f) aimDir = transform.forward;
            aimDir.Normalize();
            Vector3 side = Vector3.Cross(up, aimDir).normalized;
            Vector3 pos = rifle
                ? right.Upper.position + aimDir * 0.26f - up * 0.07f - side * 0.02f
                : shoulderMid + aimDir * 0.5f - up * 0.04f + side * 0.03f;
            Quaternion rot = Quaternion.LookRotation((AimPoint - pos).normalized, up);

            if (aimWeight < 1f)
            {
                Vector3 carryPos;
                Quaternion carryRot;
                Vector3 fwd = transform.forward, r = transform.right;
                if (rifle)
                {
                    // Across the chest, muzzle up and to the left.
                    carryPos = spine2.position + fwd * 0.27f - up * 0.14f + r * 0.13f;
                    carryRot = Quaternion.LookRotation((fwd * 0.5f - r * 0.62f + up * 0.6f).normalized, (up * 0.8f + fwd * 0.4f).normalized);
                }
                else
                {
                    // In the hand, wherever the running arm has it. The inverse of RightTargetPos / RightTargetRot.
                    Vector3 hFwd = right.Hand.TransformDirection(right.HandFwd);
                    Vector3 hBack = right.Hand.TransformDirection(right.HandBack);
                    carryRot = Quaternion.LookRotation(hFwd, Vector3.Cross(hFwd, hBack));
                    carryPos = right.Hand.position + carryRot * new Vector3(-0.012f, 0.015f, 0.075f);
                }
                pos = Vector3.Lerp(carryPos, pos, aimWeight);
                rot = Quaternion.Slerp(carryRot, rot, aimWeight);
            }

            if (ReloadWeight > 0f)
            {
                Vector3 r = rot * Vector3.right;
                pos += -up * 0.1f * ReloadWeight;
                rot = Quaternion.AngleAxis(-35f * ReloadWeight, r) * rot;
            }
            rot = Quaternion.AngleAxis(-recoilPitch, rot * Vector3.right) * rot;
            pos -= rot * Vector3.forward * recoilBack;
            gunRoot.SetPositionAndRotation(pos, rot);
        }

        Vector3 RightTargetPos()
        {
            if (gunRoot == null) return right.Hand.position;
            return gunRoot.position - gunRoot.forward * 0.075f + gunRoot.right * 0.012f - gunRoot.up * 0.015f;
        }

        Quaternion RightTargetRot()
        {
            if (gunRoot == null) return right.Hand.rotation;
            Vector3 fwd = (gunRoot.forward * 0.95f - gunRoot.up * 0.3f).normalized;
            Vector3 back = gunRoot.right;
            return HandRot(right, fwd, back);
        }

        Vector3 LeftTargetPos()
        {
            if (gunRoot == null) return left.Hand.position;
            Vector3 p = gunSupport.position;
            if (Weapon.Kind == WeaponKind.Rifle)
                p += -gunRoot.up * 0.04f - gunRoot.right * 0.035f - gunRoot.forward * 0.02f;
            else
                p += -gunRoot.right * 0.055f - gunRoot.up * 0.03f - gunRoot.forward * 0.05f;
            if (ReloadWeight > 0f)
            {
                // Support hand goes down to the magazine well and back.
                Vector3 mag = gunRoot.position - gunRoot.up * 0.16f + gunRoot.forward * 0.08f;
                float wave = Mathf.Sin(Mathf.Clamp01(ReloadWeight) * Mathf.PI * 0.5f);
                p = Vector3.Lerp(p, mag, wave);
            }
            return p;
        }

        Quaternion LeftTargetRot()
        {
            if (gunRoot == null) return left.Hand.rotation;
            Vector3 fwd, back;
            if (Weapon.Kind == WeaponKind.Rifle)
            {
                fwd = (gunRoot.forward * 0.55f + gunRoot.right * 0.8f).normalized;
                back = (-gunRoot.up * 0.85f - gunRoot.right * 0.4f).normalized;
            }
            else
            {
                fwd = (gunRoot.forward * 0.65f + gunRoot.right * 0.55f - gunRoot.up * 0.15f).normalized;
                back = (-gunRoot.right * 0.8f - gunRoot.up * 0.4f).normalized;
            }
            return HandRot(left, fwd, back);
        }

        // Elbows out from the ribs.
        Vector3 RightElbowHint() => right.Upper.position - Vector3.up * 0.42f + spine2Right * 0.62f - transform.forward * 0.12f;
        Vector3 LeftElbowHint() => left.Upper.position - Vector3.up * 0.42f - spine2Right * 0.62f - transform.forward * 0.12f;
        Vector3 spine2Right => Vector3.ProjectOnPlane(right.Upper.position - left.Upper.position, Vector3.up).normalized;

        static Quaternion HandRot(Arm a, Vector3 worldFwd, Vector3 worldBack)
        {
            // Rotation that maps the hand's local "fingers" axis and "back of hand" axis on to the wanted world directions.
            return Quaternion.LookRotation(worldFwd, worldBack) * Quaternion.Inverse(Quaternion.LookRotation(a.HandFwd, a.HandBack));
        }

        /// <param name="rollForearm">Move most of the wrist's twist into the forearm (a hand placed on a gun); off keeps the
        /// clip's own forearm roll.</param>
        void Solve(Arm a, Vector3 target, Quaternion handRot, Vector3 hint, float weight, bool rollForearm = true)
        {
            float w = ikWeight * weight;
            if (w <= 0.001f) return;
            Vector3 root = a.Upper.position;
            Vector3 toT = target - root;
            float reach = a.UpperLen + a.LowerLen - 0.002f;
            float d = Mathf.Min(toT.magnitude, reach);
            if (d < 0.05f) return;
            Vector3 dir = toT.normalized;
            target = root + dir * d;

            // Elbow from the law of cosines, on the side of the pole.
            float along = (a.UpperLen * a.UpperLen - a.LowerLen * a.LowerLen + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a.UpperLen * a.UpperLen - along * along));
            Vector3 pole = Vector3.ProjectOnPlane(hint - root, dir);
            if (pole.sqrMagnitude < 1e-5f) pole = Vector3.ProjectOnPlane(-Vector3.up, dir);
            Vector3 elbow = root + dir * along + pole.normalized * h;

            Quaternion upper = Quaternion.FromToRotation(a.Lower.position - root, elbow - root) * a.Upper.rotation;
            a.Upper.rotation = Quaternion.Slerp(a.Upper.rotation, upper, w);
            Quaternion lower = Quaternion.FromToRotation(a.Hand.position - a.Lower.position, target - a.Lower.position) * a.Lower.rotation;
            a.Lower.rotation = Quaternion.Slerp(a.Lower.rotation, lower, w);
            // A wrist turns only a little: most of a turned palm is the forearm rolling, or all the twist lands in the wrist and crushes the hand.
            if (rollForearm)
            {
                float roll = WristTwist(a, handRot, out Vector3 forearm);
                a.Lower.rotation = Quaternion.AngleAxis(roll * 0.85f * w, forearm) * a.Lower.rotation;
            }
            a.Hand.rotation = Quaternion.Slerp(a.Hand.rotation, handRot, w);
        }

        /// <summary>How far the hand is rolled about the forearm from its rest, in degrees.</summary>
        static float WristTwist(Arm a, Quaternion handRot, out Vector3 along)
        {
            along = (a.Hand.position - a.Lower.position).normalized;
            Quaternion delta = handRot * Quaternion.Inverse(a.Lower.rotation * a.HandRest);
            float d = Vector3.Dot(new Vector3(delta.x, delta.y, delta.z), along);
            return Mathf.DeltaAngle(0f, 2f * Mathf.Atan2(d, delta.w) * Mathf.Rad2Deg);
        }

        /// <summary>The roll left in the wrist now, degrees: for the automated test. side: 1 right, -1 left.</summary>
        public float WristRoll(int side)
        {
            var a = side > 0 ? right : left;
            return WristTwist(a, a.Hand.rotation, out _);
        }

        void Curl(Arm a, float fingers, float thumb, float weight)
        {
            float w = ikWeight * weight;
            if (w <= 0f) return;
            Vector3 axis = a.Hand.TransformDirection(a.CurlAxis);
            a.Finger.rotation = Quaternion.AngleAxis(fingers * w, axis) * a.Finger.rotation;
            a.Thumb.rotation = Quaternion.AngleAxis(thumb * w, axis) * a.Thumb.rotation;
        }
    }
}
