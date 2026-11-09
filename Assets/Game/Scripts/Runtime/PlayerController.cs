using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GreatHeistAuto
{
    /// <summary>
    /// Player movement, cars, camera (first / third person on foot and in a car, V toggles), and shooting.
    /// The third person camera sits over the shoulder; bullets go from the muzzle to the point under the crosshair.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        public WeaponData[] Weapons;
        public Camera Cam;
        public float WalkSpeed = 1.7f, SprintSpeed = 6.6f, CrouchSpeed = 1.1f, AimSpeed = 1.25f, JumpSpeed = CharacterRig.JumpSpeed, Gravity = CharacterRig.JumpGravity;
        public float MouseSensitivity = 0.09f;
        public float BaseFov = 58f;     // narrow enough that objects at the screen edge do not stretch

        public CharacterRig Rig { get; private set; }
        public Health Health { get; private set; }
        public GunState Gun => slot >= 0 ? guns[slot] : null;
        public bool Armed => slot >= 0;
        public int Slot => slot;
        public float CurrentSpread { get; private set; }
        public bool InputEnabled { get; set; } = true;
        public float Yaw => yaw;
        public float Pitch => pitch;
        public Vehicle CurrentVehicle { get; private set; }
        public Vehicle VehicleInReach { get; private set; }
        public bool AimingDownSights { get; private set; }
        public float AdsAmount => adsLerp;
        public float MoveSpeed { get; private set; }

        /// <summary>Automated test hook: when set, this replaces the keyboard and mouse.</summary>
        public Func<PlayerCommand> CommandOverride;

        public event Action Fired;

        CharacterController cc;
        GunState[] guns;
        int slot = -1;     // -1: hands empty
        float orbitYaw, orbitPitch;
        public bool Orbiting { get; private set; }
        float airTime, yaw, pitch, verticalVel, crouchLerp, camBoom = 2.4f, adsLerp, reloadWave;
        float carLookYaw, carLookPitch, carLookIdle, carFov = 60f, shake;
        Vector3 chasePos;
        bool chaseInit;
        Vector3 velocity;
        bool crouching, jumping;
        public float VerticalSpeed => verticalVel;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            Health = GetComponent<Health>();
            Rig = GetComponentInChildren<CharacterRig>();
            guns = new GunState[Weapons.Length];
            for (int i = 0; i < guns.Length; i++) guns[i] = new GunState(Weapons[i]);
        }

        void Start()
        {
            Rig.Holstered = true;
            yaw = transform.eulerAngles.y;
            Rig.AimPoint = transform.position + transform.forward * 20f + Vector3.up * 1.4f;
            Blast.Happened += OnAnyExplosion;
        }

        void OnDestroy() => Blast.Happened -= OnAnyExplosion;

        void OnAnyExplosion(Vector3 at, float radius)
        {
            float d = Vector3.Distance(at, transform.position);
            shake = Mathf.Max(shake, Mathf.Clamp01(1.2f - d / (45f * radius / 11f)));
        }

        public void Respawn(Vector3 pos, Quaternion rot)
        {
            StopAllCoroutines();
            Boarding = false;
            if (CurrentVehicle != null) LeaveVehicle(false);
            var fling = GetComponent<CorpseFling>();
            if (fling != null) Destroy(fling);
            cc.enabled = false;
            transform.SetParent(null);
            transform.SetPositionAndRotation(pos, rot);
            cc.enabled = true;
            yaw = rot.eulerAngles.y; pitch = 0f;
            velocity = Vector3.zero; verticalVel = 0f;
            Health.Restore();
            foreach (var g in guns) g.Refill();
            Rig.Revive();
            Equip(-1);
            orbitYaw = 0f; orbitPitch = 0f;
        }

        PlayerCommand ReadDevices()
        {
            var c = new PlayerCommand { SelectSlot = -1 };
            var k = Keyboard.current; var m = Mouse.current;
            if (k != null)
            {
                c.Move = new Vector2((k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0), (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0));
                c.Jump = k.spaceKey.wasPressedThisFrame;
                c.JumpHeld = k.spaceKey.isPressed;
                c.Sprint = k.leftShiftKey.isPressed;
                c.CrouchHeld = k.leftCtrlKey.isPressed || k.cKey.isPressed;
                c.Reload = k.rKey.wasPressedThisFrame;
                c.Interact = k.eKey.wasPressedThisFrame;
                c.Honk = k.tabKey.isPressed;
                c.ToggleLights = k.lKey.wasPressedThisFrame;
                if (k.digit0Key.wasPressedThisFrame) c.SelectSlot = -2;
                if (k.digit1Key.wasPressedThisFrame) c.SelectSlot = 0;
                if (k.digit2Key.wasPressedThisFrame) c.SelectSlot = 1;
                if (k.digit3Key.wasPressedThisFrame) c.SelectSlot = 2;
                if (k.digit4Key.wasPressedThisFrame) c.SelectSlot = 3;
            }
            if (m != null)
            {
                c.Look = m.delta.ReadValue() * MouseSensitivity;
                c.Fire = m.leftButton.isPressed;
                c.FirePressed = m.leftButton.wasPressedThisFrame;
                c.Ads = m.rightButton.isPressed;
                c.Orbit = m.middleButton.isPressed;
                float s = m.scroll.ReadValue().y;
                c.CycleSlot = s > 0.1f ? 1 : s < -0.1f ? -1 : 0;
            }
            return c;
        }

        void Update()
        {
            var cmd = CommandOverride != null ? CommandOverride() : (InputEnabled ? ReadDevices() : new PlayerCommand { SelectSlot = -1 });
            if (Health.Dead) cmd = new PlayerCommand { SelectSlot = -1 };

            shake = Mathf.MoveTowards(shake, 0f, Time.deltaTime * 1.4f);

            if (CurrentVehicle != null) { UpdateDriving(cmd); return; }
            if (Health.Dead) { Rig.Velocity = Vector3.zero; AimingDownSights = false; MoveSpeed = 0f; punchLandsAt = -1f; return; }

            if (Boarding) { Rig.Crouching = false; MoveSpeed = 0f; return; }

            // Hurt (half health or less): a limp when walking with the gun away.
            Rig.Style = Health.Current <= Health.MaxHealth * 0.5f ? CharacterRig.Gait.Injured : CharacterRig.Gait.Normal;

            // Knocked over by a car: on the ground until the get-up is done, only gravity moves the body.
            if (Rig.Down)
            {
                velocity = new Vector3(0f, cc.isGrounded ? -1f : velocity.y - Gravity * Time.deltaTime, 0f);
                if (cc.enabled) cc.Move(velocity * Time.deltaTime);
                Rig.Velocity = Vector3.zero; Rig.Crouching = false; Rig.Grounded = true;
                MoveSpeed = 0f; AimingDownSights = false;
                return;
            }

            // A free car, or one that is slow enough to pull someone out of.
            var near = Vehicle.Nearest(transform.position, 4.2f, false);
            VehicleInReach = near != null && (!near.HasDriver || (near.Speed < 6f && near.DriverHealth.GetComponent<EnemyAI>() != null)) ? near : null;
            if (cmd.Interact && VehicleInReach != null) { StartCoroutine(Board(VehicleInReach)); return; }

            // Nothing to aim with empty hands.
            if (!Armed) cmd.Ads = false;

            // Standing still with the middle button held, the mouse swings the camera around the player, who stays put.
            Orbiting = cmd.Orbit && cmd.Move.sqrMagnitude < 0.01f && !cmd.Ads && cc.isGrounded;
            if (Orbiting)
            {
                orbitYaw += cmd.Look.x;
                orbitPitch = Mathf.Clamp(orbitPitch - cmd.Look.y, -35f - pitch, 65f - pitch);
                cmd.Look = Vector2.zero;
            }
            else
            {
                float back = Time.deltaTime * 260f;
                orbitYaw = Mathf.MoveTowards(Mathf.DeltaAngle(0f, orbitYaw), 0f, back);
                orbitPitch = Mathf.MoveTowards(orbitPitch, 0f, back);
            }

            // Look.
            float adsScale = cmd.Ads ? 0.55f : 1f;
            yaw += cmd.Look.x * adsScale;
            pitch = Mathf.Clamp(pitch - cmd.Look.y * adsScale, -75f, 75f);
            transform.rotation = Quaternion.Euler(0, yaw, 0);

            // Crouch: the collider shrinks, unless something overhead blocks standing up.
            bool wantCrouch = cmd.CrouchHeld;
            if (crouching && !wantCrouch && Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.3f, Vector3.up, out _, 1.2f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                wantCrouch = true;
            crouching = wantCrouch;
            crouchLerp = Mathf.MoveTowards(crouchLerp, crouching ? 1f : 0f, Time.deltaTime * 6f);
            cc.height = Mathf.Lerp(1.8f, 1.15f, crouchLerp);
            cc.center = new Vector3(0, cc.height * 0.5f + cc.skinWidth, 0);

            // Move. Holding aim means walking pace, with no running.
            var wish = (transform.right * cmd.Move.x + transform.forward * cmd.Move.y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            AimingDownSights = cmd.Ads;
            bool canSprint = cmd.Sprint && cmd.Move.y > 0.1f && !crouching && !cmd.Ads && !cmd.Fire;
            float speed = cmd.Ads ? AimSpeed : crouching ? CrouchSpeed : canSprint ? SprintSpeed : WalkSpeed;
            var flat = Vector3.MoveTowards(new Vector3(velocity.x, 0, velocity.z), wish * speed, Time.deltaTime * (cc.isGrounded ? 30f : 6f));

            if (cc.isGrounded)
            {
                verticalVel = -1f;
                jumping = false;
                if (cmd.Jump && !crouching)
                {
                    verticalVel = JumpSpeed;
                    jumping = true;
                    Rig.PlayJump();
                }
            }
            else verticalVel -= Gravity * Time.deltaTime;

            velocity = new Vector3(flat.x, verticalVel, flat.z);
            if (cc.enabled) cc.Move(velocity * Time.deltaTime);
            var real = cc.velocity;
            MoveSpeed = new Vector3(real.x, 0, real.z).magnitude;

            Rig.Velocity = new Vector3(real.x, 0, real.z);
            Rig.Crouching = crouching;
            airTime = cc.isGrounded ? 0f : airTime + Time.deltaTime;
            Rig.Grounded = !jumping && airTime < 0.18f;   // curbs must not trigger the fall animation
            Rig.Sprinting = canSprint;
            Rig.AimingDownSights = cmd.Ads;
            Rig.AimWeightTarget = (canSprint && MoveSpeed > 4f && !cmd.Fire) ? 0f : 1f;

            HandleWeapons(cmd, canSprint);
            adsLerp = Mathf.MoveTowards(adsLerp, cmd.Ads ? 1f : 0f, Time.deltaTime * 6f);

            float moving = Mathf.Clamp01(MoveSpeed / SprintSpeed);
            float spread = !Armed ? 0f : Gun.Data.SpreadDegrees * Mathf.Lerp(1f, 0.3f, adsLerp) * Mathf.Lerp(1f, 2.4f, moving) * (crouching ? 0.7f : 1f);
            CurrentSpread = Mathf.Lerp(CurrentSpread, spread, Time.deltaTime * 10f);
        }

        // ---- cars ----

        public bool Boarding { get; private set; }

        /// <summary>The door opens (whoever is at the wheel gets out first), the player goes to it and gets in, and it closes behind them.</summary>
        System.Collections.IEnumerator Board(Vehicle v)
        {
            Boarding = true;
            Gun?.CancelReload();
            AimingDownSights = false; adsLerp = 0f;
            cc.enabled = false;
            int side = v.transform.InverseTransformPoint(transform.position).x < 0f ? 0 : 1;
            var driver = v.HasDriver ? v.DriverHealth.GetComponent<EnemyAI>() : null;
            if (driver != null)
            {
                driver.PulledOut(side);
                float until = Time.time + 2f;
                while (v.HasDriver && Time.time < until) yield return null;
                yield return new WaitForSeconds(0.25f);
            }
            float give = Time.time + 1f;
            bool ok = !v.Exploded && !v.HasDriver;
            if (ok) v.OpenDoor(side, 0.75f);
            while (ok && Time.time < give)
            {
                if (v.Exploded || Health.Dead || v.HasDriver) { ok = false; break; }
                Vector3 to = (v.DoorSpot(side) - transform.position).Flat();
                if (to.magnitude < 0.12f) break;
                Vector3 step = to.normalized * Mathf.Min(to.magnitude, 3.2f * Time.deltaTime);
                transform.position += step;
                Rig.Velocity = to.normalized * 3.2f;
                yield return null;
            }
            Rig.Velocity = Vector3.zero;
            if (ok && !Health.Dead) yield return new WaitForSeconds(0.12f);
            Boarding = false;
            if (ok && !Health.Dead && !v.Exploded && !v.HasDriver) EnterVehicle(v, side);
            else if (!Health.Dead) cc.enabled = true;
        }

        public void EnterVehicle(Vehicle v, int side = 0)
        {
            CurrentVehicle = v;
            Gun?.CancelReload();
            cc.enabled = false;
            crouching = false; crouchLerp = 0f;
            AimingDownSights = false; adsLerp = 0f;
            transform.SetParent(v.transform, false);
            transform.SetLocalPositionAndRotation(v.SeatLocal, Quaternion.identity);
            v.Enter(Health, side, false);
            Rig.EnterSeat(v);
            chaseInit = false;
            carLookYaw = 0f; carLookPitch = 0f;
            carFov = 60f;
        }

        public void LeaveVehicle(bool placeOutside)
        {
            var v = CurrentVehicle;
            if (v == null) return;
            CurrentVehicle = null;
            Vector3 exit = v.ExitPosition(out int side);
            float newYaw = v.transform.eulerAngles.y;
            transform.SetParent(null);
            if (!v.Exploded) v.Exit(side);
            Rig.LeaveSeat();
            if (!placeOutside) transform.position = exit;    // a body stays beside the car, not in it
            if (placeOutside)
            {
                transform.SetPositionAndRotation(exit, Quaternion.Euler(0, newYaw, 0));
                cc.enabled = true;
                yaw = newYaw; pitch = 0f;
                velocity = Vector3.zero;
            }
        }

        void UpdateDriving(PlayerCommand cmd)
        {
            var v = CurrentVehicle;
            if (Health.Dead || v.Exploded)
            {
                LeaveVehicle(false);
                return;
            }
            if (cmd.Interact && Mathf.Abs(v.ForwardSpeed) < 7f) { LeaveVehicle(true); return; }
            if (cmd.ToggleLights) v.LightsOn = !v.LightsOn;
            v.PlayerDriving = true;
            v.SetInput(cmd.Move.y, cmd.Move.x, false, cmd.JumpHeld, cmd.Honk);
            MoveSpeed = 0f;
            Rig.Velocity = Vector3.zero;

            // Free look drifts back behind the wheel after a moment.
            if (cmd.Look.sqrMagnitude > 0.0004f) carLookIdle = 0f; else carLookIdle += Time.deltaTime;
            carLookYaw = Mathf.Clamp(carLookYaw + cmd.Look.x, -150f, 150f);
            carLookPitch = Mathf.Clamp(carLookPitch - cmd.Look.y, -35f, 35f);
            if (carLookIdle > 1.2f)
            {
                carLookYaw = Mathf.MoveTowards(carLookYaw, 0f, Time.deltaTime * 120f);
                carLookPitch = Mathf.MoveTowards(carLookPitch, 0f, Time.deltaTime * 40f);
            }
        }

        // ---- guns ----

        void HandleWeapons(PlayerCommand cmd, bool sprinting)
        {
            // The wheel runs through the guns and empty hands: ... 3, 4, nothing, 1, 2 ...
            int want = cmd.SelectSlot == -2 ? -1 : cmd.SelectSlot >= 0 ? cmd.SelectSlot : slot;
            if (cmd.CycleSlot != 0)
            {
                int count = guns.Length + 1;
                want = (slot + 1 + cmd.CycleSlot + count) % count - 1;
            }
            if (want != slot && want < guns.Length) Equip(want);

            var gun = Gun;
            if (gun == null)
            {
                reloadWave = 0f; Rig.ReloadWeight = 0f;
                Fists(cmd);
                return;
            }
            gun.Tick();
            if (cmd.Reload && gun.StartReload()) PlayReloadSound();

            float target = gun.Reloading ? Mathf.Sin(gun.ReloadProgress * Mathf.PI) : 0f;
            reloadWave = Mathf.MoveTowards(reloadWave, target, Time.deltaTime * 5f);
            Rig.ReloadWeight = reloadWave;

            bool trigger = gun.Data.Automatic ? cmd.Fire : cmd.FirePressed;
            if (!trigger) return;
            if (gun.Reloading) return;
            if (gun.InMag <= 0)
            {
                if (cmd.FirePressed && gun.StartReload()) PlayReloadSound();
                else if (cmd.FirePressed) GameAudio.Instance?.PlayAt(GameAudio.Instance.Empty, transform.position, 0.4f);
                return;
            }
            if (!gun.CanFire) return;
            Shoot(gun);
        }

        float nextPunch, punchLandsAt = -1f;
        Health punchVictim;
        public float PunchDamage = 20f;
        readonly Collider[] punchNear = new Collider[16];

        /// <summary>Empty hands punch.</summary>
        void Fists(PlayerCommand cmd)
        {
            if (punchLandsAt > 0f && Time.time >= punchLandsAt)
            {
                punchLandsAt = -1f;
                if (punchVictim != null && !punchVictim.Dead && (punchVictim.transform.position - transform.position).Flat().magnitude < Melee.Reach + 0.25f)
                    Melee.Land(Health, punchVictim, PunchDamage);
            }
            if (!cmd.Fire || Time.time < nextPunch || crouching) return;
            nextPunch = Time.time + CharacterRig.PunchSeconds + 0.06f;
            punchVictim = PunchTarget();
            punchLandsAt = Time.time + CharacterRig.PunchLandsAfter;
            if (punchVictim != null) Melee.Square(gameObject, transform.position, punchVictim.transform.position);
            Rig.PlayPunch(punchVictim != null ? punchVictim.GetComponentInChildren<CharacterRig>() : null);
            Fired?.Invoke();
        }

        /// <summary>The nearest body within arm's length, more or less in front.</summary>
        Health PunchTarget()
        {
            int n = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up, Melee.Reach, punchNear, 1 << Layers.Character, QueryTriggerInteraction.Collide);
            Health best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = punchNear[i].GetComponentInParent<Health>();
                if (h == null || h == Health || h.Dead) continue;
                Vector3 to = (h.transform.position - transform.position).Flat();
                float d = to.magnitude;
                if (d > Melee.Reach || d < bestD == false) continue;
                if (d > 0.3f && Vector3.Angle(transform.forward, to) > 60f) continue;
                best = h; bestD = d;
            }
            return best;
        }

        void Equip(int index)
        {
            Gun?.CancelReload();
            slot = index;
            if (index < 0) { Rig.Holstered = true; return; }
            Rig.Holstered = false;
            Rig.SetWeapon(Weapons[index]);
        }

        void PlayReloadSound() => GameAudio.Instance?.PlayAt(GameAudio.Instance.Reload, transform.position, 0.6f);

        void Shoot(GunState gun)
        {
            gun.ConsumeShot();
            var muzzle = Rig.Muzzle;
            Vector3 dir = (Rig.AimPoint - muzzle.position).normalized;
            dir = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, CurrentSpread), Vector3.Cross(dir, UnityEngine.Random.onUnitSphere)) * dir;
            BulletSystem.Instance.Fire(muzzle.position, dir, gun.Data.BulletSpeed, Rig.Team, Health);
            FxPool.Instance?.MuzzleFlash(muzzle.position, muzzle.forward);
            GameAudio.Instance?.PlayAt(gun.Data.ShotClips, muzzle.position, 0.95f, 25f);
            Rig.Recoil(gun.Data.RecoilDegrees);
            pitch = Mathf.Clamp(pitch - gun.Data.RecoilDegrees * Mathf.Lerp(0.7f, 0.4f, adsLerp), -75f, 75f);
            yaw += UnityEngine.Random.Range(-0.25f, 0.25f) * gun.Data.RecoilDegrees;
            GameManager.Instance?.ReportGunfire(transform.position);
            Fired?.Invoke();
        }

        // ---- camera ----

        void LateUpdate()
        {
            if (Cam == null) return;
            var camT = Cam.transform;

            if (CurrentVehicle != null) { CarCamera(camT, CurrentVehicle); ApplyShake(camT); return; }
            carFov = 60f;

            Quaternion rot = Quaternion.Euler(pitch + orbitPitch, yaw + orbitYaw, 0);
            float eye = Mathf.Lerp(1.62f, 1.02f, crouchLerp);

            {
                Cam.nearClipPlane = 0.08f;
                Vector3 pivot = transform.position + Vector3.up * (eye - 0.02f);
                Vector3 shoulder = rot * new Vector3(Mathf.Lerp(0.5f, 0.42f, adsLerp), 0.05f, 0f);
                // Keep the shoulder offset out of walls.
                if (Physics.SphereCast(pivot, 0.15f, shoulder.normalized, out var side, shoulder.magnitude, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                    shoulder = shoulder.normalized * Mathf.Max(0.05f, side.distance - 0.03f);
                Vector3 start = pivot + shoulder;
                float want = Mathf.Lerp(2.5f, 1.1f, adsLerp);
                Vector3 back = rot * Vector3.back;
                float dist = want;
                if (Physics.SphereCast(start, 0.18f, back, out var hit, want, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                    dist = Mathf.Max(0.25f, hit.distance - 0.05f);
                camBoom = dist < camBoom ? dist : Mathf.MoveTowards(camBoom, dist, Time.deltaTime * 6f);
                camT.SetPositionAndRotation(start + back * camBoom, rot);
            }

            // Zoom while aiming: rifles further than pistols.
            bool rifle = Armed && Gun.Data.Kind == WeaponKind.Rifle;
            float aimFov = rifle ? 34f : 40f;
            float runFov = BaseFov + Mathf.Clamp01(MoveSpeed - 4f) * 3f;
            Cam.fieldOfView = Mathf.Lerp(runFov, aimFov, adsLerp);

            // The aim point is what the crosshair sees, ignoring our own body. While the camera is swung away it stays where it was.
            if (Mathf.Abs(orbitYaw) < 0.5f && Mathf.Abs(orbitPitch) < 0.5f) Rig.AimPoint = AimRay(camT, out _);
            ApplyShake(camT);
        }

        void ApplyShake(Transform camT)
        {
            if (shake <= 0f) return;
            float t = Time.time * 32f;
            camT.rotation *= Quaternion.Euler((Mathf.PerlinNoise(t, 1f) - .5f) * 7f * shake, (Mathf.PerlinNoise(t, 7f) - .5f) * 7f * shake, (Mathf.PerlinNoise(t, 13f) - .5f) * 5f * shake);
        }

        void CarCamera(Transform camT, Vehicle v)
        {
            float speed = v.Speed;
            float fovTarget = Mathf.Lerp(60f, 74f, Mathf.Clamp01(speed / 28f));
            carFov = Mathf.Lerp(carFov, fovTarget, Time.deltaTime * 3f);
            Cam.fieldOfView = carFov;

            {
                Cam.nearClipPlane = 0.1f;
                // A chase camera that swings out in corners, level with the horizon.
                Vector3 flatFwd = Vector3.ProjectOnPlane(v.transform.forward, Vector3.up).normalized;
                if (flatFwd.sqrMagnitude < 0.1f) flatFwd = transform.forward;
                Quaternion orbit = Quaternion.LookRotation(flatFwd) * Quaternion.Euler(carLookPitch + 9f, carLookYaw, 0);
                Vector3 focus = v.transform.position + Vector3.up * 1.25f;
                float dist = Mathf.Lerp(6.2f, 7.6f, Mathf.Clamp01(speed / 28f));
                Vector3 want = focus + orbit * Vector3.back * dist;
                // Above the roof the ray starts outside the car's own box.
                Vector3 from = focus + Vector3.up * 1f;
                Vector3 to = want - from;
                if (Physics.SphereCast(from, 0.25f, to.normalized, out var hit, to.magnitude, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                    want = from + to.normalized * Mathf.Max(0.4f, hit.distance - 0.1f);
                if (!chaseInit) { chasePos = want; chaseInit = true; }
                chasePos = Vector3.Lerp(chasePos, want, 1f - Mathf.Exp(-Time.deltaTime * 11f));
                camT.position = chasePos;
                camT.rotation = Quaternion.LookRotation(focus + flatFwd * 2.5f - camT.position);
            }
        }

        readonly RaycastHit[] aimHits = new RaycastHit[12];

        Vector3 AimRay(Transform camT, out RaycastHit bestHit)
        {
            bestHit = default;
            float start = camBoom + 0.5f;   // start past the character
            Vector3 origin = camT.position + camT.forward * start;
            int n = Physics.RaycastNonAlloc(origin, camT.forward, aimHits, 500f, Layers.BulletMask, QueryTriggerInteraction.Collide);
            float best = 500f;
            Vector3 p = origin + camT.forward * 500f;
            for (int i = 0; i < n; i++)
            {
                var h = aimHits[i];
                if (h.distance >= best) continue;
                if (h.collider.gameObject.layer == Layers.Hitbox)
                {
                    var box = h.collider.GetComponent<Hitbox>();
                    if (box == null || box.Team == Rig.Team) continue;
                }
                best = h.distance; p = h.point; bestHit = h;
            }
            return p;
        }
    }
}
