using UnityEngine;
using UnityEngine.AI;

namespace GreatHeistAuto
{
    /// <summary>
    /// Enemy brain. While the city is calm it strolls with its gun put away, and may drive around. Once hostile it fights from cover: runs to a cover point that really
    /// blocks the line to the player, ducks, pops up (low cover) or steps out (tall cover) for a burst, ducks again. It dodges
    /// sideways or rolls when bullets pass close or the player's crosshair sits on it in the open, relocates when flanked, and
    /// pushes forward when it has lost sight of the player for a while.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class EnemyAI : MonoBehaviour
    {
        enum State { Patrol, ToCover, InCover, Hunt, ToCar, Driving, Dead }
        enum CoverPhase { Hidden, Exposed }

        public float RunSpeed = 5.2f, WalkSpeed = 1.7f;
        public float SightRange = 75f;

        public CharacterRig Rig { get; private set; }
        public Health Health { get; private set; }
        public string StateName => state.ToString();

        // Read by the automated test.
        public static int DodgeCount, CoverReachedCount, PeekCount, CarsEntered;
        public static int ActiveDrivers;

        NavMeshAgent agent;
        GunState gun;
        State state = State.Patrol;
        CoverPhase phase = CoverPhase.Hidden;
        int coverIndex = -1;
        CoverPoint cover;
        Vector3 peekPos, lastKnown;
        bool seesPlayer, alerted;
        float thinkAt, phaseEnd, coverLeaveAt, lastSeenTime = -100f, nextDodge, dodgeEnd, exposedSince, burstLeft, reloadWave;
        Vector3 dodgeDir;
        float accuracy = 1f, flankBias;

        // Grenades: thrown from cover at where the player was last seen, when the player stays out of sight.
        public static int GrenadesThrown;
        static float nextGrenadeAny;
        float nextGrenade, releaseAt = -1f;
        bool tossing;
        Vector3 throwAt;
        float difficulty = 1f;

        // Driving.
        Vehicle car;
        readonly System.Collections.Generic.List<Vector3> route = new();
        int routeLane = -1;
        float nextCarCheck, drivingSince, stuckTime, reverseUntil, ramUntil, nextRam, lastShot = -10f;
        Collider ownCapsule;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.updateRotation = false;
            Health = GetComponent<Health>();
            Rig = GetComponentInChildren<CharacterRig>();
            Health.Damaged += OnDamaged;
            Health.Died += OnDied;
            BulletSystem.BulletFired += OnBulletFired;
            flankBias = Random.value;
            ownCapsule = GetComponent<CapsuleCollider>();
            nextCarCheck = Time.time + Random.Range(3f, 9f);
        }

        void OnDestroy()
        {
            BulletSystem.BulletFired -= OnBulletFired;
            if (state == State.Driving) ActiveDrivers = Mathf.Max(0, ActiveDrivers - 1);
        }

        public void Setup(WeaponData weapon, Color tint, float difficultyScale)
        {
            gun = new GunState(weapon) { CadenceScale = FireRateSlowdown };
            Rig.SetWeapon(weapon);
            Rig.SetTint(tint);
            difficulty = difficultyScale;
            accuracy = Mathf.Lerp(1.15f, 0.65f, Mathf.Clamp01((difficulty - 1f) / 2f));
            agent.speed = WalkSpeed;
            PickPatrol();
            SetHostile(GameManager.Instance != null && GameManager.Instance.Hostile);
        }

        static bool CityHostile => GameManager.Instance != null && GameManager.Instance.Hostile;

        /// <summary>All enemies at once: guns out and into the fight, or guns away and back to strolling.</summary>
        public void SetHostile(bool hostile)
        {
            if (state == State.Dead) return;
            Rig.Holstered = !hostile;
            if (hostile)
            {
                alerted = true;
                var p = Player;
                lastKnown = p != null ? p.transform.position : transform.position;
                lastSeenTime = Time.time;
                if (state == State.Driving || state == State.ToCar) return;
                if (agent.enabled && agent.isOnNavMesh) EnterCombat();
            }
            else
            {
                alerted = false;
                seesPlayer = false;
                if (state == State.Driving) return;
                LeaveCover();
                Rig.Crouching = false;
                dodgeEnd = 0f;
                state = State.Patrol;
                car = null;
                PickPatrol();
            }
        }

        // ---- perception ----

        PlayerController Player => GameManager.Instance != null ? GameManager.Instance.Player : null;

        static Vector3 ChestOf(PlayerController p) => p.transform.position + Vector3.up * (p.Rig.Crouching ? 0.75f : 1.25f);

        Vector3 Eye => transform.position + Vector3.up * (Rig.Crouching ? 0.95f : 1.55f);

        bool HasLine(Vector3 from, Vector3 to) =>
            !Physics.Linecast(from, to, Layers.WorldMask, QueryTriggerInteraction.Ignore);

        /// <summary>Clear, or the only thing in the way is the car the player is sitting in.</summary>
        bool HasLineTo(PlayerController p, Vector3 from, Vector3 to)
        {
            if (!Physics.Linecast(from, to, out var hit, Layers.WorldMask, QueryTriggerInteraction.Ignore)) return true;
            return p.CurrentVehicle != null && hit.collider.GetComponentInParent<Vehicle>() == p.CurrentVehicle;
        }

        void Perceive(PlayerController p)
        {
            seesPlayer = false;
            if (p == null || p.Health.Dead || !CityHostile) return;
            Vector3 chest = ChestOf(p);
            Vector3 to = chest - Eye;
            float dist = to.magnitude;
            if (dist > SightRange) return;
            bool inFov = alerted || Vector3.Angle(transform.forward, to) < 75f || dist < 6f;
            if (inFov && (HasLineTo(p, Eye, chest) || HasLineTo(p, Eye, p.transform.position + Vector3.up * 1.6f)))
            {
                seesPlayer = true;
                lastKnown = p.transform.position;
                lastSeenTime = Time.time;
                if (!alerted) { alerted = true; GameManager.Instance?.ReportSighting(p.transform.position, this); }
            }
        }

        /// <summary>The player was heard or spotted by someone else.</summary>
        public void Alert(Vector3 where)
        {
            if (state == State.Dead || !CityHostile) return;
            lastKnown = where;
            lastSeenTime = Mathf.Max(lastSeenTime, Time.time - 3f);
            if (state == State.Driving || state == State.ToCar) { alerted = true; return; }
            if (!alerted) { alerted = true; EnterCombat(); }
        }

        void EnterCombat()
        {
            agent.speed = RunSpeed;
            if (!TryFindCover()) StartHunt();
        }

        // ---- threat reactions ----

        void OnBulletFired(Vector3 origin, Vector3 dir, Health shooter)
        {
            if (state == State.Dead || state == State.Driving || shooter == null || shooter == Health || !CityHostile) return;
            if (Player == null || shooter != Player.Health) return;
            Vector3 me = transform.position + Vector3.up * 1.2f;
            Vector3 toMe = me - origin;
            float along = Vector3.Dot(toMe, dir);
            if (along < 0f) return;
            float miss = (toMe - dir * along).magnitude;
            if (miss > 2.2f) return;
            // Heard one going by: this is what makes them move out of the line of fire.
            Alert(shooter.transform.position);
            float reaction = Random.Range(0.05f, 0.3f);
            TryDodge(dir, reaction);
        }

        void TryDodge(Vector3 incoming, float delay)
        {
            if (Time.time < nextDodge || state == State.Dead || state == State.Driving || !agent.enabled) return;
            if (state == State.InCover)
            {
                // Already behind something: get down rather than run.
                if (phase == CoverPhase.Exposed) EndExposure();
                nextDodge = Time.time + 0.8f;
                return;
            }
            Vector3 side = Vector3.Cross(Vector3.up, incoming).normalized;
            if (Random.value < 0.5f) side = -side;
            // Pick the side with room on the navmesh.
            if (NavMesh.Raycast(transform.position, transform.position + side * 3f, out _, NavMesh.AllAreas)) side = -side;
            if (NavMesh.Raycast(transform.position, transform.position + side * 3f, out _, NavMesh.AllAreas)) return;
            dodgeDir = side;
            dodgeEnd = Time.time + delay + Random.Range(0.45f, 0.8f);
            DodgeCount++;
            nextDodge = Time.time + Random.Range(1.1f, 2.4f) / Mathf.Sqrt(difficulty);
            if (Random.value < 0.35f) Rig.PlayRoll();
            else Rig.PlayDodge(Vector3.Dot(side, transform.right) > 0f ? 1 : -1);
        }

        void CheckCrosshairThreat(PlayerController p)
        {
            if (p == null || p.Cam == null || !seesPlayer) return;
            var ray = new Ray(p.Cam.transform.position, p.Cam.transform.forward);
            Vector3 me = transform.position + Vector3.up * 1.2f;
            float along = Vector3.Dot(me - ray.origin, ray.direction);
            if (along < 2f) return;
            float miss = Vector3.Cross(ray.direction, me - ray.origin).magnitude;
            if (miss < 0.9f && Random.value < 0.25f) TryDodge(ray.direction, Random.Range(0.1f, 0.35f));
        }

        void OnDamaged(HitZone zone, Vector3 point, Vector3 dir, Health shooter)
        {
            Rig.Flinch(dir);
            GameManager.Instance?.ReportEnemyAttacked(shooter);
            if (shooter != null) Alert(shooter.transform.position);
            nextDodge = Mathf.Min(nextDodge, Time.time + 0.2f);
            TryDodge(dir, 0f);
        }

        void OnDied(HitZone zone, Health shooter)
        {
            if (state == State.Driving)
            {
                ActiveDrivers = Mathf.Max(0, ActiveDrivers - 1);
                transform.SetParent(null, true);
                if (car != null)
                {
                    transform.position = car.ExitPosition(out int side);    // the body stays beside the car, not in it
                    if (!car.Exploded) car.Exit(side);
                }
                Rig.LeaveSeat();
                car = null;
            }
            state = State.Dead;
            LeaveCover();
            if (agent.enabled)
            {
                if (agent.isOnNavMesh) agent.isStopped = true;
                agent.enabled = false;
            }
            Rig.Velocity = Vector3.zero;
            Rig.Die(Health.LastDir);     // still crouched if it was: that has a fall of its own
            Rig.Crouching = false;
            foreach (var box in GetComponentsInChildren<Hitbox>()) box.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            var a = GameAudio.Instance;
            a?.PlayAt(a.Death, transform.position, 0.7f);
            GameManager.Instance?.ReportKill(this, zone, shooter);
            Fader.OutAndDestroy(gameObject, 8.1f);
        }

        // ---- cover ----

        bool TryFindCover()
        {
            var city = CityData.Instance; var p = Player;
            if (city == null || city.Cover == null || p == null) return false;
            Vector3 threat = lastKnown;
            Vector3 threatChest = threat + Vector3.up * 1.25f;
            float preferred = gun.Data.Kind == WeaponKind.Rifle ? 26f : 15f;
            Vector3 pFwd = p.transform.forward;

            float bestScore = float.MinValue;
            int best = -1;
            int tested = 0;
            int offset = Random.Range(0, city.Cover.Length);   // no bias to the start of the list
            for (int i = 0; i < city.Cover.Length && tested < 28; i++)
            {
                int idx = (i + offset) % city.Cover.Length;
                var c = city.Cover[idx];
                float dMe = Vector3.Distance(c.Position, transform.position);
                if (dMe > 45f) continue;
                float dPl = Vector3.Distance(c.Position, threat);
                if (dPl < 7f || dPl > 60f) continue;
                if (!city.IsFree(idx, Rig)) continue;
                tested++;
                // Must really block the line at crouch height.
                Vector3 low = c.Position + Vector3.up * 0.8f;
                if (HasLine(low, threatChest)) continue;
                bool canShoot = c.Low
                    ? HasLine(c.Position + Vector3.up * 1.5f, threatChest)
                    : PeekSide(c, threatChest, out _);
                float score = -dMe * 0.9f - Mathf.Abs(dPl - preferred) * 0.7f + (canShoot ? 12f : 0f);
                // Some enemies prefer to end up beside or behind where the player is looking.
                float view = Vector3.Dot((c.Position - threat).normalized, pFwd);
                score += (1f - view) * 7f * flankBias;
                if (idx == coverIndex) score -= 25f;
                if (score > bestScore) { bestScore = score; best = idx; }
            }
            if (best < 0) return false;
            LeaveCover();
            coverIndex = best;
            cover = city.Cover[best];
            city.Reserve(best, Rig);
            agent.speed = RunSpeed;
            agent.isStopped = false;
            agent.SetDestination(cover.Position);
            state = State.ToCover;
            Rig.Crouching = false;
            return true;
        }

        bool PeekSide(CoverPoint c, Vector3 threatChest, out Vector3 pos)
        {
            Vector3 side = Vector3.Cross(Vector3.up, c.Normal).normalized;
            for (int s = 0; s < 2; s++)
            {
                pos = c.Position + side * (s == 0 ? 1.2f : -1.2f);
                if (HasLine(pos + Vector3.up * 1.5f, threatChest) && NavMesh.SamplePosition(pos, out var h, 0.6f, NavMesh.AllAreas))
                {
                    pos = h.position;
                    return true;
                }
            }
            pos = c.Position;
            return false;
        }

        void LeaveCover()
        {
            if (coverIndex >= 0 && CityData.Instance != null) CityData.Instance.Release(coverIndex, Rig);
            coverIndex = -1;
        }

        void StartHunt()
        {
            state = State.Hunt;
            agent.speed = RunSpeed;
            agent.isStopped = false;
            Rig.Crouching = false;
            agent.SetDestination(lastKnown + Random.insideUnitSphere.Flat() * 6f);
        }

        void PickPatrol()
        {
            var city = CityData.Instance;
            if (city == null || city.PatrolPoints == null || city.PatrolPoints.Length == 0 || !agent.isOnNavMesh) return;
            agent.speed = WalkSpeed;
            agent.isStopped = false;
            agent.SetDestination(city.PatrolPoints[Random.Range(0, city.PatrolPoints.Length)]);
        }

        // ---- main loop ----

        void Update()
        {
            if (state == State.Driving) { DriveTick(Player); return; }
            if (state == State.Dead || gun == null || !agent.enabled || !agent.isOnNavMesh) { Rig.Velocity = Vector3.zero; return; }
            var p = Player;
            gun.Tick();

            if (state == State.ToCar) { WalkToCar(); }
            else ConsiderCar(p);

            if (Time.time >= thinkAt)
            {
                thinkAt = Time.time + Random.Range(0.14f, 0.26f);
                Perceive(p);
                if (seesPlayer && state == State.Patrol) EnterCombat();
                CheckCrosshairThreat(p);
                Decide(p);
            }

            // Dodge overlays whatever else we are doing.
            if (Time.time < dodgeEnd)
                agent.Move(dodgeDir * (RunSpeed * 1.1f * Time.deltaTime));

            if (tossing) ThrowTick();
            else
            {
                UpdateCoverPhase(p);
                Shooting(p);
            }
            Facing(p);

            Rig.Velocity = Time.time < dodgeEnd ? dodgeDir * RunSpeed : agent.velocity;
            float target = gun.Reloading ? Mathf.Sin(gun.ReloadProgress * Mathf.PI) : 0f;
            reloadWave = Mathf.MoveTowards(reloadWave, target, Time.deltaTime * 5f);
            Rig.ReloadWeight = reloadWave;
            // Runs with the gun carried; it only comes up to aim when there is something to shoot.
            bool running = agent.velocity.sqrMagnitude > 9f;
            bool shootingLately = Time.time - lastShot < 1.2f;
            Rig.AimWeightTarget = shootingLately || (alerted && seesPlayer && !running) || state == State.InCover ? 1f : 0f;
        }

        void Decide(PlayerController p)
        {
            bool playerDead = p == null || p.Health.Dead;
            switch (state)
            {
                case State.Patrol:
                    if (!agent.pathPending && agent.remainingDistance < 1.5f) PickPatrol();
                    break;

                case State.ToCover:
                    if (!agent.pathPending && agent.remainingDistance < 0.6f)
                    {
                        state = State.InCover;
                        CoverReachedCount++;
                        phase = CoverPhase.Hidden;
                        phaseEnd = Time.time + Random.Range(0.4f, 1.2f);
                        coverLeaveAt = Time.time + Random.Range(9f, 18f);
                        Rig.Crouching = true;
                    }
                    break;

                case State.InCover:
                    if (playerDead) break;
                    if (ConsiderGrenade(p)) break;
                    // Flanked: the player can see us even crouched.
                    if (phase == CoverPhase.Hidden && p != null &&
                        HasLine(transform.position + Vector3.up * 0.8f, ChestOf(p)))
                    {
                        lastKnown = p.transform.position;
                        if (!TryFindCover()) { nextDodge = 0f; StartHunt(); }
                        break;
                    }
                    if (Time.time > coverLeaveAt)
                    {
                        coverLeaveAt = Time.time + Random.Range(8f, 14f);
                        bool lost = Time.time - lastSeenTime > 7f;
                        if (lost) StartHunt();
                        else if (phase == CoverPhase.Hidden) TryFindCover();
                    }
                    break;

                case State.Hunt:
                    if (seesPlayer && Time.time - lastSeenTime < 0.5f)
                    {
                        if (TryFindCover()) break;
                    }
                    if (!agent.pathPending && agent.remainingDistance < 1.5f)
                    {
                        if (p != null && !playerDead)
                            lastKnown = Vector3.Lerp(lastKnown, p.transform.position, 0.6f);
                        if (!TryFindCover()) agent.SetDestination(lastKnown + Random.insideUnitSphere.Flat() * 8f);
                    }
                    break;
            }
        }

        void UpdateCoverPhase(PlayerController p)
        {
            if (state != State.InCover) return;
            if (phase == CoverPhase.Hidden)
            {
                if (gun.InMag <= 0) gun.StartReload();
                if (Time.time >= phaseEnd && !gun.Reloading)
                {
                    phase = CoverPhase.Exposed;
                    PeekCount++;
                    phaseEnd = Time.time + Random.Range(1.2f, 2.6f);
                    exposedSince = Time.time;
                    burstLeft = gun.Data.Automatic ? Random.Range(3, 8) : Random.Range(2, 5);
                    if (cover.Low) { Rig.Crouching = false; agent.isStopped = true; }
                    else
                    {
                        Rig.Crouching = false;
                        Vector3 chest = p != null ? ChestOf(p) : lastKnown + Vector3.up;
                        if (PeekSide(cover, chest, out peekPos)) { agent.isStopped = false; agent.speed = WalkSpeed * 1.6f; agent.SetDestination(peekPos); }
                    }
                }
            }
            else if (Time.time >= phaseEnd || gun.InMag <= 0)
            {
                EndExposure();
            }
        }

        void EndExposure()
        {
            phase = CoverPhase.Hidden;
            phaseEnd = Time.time + Random.Range(0.9f, 2.4f) / Mathf.Sqrt(difficulty);
            Rig.Crouching = true;
            agent.isStopped = false;
            agent.speed = RunSpeed;
            agent.SetDestination(cover.Position);
        }

        void Shooting(PlayerController p)
        {
            if (p == null || p.Health.Dead) return;
            bool allowed = false;
            switch (state)
            {
                case State.InCover: allowed = phase == CoverPhase.Exposed && Time.time - exposedSince > 0.3f && burstLeft > 0; break;
                case State.ToCover:
                case State.Hunt: allowed = Vector3.Distance(transform.position, p.transform.position) < 40f; break;
            }
            if (!allowed || !seesPlayer || !gun.CanFire) return;

            // Aim: chest, with an error that grows with distance and the player's speed, and shrinks while aiming steadily.
            Vector3 chest = ChestOf(p);
            float dist = Vector3.Distance(Eye, chest);
            float moving = Mathf.Clamp01(p.Rig.Velocity.magnitude / 6f);
            float self = state == State.InCover ? 1f : 1.7f;
            float err = (0.34f + dist * 0.018f) * accuracy * self * (1f + moving * 0.7f);
            Vector3 aim = chest + Random.insideUnitSphere * err + Vector3.down * 0.08f;
            // Half of all shots go wide on purpose: past the body and close enough to hear. The others still aim as above, so they can miss too.
            ShotsFired++;
            if (Random.value < MissChance)
            {
                ShotsWide++;
                Vector3 toEye = (chest - Eye).normalized;
                Vector3 across = Vector3.Cross(toEye, Random.onUnitSphere).normalized;
                if (across.y < -0.2f) across.y = -across.y;
                aim = chest + across * Random.Range(0.9f, 1.7f);
            }
            Rig.AimPoint = aim;

            var muzzle = Rig.Muzzle;
            if (muzzle == null) return;
            gun.ConsumeShot();
            lastShot = Time.time;
            // Pistols and rifles sound and spread differently but follow the same rule.
            Vector3 dir = (aim - muzzle.position).normalized;
            dir = Quaternion.AngleAxis(Random.Range(0f, gun.Data.SpreadDegrees), Vector3.Cross(dir, Random.onUnitSphere)) * dir;
            float bulletSpeed = gun.Data.BulletSpeed * 0.42f;  // enemy shots stay dodgeable at range
            BulletSystem.Instance.Fire(muzzle.position, dir, bulletSpeed, Rig.Team, Health);
            FxPool.Instance?.MuzzleFlash(muzzle.position, muzzle.forward);
            GameAudio.Instance?.PlayAt(gun.Data.ShotClips, muzzle.position, 0.8f, 18f);
            Rig.Recoil(gun.Data.RecoilDegrees * 0.6f);
            if (state == State.InCover)
            {
                burstLeft--;
                if (burstLeft <= 0) phaseEnd = Mathf.Min(phaseEnd, Time.time + 0.2f);
            }
            if (gun.Data.Automatic) gun.NextShotTime += Random.Range(0.02f, 0.08f);
            else gun.NextShotTime += Random.Range(0.1f, 0.45f);
        }

        // ---- cars ----

        /// <summary>Automated test: nobody takes a car for now.</summary>
        public static bool NoNewDrivers;
        /// <summary>Enemies fire this many times slower than the player would with the same gun.</summary>
        public const float FireRateSlowdown = 2f;
        public WeaponKind GunKind => gun != null ? gun.Data.Kind : WeaponKind.Rifle;
        public float SecondsPerShot => gun != null ? gun.Data.SecondsPerShot * gun.CadenceScale : 0f;
        public float PlayerSecondsPerShot => gun != null ? gun.Data.SecondsPerShot : 0f;
        /// <summary>The chance that a shot goes wide whatever the aim.</summary>
        public const float MissChance = 0.5f;
        public static int ShotsFired, ShotsWide;

        void ConsiderCar(PlayerController p)
        {
            if (NoNewDrivers || Time.time < nextCarCheck || ActiveDrivers >= (CityHostile ? 3 : 2)) return;
            nextCarCheck = Time.time + Random.Range(4f, 8f);
            if (state != State.Patrol && state != State.Hunt) return;
            Vector3 goal = alerted || state == State.Hunt ? lastKnown : (agent.hasPath ? agent.destination : transform.position);
            if (Vector3.Distance(transform.position, goal) < 55f) return;
            var v = Vehicle.Nearest(transform.position, 16f, true);
            if (v == null || v.OnFire) return;
            car = v;
            state = State.ToCar;
            agent.speed = RunSpeed;
            agent.isStopped = false;
            agent.SetDestination(v.ExitPosition());
        }

        void WalkToCar()
        {
            if (car == null || car.Exploded || car.HasDriver) { car = null; state = State.Hunt; StartHunt(); return; }
            if (Vector3.Distance(transform.position, car.transform.position) < 3.6f) EnterCar();
            else if (!agent.pathPending && agent.pathStatus != UnityEngine.AI.NavMeshPathStatus.PathComplete) { car = null; StartHunt(); }
        }

        /// <summary>Used by the spawner: this one starts at the wheel of a parked car.</summary>
        public void DriveImmediately(Vehicle v)
        {
            car = v;
            alerted = CityHostile;
            lastKnown = Player != null ? Player.transform.position : transform.position;
            spawning = true;
            EnterCar();
            spawning = false;
        }

        void EnterCar()
        {
            ActiveDrivers++;
            CarsEntered++;
            LeaveCover();
            state = State.Driving;
            if (agent.enabled) agent.enabled = false;
            if (ownCapsule != null) ownCapsule.enabled = false;
            transform.SetParent(car.transform, false);
            transform.SetLocalPositionAndRotation(car.SeatLocal, Quaternion.identity);
            car.Enter(Health, car.transform.InverseTransformPoint(transform.position).x < 0f ? 0 : 1, !spawning);
            Rig.EnterSeat(car);
            Rig.Velocity = Vector3.zero;
            route.Clear();
            routeLane = -1;
            drivingSince = Time.time;
            stuckTime = 0f;
        }

        bool spawning, leaving;

        /// <summary>The player wants this car: out, on that side.</summary>
        public void PulledOut(int side)
        {
            if (state != State.Driving || car == null || leaving) return;
            StartCoroutine(GetOut(side));
        }

        void ExitCar()
        {
            if (leaving || car == null) return;
            car.ExitPosition(out int side);
            StartCoroutine(GetOut(side));
        }

        /// <summary>Stops, opens the door, and then steps out through it, where everyone can see.</summary>
        System.Collections.IEnumerator GetOut(int side)
        {
            leaving = true;
            var c = car;
            float stopBy = Time.time + 1.4f;
            while (c != null && !c.Exploded && c.Speed > 2.5f && Time.time < stopBy && !c.OnFire && state == State.Driving)
            {
                c.SetInput(0f, 0f, true, false, false);
                yield return null;
            }
            if (state == State.Driving && car == c && c != null && !c.Exploded)
            {
                c.SetInput(0f, 0f, true, false, false);
                c.OpenDoor(side, 0.8f);
                yield return new WaitForSeconds(0.28f);
            }
            leaving = false;
            if (state == State.Driving && car == c && c != null) FinishExit(side);
        }

        void FinishExit(int side)
        {
            var c = car;
            car = null;
            ActiveDrivers = Mathf.Max(0, ActiveDrivers - 1);
            transform.SetParent(null, true);
            Vector3 pos = c.DoorSpot(side);
            if (!c.Exploded) c.Exit(side, false);
            Rig.LeaveSeat();
            if (ownCapsule != null) ownCapsule.enabled = true;
            transform.rotation = Quaternion.Euler(0, c.transform.eulerAngles.y, 0);
            if (NavMesh.SamplePosition(pos, out var h, 6f, NavMesh.AllAreas)) pos = h.position;
            transform.position = pos;
            agent.enabled = true;
            agent.Warp(pos);
            nextCarCheck = Time.time + 25f;
            if (CityHostile)
            {
                state = State.Hunt;
                alerted = true;
                EnterCombat();
            }
            else
            {
                state = State.Patrol;
                PickPatrol();
            }
        }

        RoadNetwork Roads => roads != null ? roads : roads = FindAnyObjectByType<RoadNetwork>();
        RoadNetwork roads;

        void ExtendRoute(Vector3 goal)
        {
            var net = Roads;
            if (net == null) return;
            if (routeLane < 0)
            {
                routeLane = net.GetNearestLane(transform.position, out int index);
                var pts = net.Lanes[routeLane].Points;
                for (int i = index; i < pts.Length; i++) route.Add(pts[i]);
                return;
            }
            // At the end of a lane: of the possible turns, usually the one that ends nearest to where we want to be.
            var next = net.Lanes[routeLane].NextConnectors;
            if (next.Length == 0) return;
            int best = next[0];
            float bestD = float.MaxValue;
            foreach (int c in next)
            {
                var pts = net.Lanes[net.Connectors[c].ToLane].Points;
                float d = Vector3.Distance(pts[^1], goal) + Random.Range(0f, 22f);
                if (d < bestD) { bestD = d; best = c; }
            }
            route.AddRange(net.Connectors[best].Points);
            routeLane = net.Connectors[best].ToLane;
            route.AddRange(net.Lanes[routeLane].Points);
        }

        void DriveTick(PlayerController p)
        {
            if (car == null || car.Exploded || leaving) { return; }
            gun?.Tick();
            Rig.Velocity = Vector3.zero;
            Vector3 pos = car.transform.position;
            Vector3 goal = p != null ? p.transform.position : lastKnown;
            float toPlayer = Vector3.Distance(pos, goal);
            float speed = car.Speed;

            // Abandon ship when burning; get out and fight once close, unless going for the ram.
            if (car.OnFire || car.CurrentHealth < 45f) { ExitCar(); return; }
            // Calm drivers just cruise; only hostile ones hunt the player.
            bool playerAlive = p != null && !p.Health.Dead && CityHostile;
            bool lineClear = playerAlive && toPlayer < 34f && !Physics.Linecast(pos + Vector3.up * 1.6f, goal + Vector3.up, out var block, Layers.WorldMask, QueryTriggerInteraction.Ignore);
            if (lineClear && p.CurrentVehicle == null && Time.time > nextRam) { ramUntil = Time.time + 2.8f; nextRam = Time.time + 9f; }
            bool ramming = Time.time < ramUntil && playerAlive;
            if (!ramming && playerAlive && toPlayer < 13f && Time.time - drivingSince > 5f && speed < 9f) { ExitCar(); return; }

            Vector3 target;
            if (ramming) target = goal;
            else
            {
                if (route.Count < 4) ExtendRoute(CityHostile ? goal : pos + car.transform.forward * 80f);
                while (route.Count > 1 && Vector3.Distance(pos, route[0]) < Mathf.Max(4.5f, speed * 0.45f)) route.RemoveAt(0);
                target = route.Count > 0 ? route[0] : goal;
            }

            Vector3 local = car.transform.InverseTransformPoint(target);
            float angle = Mathf.Atan2(local.x, Mathf.Max(0.01f, local.z)) * Mathf.Rad2Deg;
            if (local.z < 0f) angle = Mathf.Sign(local.x) * 90f;
            float steer = Mathf.Clamp(angle / 26f, -1f, 1f);
            float cruise = CityHostile ? 16f : 10f;
            float wantSpeed = ramming ? 22f : Mathf.Lerp(cruise, 6.5f, Mathf.Clamp01(Mathf.Abs(angle) / 45f));

            // Slow for whatever is ahead, unless it is the player.
            Vector3 nose = car.transform.TransformPoint(new Vector3(0, 0.8f, car.BodySize.z * 0.5f + 0.3f));
            if (!ramming && Physics.SphereCast(nose, 0.7f, car.transform.forward, out var ahead, 4f + speed * 0.7f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                wantSpeed = Mathf.Min(wantSpeed, Mathf.Max(2.5f, ahead.distance * 0.8f));

            // Traffic always slows at intersections and zebra crossings.
            if (!ramming && Roads != null && Roads.InSlowZone(pos, 15f + speed * 0.5f)) wantSpeed = Mathf.Min(wantSpeed, 5f);

            // A calm driver stops for anyone in the way; a hostile one only for its own side.
            bool personAhead = !ramming && PersonAhead(speed, p);

            float throttle = speed < wantSpeed ? 1f : (speed > wantSpeed + 2f ? -0.8f : 0f);

            // Stuck: back up a bit, then if that fails get out and walk.
            stuckTime = speed < 1f && !personAhead && Time.time - drivingSince > 2f ? stuckTime + Time.deltaTime : 0f;
            if (stuckTime > 2.2f && Time.time > reverseUntil)
            {
                reverseUntil = Time.time + 1.6f;
                stuckTime = 0f;
                stuckCount++;
                if (stuckCount >= 3) { ExitCar(); return; }
            }
            if (Time.time < reverseUntil) { throttle = -1f; steer = -steer; }

            bool honk = ramming || (personAhead && Random.value < 0.004f);
            if (personAhead) StoppedForPeople++;
            car.SetInput(personAhead ? 0f : throttle, steer, personAhead, false, honk);
        }

        int stuckCount;
        readonly Collider[] ahead = new Collider[12];
        public static int StoppedForPeople;

        /// <summary>Is someone in the box of road the car is about to cover, with room left to stop?</summary>
        bool PersonAhead(float speed, PlayerController p)
        {
            float length = 4f + speed * 1.1f;
            Vector3 center = car.transform.TransformPoint(new Vector3(0, 0.9f, car.BodySize.z * 0.5f + length * 0.5f));
            Vector3 half = new Vector3(car.BodySize.x * 0.5f + 0.35f, 0.9f, length * 0.5f);
            int n = Physics.OverlapBoxNonAlloc(center, half, ahead, car.transform.rotation, 1 << Layers.Character, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var h = ahead[i].GetComponentInParent<Health>();
                if (h == null || h.Dead || h == Health) continue;
                if (CityHostile && p != null && h == p.Health) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// From cover, hidden, when the player has been out of sight for a while within throwing range: lob a grenade at the last place
        /// they were seen. Each enemy waits long between throws, and only one throw at a time goes out across the city.
        /// </summary>
        bool ConsiderGrenade(PlayerController p)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.GrenadePrefab == null || phase != CoverPhase.Hidden || gun.Reloading || !alerted) return false;
            if (Time.time < nextGrenade || Time.time < nextGrenadeAny || p == null || p.CurrentVehicle != null) return false;
            float unseen = Time.time - lastSeenTime;
            float dist = Vector3.Distance(transform.position, lastKnown);
            if (unseen < 2.5f || unseen > 12f || dist < 8f || dist > 24f || Random.value > 0.12f) return false;
            float release = Rig.PlayThrow();
            if (release < 0f) return false;
            throwAt = lastKnown;
            releaseAt = Time.time + release;
            tossing = true;
            Rig.Crouching = false;
            agent.isStopped = true;
            nextGrenade = Time.time + Random.Range(18f, 32f);
            nextGrenadeAny = Time.time + 9f;
            return true;
        }

        void ThrowTick()
        {
            Rig.AimPoint = throwAt + Vector3.up;
            if (releaseAt > 0f && Time.time >= releaseAt)
            {
                releaseAt = -1f;
                var hand = Rig.Bone("RightHand");
                Vector3 from = hand != null ? hand.position : transform.position + Vector3.up * 1.7f;
                Grenade.Throw(GameManager.Instance.GrenadePrefab, from, throwAt + Random.insideUnitSphere.Flat() * 1.2f, Health);
                GrenadesThrown++;
            }
            if (!Rig.Throwing && releaseAt < 0f)
            {
                tossing = false;
                if (state == State.InCover) { Rig.Crouching = true; agent.isStopped = false; }    // back down behind the cover
            }
        }

        void Facing(PlayerController p)
        {
            Vector3 look;
            if (releaseAt > 0f)
            {
                look = throwAt - transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(look), Time.deltaTime * 400f);
                return;
            }
            if (alerted && (seesPlayer || Time.time - lastSeenTime < 4f))
            {
                look = (seesPlayer && p != null ? p.transform.position : lastKnown) - transform.position;
                if (!seesPlayer) Rig.AimPoint = lastKnown + Vector3.up * 1.3f;
                else if (Rig.Muzzle != null && p != null) Rig.AimPoint = Vector3.Lerp(Rig.AimPoint, ChestOf(p), Time.deltaTime * 8f);
            }
            else
            {
                look = agent.velocity.sqrMagnitude > 0.2f ? agent.velocity : transform.forward;
                Rig.AimPoint = transform.position + transform.forward * 10f + Vector3.up * 1.2f;
            }
            look.y = 0f;
            if (look.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(look), Time.deltaTime * 300f);
        }
    }

    public static class VectorExt
    {
        public static Vector3 Flat(this Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
