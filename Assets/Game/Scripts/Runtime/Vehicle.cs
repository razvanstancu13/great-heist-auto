using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace GreatHeistAuto
{
    /// <summary>
    /// A car. Parked cars are kinematic and cost almost nothing; a car wakes up when someone drives it or something hits it.
    /// Arcade physics: four suspension rays, then grip and drive forces per wheel. Bullets wear down its health, below a threshold it
    /// burns by itself, and at zero it explodes with area damage, which can chain to other cars.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Vehicle : MonoBehaviour
    {
        public static readonly List<Vehicle> All = new();

        [Header("Set by the builder, local to the car, +Z is the front")]
        public Vector3 SeatLocal, SteeringLocal, ExitLocal, ExhaustLocal;
        public Vector3[] WheelLocal = new Vector3[4];   // FL FR RL RR, on the ground
        public float WheelRadius = 0.33f;
        public Vector3 BodySize = new(1.85f, 1.4f, 4.5f);
        public Transform[] WheelVisuals;
        public Mesh CutBody;                    // the near shell with holes for the wheels and doors, while the car is in use
        public Transform[] Doors;               // left, right; the origin of each is its hinge
        public GameObject Wells;                // dark insides behind those holes
        public Renderer LampRenderer;
        public Renderer[] BodyRenderers;
        public Material CharredMaterial;
        public Light[] Headlights;

        [Header("Tuning")]
        public float MaxHealth = 100f;
        public float BurnThreshold = 72f;     // bullets set it alight below this
        public float CrashBurnThreshold = 30f;   // crashes only when it is nearly wrecked
        public float BurnPerSecond = 7f;
        public float TopSpeed = 61.2f, ReverseSpeed = 8f, Acceleration = 9f, BrakeAccel = 18f;
        public float ExplosionRadius = 11f;
        public float WreckSeconds = 8f, RespawnSeconds = 6f;

        public float CurrentHealth { get; private set; }
        public bool OnFire { get; private set; }
        public bool Exploded { get; private set; }
        public bool HasDriver => DriverHealth != null;
        public Health DriverHealth { get; private set; }
        public float Speed => rb != null && !rb.isKinematic ? rb.linearVelocity.magnitude : 0f;
        public float ForwardSpeed => rb != null && !rb.isKinematic ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
        public bool LightsOn { get; set; }
        public bool ForceLit { get; set; }       // test pictures
        public Vector3 Home { get; private set; }
        public float SteerAngle { get; private set; }

        public static event System.Action<Vehicle> ExplodedEvent;
        /// <summary>People knocked over by a slow car (read by the automated test).</summary>
        public static int PeopleKnocked;

        Rigidbody rb;
        float throttle, steer, steerSmooth, wheelSpin, idleTime, tiltTime;
        bool handbrake, braking, honking;
        readonly float[] compress = new float[4];
        AudioSource engine, horn, fireLoop;
        ParticleSystem exhaust, fire, burnSmoke;
        NavMeshObstacle obstacle;
        Vector3 parkedAt;
        readonly Collider[] overlap = new Collider[16];
        MaterialPropertyBlock block;
        float lastCrash, driftAmount, nextTyreSmoke;
        /// <summary>Set by the player's controller. Cars driven by others keep their grip: they brake for people, and must not spin doing it.</summary>
        public bool PlayerDriving { get; set; }
        // Hard braking at speed, 0 to 1: the tyres have nothing left for steering. And how far the weight has gone to the front.
        float brakeHard, loadShift;
        float wobbleSeed;
        AudioSource skid;
        float skidLevel;
        bool squealing;
        /// <summary>The tail is sliding out.</summary>
        public bool Drifting { get; private set; }
        /// <summary>Thumps of a car hitting someone: for the automated test.</summary>
        public static int PeopleHit;
        /// <summary>Tyre screeches played: for the automated test.</summary>
        public static int Screeches;
        ParticleSystem damageSmoke;
        /// <summary>How hard it is smoking from damage, 0 to 1.</summary>
        public float SmokeAmount { get; private set; }
        static long nextTyreKey;
        readonly long tyreKey = ++nextTyreKey;
        const float MountHeight = 0.7f, SpringRest = 0.76f, Travel = 0.16f;
        const float RaySpan = SpringRest + Travel;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.isKinematic = true;
            wobbleSeed = Random.value * 100f;
            CurrentHealth = MaxHealth;
            All.Add(this);
            parkedAt = transform.position;
            Home = transform.position;
            homeRot = transform.rotation;
            if (BodyRenderers != null && BodyRenderers.Length > 0 && BodyRenderers[0] != null)
            {
                paintMat = BodyRenderers[0].sharedMaterial;
                bodyFilter = BodyRenderers[0].GetComponent<MeshFilter>();
                wholeBody = bodyFilter.sharedMesh;
            }
            body = GetComponent<BoxCollider>();
            block = new MaterialPropertyBlock();
            obstacle = GetComponent<NavMeshObstacle>();
            enabled = false;
            UpdateShell();
        }

        void OnDestroy() => All.Remove(this);

        // ---- driver ----

        /// <summary>side: 0 left, 1 right. door: false when the door is already open, or for someone placed at the wheel from the start.</summary>
        public void Enter(Health driver, int side = 0, bool door = true)
        {
            DriverHealth = driver;
            Wake();
            if (door) OpenDoor(side, 0.45f);
            SetDrivenVisuals(true);
        }

        public void Exit(int side = 0, bool door = true)
        {
            DriverHealth = null;
            PlayerDriving = false;
            throttle = 0f; steer = 0f; handbrake = true; honking = false;
            if (door) OpenDoor(side, 0.7f);
            if (engine != null) engine.Stop();
            if (horn != null) horn.Stop();
            SetDrivenVisuals(false);
        }

        // ---- doors and the shell in use ----

        readonly float[] doorAngle = new float[2], doorCloseAt = new float[2];
        Coroutine doorRoutine;
        bool drivenVisuals;
        MeshFilter bodyFilter;
        Mesh wholeBody;
        const float DoorOpenDegrees = 62f, DoorSeconds = 0.26f;

        public bool DoorMoving => doorRoutine != null;
        public float DoorOpenness(int side) => doorAngle[side];
        /// <summary>Where someone stands to get in on this side (0 left, 1 right), on the car's own level.</summary>
        public Vector3 DoorSpot(int side) =>
            transform.TransformPoint(new Vector3(side == 0 ? ExitLocal.x : -ExitLocal.x, ExitLocal.y, ExitLocal.z));

        /// <summary>Opens this door, and closes it again by itself after 'seconds'.</summary>
        public void OpenDoor(int side, float seconds)
        {
            if (Exploded || Doors == null || Doors.Length < 2 || Doors[side] == null) return;
            if (doorAngle[side] <= 0f && Time.time >= doorCloseAt[side]) GameAudio.Instance?.PlayAt(GameAudio.Instance.Door, transform.position, 0.6f);
            doorCloseAt[side] = Time.time + seconds;
            if (doorRoutine == null) doorRoutine = StartCoroutine(MoveDoors());
            UpdateShell();
        }

        System.Collections.IEnumerator MoveDoors()
        {
            bool moving = true;
            while (moving && !Exploded)
            {
                moving = false;
                for (int i = 0; i < 2; i++)
                {
                    float target = Time.time < doorCloseAt[i] ? 1f : 0f;
                    float was = doorAngle[i];
                    doorAngle[i] = Mathf.MoveTowards(was, target, Time.deltaTime / DoorSeconds);
                    if (was > 0f && doorAngle[i] <= 0f) GameAudio.Instance?.PlayAt(GameAudio.Instance.Door, transform.position, 0.8f);
                    if (Doors[i] != null) Doors[i].localRotation = Quaternion.Euler(0f, (i == 0 ? 1f : -1f) * DoorOpenDegrees * Mathf.SmoothStep(0f, 1f, doorAngle[i]), 0f);
                    if (doorAngle[i] > 0f || target > 0f) moving = true;
                }
                yield return null;
            }
            doorRoutine = null;
            UpdateShell();
        }

        /// <summary>A car shows real wheels in real holes and doors of their own, from the start, so nothing changes when someone gets in; a wreck is the plain shell. The LOD group leaves all of it out at a distance.</summary>
        void UpdateShell()
        {
            bool inUse = !Exploded && CutBody != null;
            if (bodyFilter == null) return;
            bodyFilter.sharedMesh = inUse ? CutBody : wholeBody;
            foreach (var w in WheelVisuals) if (w != null) w.gameObject.SetActive(inUse);
            if (Wells != null) Wells.SetActive(inUse);
            if (Doors != null)
                foreach (var d in Doors)
                {
                    if (d == null) continue;
                    d.gameObject.SetActive(inUse);
                    if (inUse)
                    {
                        var r = d.GetComponent<Renderer>();
                        r.sharedMaterial = BodyRenderers[0].sharedMaterial;
                    }
                }
        }

        public void SetInput(float throttleInput, float steerInput, bool brake, bool hand, bool honk)
        {
            throttle = Mathf.Clamp(throttleInput, -1f, 1f);
            steer = Mathf.Clamp(steerInput, -1f, 1f);
            braking = brake;
            handbrake = hand;
            if (honk != honking)
            {
                honking = honk;
                if (horn == null) horn = MakeSource(false);
                var clips = GameAudio.Instance != null ? GameAudio.Instance.Honk : null;
                if (honk && clips != null && clips.Length > 0) { horn.clip = clips[0]; horn.loop = true; horn.volume = 1f; horn.Play(); }
                else horn.Stop();
            }
        }

        public void SetDrivenVisuals(bool on)
        {
            drivenVisuals = on;
            UpdateShell();
            if (LampRenderer != null) LampRenderer.enabled = on;
            if (exhaust == null && on && FxPool.Instance != null) exhaust = FxPool.Instance.MakeExhaust(transform, ExhaustLocal);
            if (exhaust != null)
            {
                if (on) exhaust.Play(); else exhaust.Stop();
            }
            if (on)
            {
                if (engine == null) engine = MakeSource(true);
                var a = GameAudio.Instance;
                if (a != null && a.Engine != null) { engine.clip = a.Engine; engine.volume = 0.7f; engine.Play(); }
            }
        }

        AudioSource MakeSource(bool loop)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.spatialBlend = 1f; s.minDistance = 7f; s.maxDistance = 150f; s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.loop = loop; s.dopplerLevel = 0.3f; s.playOnAwake = false;
            return s;
        }

        /// <summary>Holds the front wheels at a steering position, for test pictures of a parked car.</summary>
        public void SetPreviewSteer(float s) => steerSmooth = s;

        /// <summary>Puts the car somewhere at rest (used by the automated test).</summary>
        public void Teleport(Vector3 pos, Quaternion rot)
        {
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.position = pos; rb.rotation = rot;
            transform.SetPositionAndRotation(pos, rot);
            Physics.SyncTransforms();
        }

        public void Wake()
        {
            enabled = true;
            idleTime = 0f;
            if (rb.isKinematic)
            {
                rb.isKinematic = false;
                rb.mass = 1250f;
                rb.centerOfMass = new Vector3(0, 0.42f, 0.05f);
                rb.linearDamping = 0.02f; rb.angularDamping = 0.6f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.maxAngularVelocity = 5f;
                if (obstacle != null) obstacle.enabled = false;
            }
        }

        void GoToSleep()
        {
            StopSkidSound();
            brakeHard = 0f; loadShift = 0f;
            // Never come to rest on another car: that one may drive away or blow up, and this one would hang in the air.
            if (!Physics.Raycast(transform.TransformPoint(new Vector3(0, 0.6f, 0)), Vector3.down, out var under, 3f, Layers.WorldMask, QueryTriggerInteraction.Ignore)
                || under.collider.attachedRigidbody != null) { idleTime = 0f; return; }
            rb.isKinematic = true;
            enabled = false;
            // It left its parking spot: the baked navmesh does not know about the new one.
            if (obstacle != null && (transform.position - parkedAt).sqrMagnitude > 4f) obstacle.enabled = true;
        }

        // ---- physics ----

        void FixedUpdate()
        {
            if (rb.isKinematic) return;
            float dt = Time.fixedDeltaTime;
            float k = rb.mass * 9.81f / 4f / (SpringRest - MountHeight);
            float damper = 2f * Mathf.Sqrt(k * rb.mass / 4f) * 0.55f;

            float speed = rb.linearVelocity.magnitude;
            float fwdSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
            if (Exploded) rb.WakeUp();     // whatever it lies on may go; a sleeping body would stay hanging

            // Braking hard at speed: the weight goes on to the front, the light rear lets go, and what the tyres spend on stopping they cannot
            // spend on steering. The faster the car, the worse. At town speeds a stop stays a plain stop.
            bool pedal = (handbrake && throttle <= 0.1f) || (throttle < -0.1f && fwdSpeed > 2f);
            float hardWanted = PlayerDriving && pedal ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(9f, 24f, speed)) : 0f;
            brakeHard = Mathf.MoveTowards(brakeHard, hardWanted, dt * (hardWanted > brakeHard ? 4f : 2.5f));
            loadShift = Mathf.MoveTowards(loadShift, pedal || braking ? Mathf.Clamp01(speed / 6f) : 0f, dt * 5f);
            // The faster, the less any of this forgives. Others keep their quick hands and steady rotation: they have lanes to follow.
            float flatOut = Mathf.InverseLerp(22f, 55f, speed);
            float steerRate = PlayerDriving ? Mathf.Lerp(3.2f, 2f, Mathf.InverseLerp(8f, 40f, speed)) * Mathf.Lerp(1f, 0.75f, brakeHard) : 3.2f;
            steerSmooth = Mathf.MoveTowards(steerSmooth, steer, dt * steerRate);
            rb.angularDamping = PlayerDriving ? Mathf.Lerp(0.45f, 0.25f, brakeHard) : 0.6f;     // a turning car goes on turning

            // Space with the throttle down is a power slide. Space alone lets the tail go only as far as the speed asks.
            float rearSlip = Mathf.Abs(Vector3.Dot(rb.linearVelocity, transform.right));
            bool slideWanted = handbrake && speed > 4f;
            float driftWanted = slideWanted ? (throttle > 0.1f ? 1f : Mathf.Lerp(0.25f, 1f, brakeHard)) : 0f;
            driftAmount = driftAmount < driftWanted
                ? Mathf.MoveTowards(driftAmount, driftWanted, dt * (throttle > 0.1f ? 7f : 3.5f))
                : Mathf.MoveTowards(driftAmount, driftWanted, dt * (rearSlip > 3f ? 0.8f : 3f));
            float looseRear = Mathf.Max(driftAmount, brakeHard);
            Drifting = looseRear > 0.25f && rearSlip > 2f;

            // Tyres at their limit do not go where they are pointed: the steering wanders, and the tail with it.
            float wobbleTime = Time.time * 2.3f + wobbleSeed;
            float steerWobble = (Mathf.PerlinNoise(wobbleTime, wobbleSeed) - 0.5f) * 2f;
            float tailWobble = (Mathf.PerlinNoise(wobbleTime * 0.7f, wobbleSeed + 9f) - 0.5f) * 2f;
            float steerAngle = steerSmooth * Mathf.Lerp(Mathf.Lerp(Mathf.Lerp(34f, 9f, Mathf.Clamp01(speed / 26f)), 4f, flatOut), 20f, driftAmount) + steerWobble * 5f * (1f + flatOut) * brakeHard;
            SteerAngle = steerAngle;
            if (looseRear > 0.05f && speed > 4f)
            {
                // The tail steers more than the front does now: it swings out on the side steered to, and steering the other way brings it back.
                float tailSwing = steerSmooth * (driftAmount * 0.7f + brakeHard * 3f) + tailWobble * 1.1f * (1f + flatOut) * brakeHard;
                rb.AddTorque(transform.up * tailSwing, ForceMode.Acceleration);

                // Catch a slide past about 60 degrees, so a drift stays a drift and not a spin. Hard braking may go a little further.
                float slide = Vector3.SignedAngle(transform.forward, Vector3.ProjectOnPlane(rb.linearVelocity, transform.up), transform.up);
                float slack = 12f * brakeHard;
                // Past about 50 degrees the turning itself is slowed, and past 60 the car is turned back towards where it is going.
                float over = Mathf.Clamp01((Mathf.Abs(slide) - 50f - slack) / 15f);
                float yawRate = Vector3.Dot(rb.angularVelocity, transform.up);
                // Weaker under hard braking: brake too hard into a bend at speed and the car can still spin, but it can be caught.
                float caught = 1f - 0.6f * brakeHard;
                if (over > 0f) rb.AddTorque(transform.up * (-yawRate * 9f * over * caught), ForceMode.Acceleration);
                if (Mathf.Abs(slide) > 60f + slack) rb.AddTorque(transform.up * (Mathf.Sign(slide) * (Mathf.Abs(slide) - 60f - slack) * 0.25f * caught), ForceMode.Acceleration);
            }

            int grounded = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = WheelLocal[i]; local.y = MountHeight;
                Vector3 mount = transform.TransformPoint(local);
                Vector3 up = transform.up;
                compress[i] = 0f;
                if (!Physics.Raycast(mount, -up, out var hit, RaySpan, Layers.WorldMask, QueryTriggerInteraction.Ignore)) continue;
                if (hit.distance >= SpringRest) continue;
                grounded++;
                compress[i] = SpringRest - hit.distance;
                Vector3 velAt = rb.GetPointVelocity(mount);
                float suspVel = Vector3.Dot(velAt, up);
                float f = Mathf.Max(0f, compress[i] * k - suspVel * damper);
                rb.AddForceAtPosition(up * f, mount);

                // Tyre: grip across, drive and brakes along.
                bool front = i < 2;
                Vector3 fwd = front ? Quaternion.AngleAxis(steerAngle, up) * transform.forward : transform.forward;
                Vector3 right = Vector3.Cross(up, fwd).normalized;
                fwd = Vector3.Cross(right, up);
                float lat = Vector3.Dot(velAt, right);
                float lon = Vector3.Dot(velAt, fwd);
                float grip = front ? Mathf.Lerp(0.82f, 0.45f, brakeHard) : Mathf.Lerp(0.82f, 0.14f, looseRear);
                float gripForce = -lat * rb.mass / 4f / dt * grip;
                float load = rb.mass * 9.81f / 4f * (front ? 1f + 0.3f * loadShift : 1f - 0.3f * loadShift);
                // Road tyres hold about 1 g at speed; the extra at low speed keeps town corners easy. The rear gives up first.
                float friction = PlayerDriving ? Mathf.Lerp(1.5f, Mathf.Lerp(1.15f, 0.95f, brakeHard), Mathf.InverseLerp(15f, 45f, speed)) * (front ? 1f : Mathf.Lerp(1.02f, 0.93f, brakeHard)) : 1.5f;
                float limit = load * friction * (front ? 1f : Mathf.Lerp(1f, 0.5f, driftAmount));
                float brakeLimit = load * 1.25f;
                // Above the road, so the car cannot roll over in corners: lower for the player, so that it dives and leans with its weight.
                Vector3 contact = hit.point + up * (PlayerDriving ? 0.12f : 0.25f);

                float drive = 0f;
                if (throttle > 0.01f)
                {
                    if (fwdSpeed < -0.5f) drive = BrakeAccel * throttle;
                    else if (fwdSpeed < TopSpeed) drive = Acceleration * throttle * Mathf.Lerp(1f, 0.2f, Mathf.Clamp01(fwdSpeed / TopSpeed));
                }
                else if (throttle < -0.01f)
                {
                    if (fwdSpeed > 0.5f) drive = BrakeAccel * throttle;
                    else if (fwdSpeed > -ReverseSpeed) drive = Acceleration * 0.6f * throttle;
                }
                else
                {
                    drive = Mathf.Clamp(-lon, -1f, 1f) * (HasDriver ? 0.8f : 3f);    // rolling resistance, more when nobody drives
                }
                if (braking) drive = Mathf.Clamp(-lon * 2f, -BrakeAccel, BrakeAccel);
                if (handbrake)
                {
                    // With the throttle down it is a power slide, with little braking; without, the brakes bite on all four.
                    float bite = throttle > 0.1f ? (front ? 0f : 0.12f) : (front ? 1f : 0.7f);
                    float held = Mathf.Clamp(-lon * 2f, -BrakeAccel, BrakeAccel) * bite;
                    drive = throttle > 0.1f ? drive * (front ? 1f : 0.6f) + held : held;
                }

                // Tyres leave marks on the road when they slide or lock.
                bool sliding = Mathf.Abs(lat) > 2.2f || (handbrake && Mathf.Abs(lon) > 2f && throttle <= 0.1f && (!front || Mathf.Abs(lon) > 6f));
                if (sliding && Surface.Paintable(hit.collider))
                {
                    float amount = Mathf.Clamp01(0.45f + (Mathf.Abs(lat) + Mathf.Abs(lon) * 0.25f) * 0.08f);
                    SkidMarks.Instance?.Add(tyreKey * 8L + i, hit.point, hit.normal, right, 0.2f, amount);
                    if (!front && Time.time >= nextTyreSmoke)
                    {
                        nextTyreSmoke = Time.time + 0.05f;
                        FxPool.Instance?.TyreSmoke(hit.point + Vector3.up * 0.1f);
                    }
                }
                // A tyre has one budget of grip: what goes on braking is gone from steering. Some is always left, less the harder the braking at speed.
                // Steering while braking at speed takes some of the braking away, which leaves the tyres grip to turn the car with.
                float turning = drive < 0f == lon > 0f ? Mathf.Abs(steerSmooth) * brakeHard : 0f;
                float along = Mathf.Clamp(drive * rb.mass / 4f * (1f - 0.4f * turning), -brakeLimit, brakeLimit);
                float used = along / brakeLimit;
                float across = limit * Mathf.Sqrt(Mathf.Max(0f, 1f - used * used));
                float least = PlayerDriving ? Mathf.Lerp(0.85f, front ? Mathf.Lerp(0.55f, 0.45f, flatOut) : Mathf.Lerp(0.3f, 0.16f, flatOut), brakeHard) : 0.7f;
                float budget = Mathf.Max(across, limit * least);
                if (PlayerDriving)
                {
                    // A tyre's grip follows how far it is slipping: it builds quickly with the slip angle, peaks at a few degrees, and past that falls away,
                    // further when the brakes are on. So the car leans on its tyres before it turns, and once one lets go, it has let go. Never more than
                    // would stop the sideways motion in one step, or it would shake at low speed.
                    float slipAngle = Mathf.Atan2(Mathf.Abs(lat), Mathf.Abs(lon) + 2f);
                    float curve = Mathf.Sin(Mathf.Lerp(1.25f, 1.35f, brakeHard) * Mathf.Atan(Mathf.Lerp(16f, 12f, brakeHard) * slipAngle));
                    gripForce = Mathf.Sign(gripForce) * Mathf.Min(Mathf.Abs(gripForce), budget * curve);
                }
                else gripForce = Mathf.Clamp(gripForce, -budget, budget);
                rb.AddForceAtPosition(right * gripForce, contact);
                rb.AddForceAtPosition(fwd * along, contact);
            }
            if (grounded == 4) rb.AddForce(-transform.up * (speed * speed * 0.8f));   // light downforce

            if (speed > 3f) CheckRunOver(speed);

            // Tyres squeal for as long as the car is being stopped: Space, or pushing against the way it is rolling. Others only when they brake from speed.
            bool stopping = handbrake || braking || (throttle < -0.1f && fwdSpeed > 2f) || (throttle > 0.1f && fwdSpeed < -2f);
            UpdateSkidSound(grounded > 2 && stopping && speed > (PlayerDriving ? 3f : 7f), speed, dt);

            // Back on the wheels if it ends up on its side.
            if (Vector3.Dot(transform.up, Vector3.up) < 0.35f && speed < 2f)
            {
                tiltTime += dt;
                if (tiltTime > 2.5f && HasDriver)
                {
                    tiltTime = 0f;
                    transform.SetPositionAndRotation(transform.position + Vector3.up * 1f, Quaternion.Euler(0, transform.eulerAngles.y, 0));
                    rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                }
            }
            else tiltTime = 0f;

            if (!HasDriver && !OnFire && !Exploded)
            {
                idleTime = speed < 0.4f && rb.angularVelocity.sqrMagnitude < 0.05f ? idleTime + dt : 0f;
                if (idleTime > 1.5f) GoToSleep();
            }
        }

        void UpdateSkidSound(bool on, float speed, float dt)
        {
            if (on && !squealing) Screeches++;
            squealing = on;
            skidLevel = Mathf.MoveTowards(skidLevel, on ? 1f : 0f, dt / (on ? 0.08f : 0.18f));
            if (skid == null)
            {
                var clip = GameAudio.Instance != null ? GameAudio.Instance.SkidLoop : null;
                if (!on || clip == null) return;
                skid = MakeSource(true);
                skid.clip = clip;
            }
            skid.volume = skidLevel * 0.7f;
            skid.pitch = Mathf.Lerp(0.92f, 1.08f, Mathf.Clamp01(speed / 30f));
            if (skidLevel > 0f && !skid.isPlaying)
            {
                skid.time = Random.value * skid.clip.length * 0.9f;    // not the same stretch of squeal every time
                skid.Play();
            }
            else if (skidLevel <= 0f && skid.isPlaying) skid.Stop();
        }

        void StopSkidSound()
        {
            squealing = false; skidLevel = 0f;
            if (skid != null) skid.Stop();
        }

        void CheckRunOver(float speed)
        {
            Vector3 center = transform.TransformPoint(new Vector3(0, 0.9f, 0));
            Vector3 half = new Vector3(BodySize.x * 0.5f + 0.15f, 0.9f, BodySize.z * 0.5f + 0.25f);
            int n = Physics.OverlapBoxNonAlloc(center, half, overlap, transform.rotation, 1 << Layers.Character, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var h = overlap[i].GetComponentInParent<Health>();
                if (h == null || h.Dead || h == DriverHealth) continue;
                if (speed < 5f && h.GetComponent<EnemyAI>() == null)
                {
                    // A slow nudge does not kill the player or a pedestrian: it knocks them over.
                    var rig = h.GetComponentInChildren<CharacterRig>();
                    if (rig != null && rig.KnockDown())
                    {
                        PeopleKnocked++;
                        var ga = GameAudio.Instance;
                        ga?.PlayAt(ga.CarHit, h.transform.position + Vector3.up, 0.45f, 10f);
                    }
                    continue;
                }
                Vector3 impulse = rb.linearVelocity * 0.75f + Vector3.up * Mathf.Min(7f, speed * 0.4f);
                Vector3 point = h.transform.position + Vector3.up * 1.1f;
                h.Apply(Health.MaxHealth, HitZone.Body, point, rb.linearVelocity.normalized, DriverHealth, DamageCause.Vehicle, impulse);
                rb.AddForce(-rb.linearVelocity.normalized * rb.mass * 1.5f, ForceMode.Impulse);
                PeopleHit++;
                var a = GameAudio.Instance;
                a?.PlayAt(a.CarHit, point, 1f, 14f);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            float fwd = ForwardSpeed;
            wheelSpin += fwd / Mathf.Max(0.1f, WheelRadius) * Mathf.Rad2Deg * dt;
            for (int i = 0; i < 4 && WheelVisuals != null && i < WheelVisuals.Length; i++)
            {
                var w = WheelVisuals[i];
                if (w == null || !w.gameObject.activeSelf) continue;
                float steerA = i < 2 ? steerSmooth * Mathf.Lerp(34f, 9f, Mathf.Clamp01(Speed / 26f)) : 0f;
                w.localRotation = Quaternion.Euler(0, steerA, 0) * Quaternion.Euler(wheelSpin, 0, 0);
                Vector3 p = WheelLocal[i]; p.y = WheelRadius + compress[i] * 0.5f;
                w.localPosition = p;
            }

            if (engine != null && engine.isPlaying)
            {
                float rev = Mathf.Abs(fwd) / TopSpeed;
                engine.pitch = Mathf.Lerp(engine.pitch, 0.7f + rev * 1.3f + Mathf.Abs(throttle) * 0.25f, dt * 4f);
                engine.volume = 0.55f + Mathf.Abs(throttle) * 0.45f;
            }
            if (exhaust != null)
            {
                var em = exhaust.emission;
                em.rateOverTime = HasDriver ? 10f + Mathf.Abs(throttle) * 55f : 0f;
            }
            bool lampsOn = ForceLit || (HasDriver && (LightsOn || DayNightCycle.NightAmount > 0.25f));
            if (Headlights != null) foreach (var l in Headlights) if (l != null) l.enabled = lampsOn;
            if (LampRenderer != null && LampRenderer.enabled)
            {
                LampRenderer.GetPropertyBlock(block);
                block.SetFloat("_Lit", lampsOn ? 1f : 0.05f);
                block.SetFloat("_Brake", (braking || handbrake || throttle < -0.1f) ? 1f : 0f);
                LampRenderer.SetPropertyBlock(block);
            }

            if (OnFire && !Exploded)
            {
                CurrentHealth -= BurnPerSecond * dt;
                if (CurrentHealth <= 0f) Explode(null);
            }
        }

        // ---- damage ----

        Health lastDamager;
        Quaternion homeRot;
        Material paintMat;
        BoxCollider body;

        /// <summary>Back in its own parking spot, at rest: the automated test clears the street with this.</summary>
        public void ParkAtHome()
        {
            if (HasDriver || Exploded) return;
            CancelInvoke();
            Teleport(Home, homeRot);
            parkedAt = Home;
            CurrentHealth = MaxHealth;
            ClearDamageSmoke();
            drivenVisuals = false;
            UpdateShell();
            rb.interpolation = RigidbodyInterpolation.None;
            rb.isKinematic = true;
            enabled = false;
            if (obstacle != null) obstacle.enabled = false;
        }

        /// <summary>The automated test drives through a live city: this keeps a stray blast from ending a driving check.</summary>
        public bool Protected { get; set; }
        /// <summary>For the test's log.</summary>
        public string LastDamage { get; private set; } = "none";
        string lastCrashedInto = "?";

        /// <summary>Back to full health, smoke gone: for the automated test.</summary>
        public void Repair()
        {
            CurrentHealth = MaxHealth;
            ClearDamageSmoke();
        }

        public void TakeDamage(float amount, Vector3 point, Vector3 normal, Health source, bool crash = false)
        {
            if (Exploded || Protected) return;
            Wake();
            CurrentHealth -= amount;
            LastDamage = $"{amount:F0} from {(source != null ? source.name : crash ? "a crash" : "a blast")}" + (crash ? $" into {lastCrashedInto}" : "");
            if (source != null) lastDamager = source;
            UpdateDamageSmoke();
            if (!OnFire && CurrentHealth <= (crash ? CrashBurnThreshold : BurnThreshold)) StartFire();
            if (CurrentHealth <= 0f) Explode(source);
        }

        /// <summary>Damage shows: the more worn the car, however it got that way, the more it smokes from under the bonnet.</summary>
        void UpdateDamageSmoke()
        {
            SmokeAmount = Mathf.InverseLerp(MaxHealth * 0.92f, MaxHealth * 0.25f, CurrentHealth);
            if (SmokeAmount <= 0f || Exploded) return;
            var fx = FxPool.Instance;
            if (damageSmoke == null && fx != null) damageSmoke = fx.MakeBurnSmoke(transform, new Vector3(0, BodySize.y * 0.62f, BodySize.z * 0.32f));
            if (damageSmoke != null)
            {
                var em = damageSmoke.emission;
                em.rateOverTime = Mathf.Lerp(2.5f, 24f, SmokeAmount);
            }
        }

        void ClearDamageSmoke()
        {
            if (damageSmoke != null) Destroy(damageSmoke.gameObject);
            damageSmoke = null;
            SmokeAmount = 0f;
        }

        void StartFire()
        {
            OnFire = true;
            var fx = FxPool.Instance;
            if (fx != null)
            {
                Vector3 front = new Vector3(0, BodySize.y * 0.75f, BodySize.z * 0.3f);
                fire = fx.MakeFire(transform, front, 1f);
                burnSmoke = fx.MakeBurnSmoke(transform, front + Vector3.up * 0.5f);
            }
            fireLoop = MakeSource(true);
            var a = GameAudio.Instance;
            if (a != null && a.FireLoop != null) { fireLoop.clip = a.FireLoop; fireLoop.volume = 0.9f; fireLoop.Play(); }
        }

        public void Explode(Health source)
        {
            if (Exploded) return;
            Exploded = true;
            if (source == null) source = lastDamager;
            OnFire = false;
            Vector3 c = transform.TransformPoint(new Vector3(0, 0.7f, 0));
            var fx = FxPool.Instance;
            if (fx != null) fx.Explosion(c);
            var a = GameAudio.Instance;
            a?.PlayAt(a.Explosion, c, 1f, 30f);
            if (engine != null) engine.Stop();
            if (horn != null) horn.Stop();
            StopSkidSound();
            ExplodedEvent?.Invoke(this);

            // Area damage: certain death close by, falling off to nothing at the edge.
            Blast.Damage(c, ExplosionRadius, 4.5f, source, this);
            // The driver inside dies with the car.
            if (DriverHealth != null) DriverHealth.Apply(Health.MaxHealth, HitZone.Body, c, Vector3.up, source, DamageCause.Explosion, Vector3.up * 8f);

            // What is left: a burnt shell that still has physics, throwing itself up a little.
            if (CharredMaterial != null && BodyRenderers != null) foreach (var r in BodyRenderers) if (r != null) r.sharedMaterial = CharredMaterial;
            UpdateShell();    // the whole shell again, burnt: no holes, no loose doors
            if (LampRenderer != null) LampRenderer.enabled = false;
            if (Headlights != null) foreach (var l in Headlights) if (l != null) l.enabled = false;
            Wake();
            rb.AddForce(Vector3.up * rb.mass * 5.5f, ForceMode.Impulse);
            rb.AddTorque(new Vector3(Random.Range(-1f, 1f), 0.2f, Random.Range(-1f, 1f)) * rb.mass * 2f, ForceMode.Impulse);
            if (fx != null)
            {
                Vector3 top = new Vector3(0, BodySize.y * 0.7f, 0);
                if (fire == null) fire = fx.MakeFire(transform, top, 1.5f);
                if (burnSmoke == null) burnSmoke = fx.MakeBurnSmoke(transform, top + Vector3.up * 0.6f);
            }
            DriverHealth = null;
            Invoke(nameof(Vanish), WreckSeconds - WreckFadeSeconds);
        }

        /// <summary>The wreck goes after a few seconds, and a new car stands in the original spot a little later.</summary>
        void Vanish()
        {
            // The flames die down and the wreck fades; only then is it gone.
            if (fire != null) fire.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (damageSmoke != null) damageSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (burnSmoke != null) burnSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Fader.Out(gameObject, WreckFadeSeconds, Gone);
        }

        const float WreckFadeSeconds = 1f, ComeBackFadeSeconds = 0.8f;

        void Gone()
        {
            foreach (var r in BodyRenderers) if (r != null) r.enabled = false;
            if (body != null) body.enabled = false;
            WakeWhatLayOnIt();
            if (fire != null) Destroy(fire.gameObject);
            if (burnSmoke != null) Destroy(burnSmoke.gameObject);
            if (fireLoop != null) fireLoop.Stop();
            fire = null; burnSmoke = null;
            ClearDamageSmoke();
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;   // interpolation would drag it back towards the old pose
            enabled = false;
            Invoke(nameof(Restore), RespawnSeconds);
        }

        /// <summary>Anything resting on the wreck falls now that it is gone.</summary>
        void WakeWhatLayOnIt()
        {
            int n = Physics.OverlapBoxNonAlloc(transform.TransformPoint(new Vector3(0, BodySize.y, 0)), new Vector3(BodySize.x, BodySize.y + 1.5f, BodySize.z) * 0.6f,
                overlap, transform.rotation, (1 << Layers.Debris) | Layers.WorldMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var other = overlap[i].GetComponentInParent<Vehicle>();
                if (other != null && other != this && !other.Exploded) other.Wake();
                else if (overlap[i].attachedRigidbody != null && !overlap[i].attachedRigidbody.isKinematic) overlap[i].attachedRigidbody.WakeUp();
            }
        }

        void Restore()
        {
            // Not on top of anyone: tries again shortly.
            Vector3 c = Home + Vector3.up * 1f;
            if (Physics.CheckBox(c, new Vector3(BodySize.x * .5f, 0.7f, BodySize.z * .5f), homeRot, (1 << Layers.Character) | Layers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                Invoke(nameof(Restore), 2f);
                return;
            }
            transform.SetPositionAndRotation(Home, homeRot);
            rb.position = Home; rb.rotation = homeRot;
            parkedAt = Home;
            CurrentHealth = MaxHealth;
            OnFire = false; Exploded = false;
            lastDamager = null;
            LightsOn = false;
            throttle = 0f; steer = 0f; steerSmooth = 0f; handbrake = false; braking = false; driftAmount = 0f; drivenVisuals = false;
            UpdateShell();
            foreach (var r in BodyRenderers)
                if (r != null) { r.enabled = true; r.sharedMaterial = paintMat; }
            if (body != null) body.enabled = true;
            if (obstacle != null) obstacle.enabled = false;
            Physics.SyncTransforms();
            Fader.In(gameObject, ComeBackFadeSeconds);
        }

        void OnCollisionEnter(Collision c)
        {
            // People do not dent a car: hitting them has its own rules (CheckRunOver).
            if (c.collider.GetComponentInParent<Health>() != null || c.gameObject.layer == Layers.Debris) return;   // nor do lumps of them
            Vector3 rel = c.relativeVelocity;
            float impact = rel.magnitude;
            if (impact < 5f) return;
            var other = c.collider.GetComponentInParent<Vehicle>();
            if (other != null && other != this) other.Wake();
            if (Time.time < lastCrash + 0.25f) return;
            lastCrash = Time.time;
            var a = GameAudio.Instance;
            a?.PlayAt(a.Crash, c.GetContact(0).point, Mathf.Clamp01(impact / 18f), 12f);
            FxPool.Instance?.Sparks(c.GetContact(0).point, c.GetContact(0).normal);
            lastCrashedInto = c.collider.name;
            if (impact > 11f) TakeDamage((impact - 11f) * 2.2f, c.GetContact(0).point, c.GetContact(0).normal, null, true);
        }

        /// <summary>Where someone stands after getting out on the driver's side (or, blocked, on the other).</summary>
        public Vector3 ExitPosition() => ExitPosition(out _);

        public Vector3 ExitPosition(out int side)
        {
            Vector3 left = transform.TransformPoint(ExitLocal);
            Vector3 right = transform.TransformPoint(new Vector3(-ExitLocal.x, ExitLocal.y, ExitLocal.z));
            int index = 0;
            foreach (var p in new[] { left, right, transform.TransformPoint(new Vector3(0, ExitLocal.y, -BodySize.z * 0.5f - 1.1f)) })
            {
                side = index == 1 ? 1 : 0;
                index++;
                Vector3 test = p + Vector3.up * 1f;
                if (Physics.CheckSphere(test, 0.35f, Layers.WorldMask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.Raycast(test, Vector3.down, out var hit, 3f, Layers.WorldMask, QueryTriggerInteraction.Ignore)) return hit.point + Vector3.up * 0.02f;
            }
            side = 0;
            return transform.position + Vector3.up * (BodySize.y + 0.5f);
        }

        public static Vehicle Nearest(Vector3 pos, float radius, bool mustBeFree)
        {
            Vehicle best = null;
            float bestD = radius * radius;
            foreach (var v in All)
            {
                if (v.Exploded || (mustBeFree && v.HasDriver)) continue;
                float d = (v.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = v; }
            }
            return best;
        }
    }
}
