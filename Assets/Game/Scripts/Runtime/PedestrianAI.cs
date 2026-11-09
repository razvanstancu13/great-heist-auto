using UnityEngine;
using UnityEngine.AI;

namespace GreatHeistAuto
{
    /// <summary>
    /// A person on the street. Strolls between points, stops now and then to look around or talk with the hands (facing anyone standing
    /// close), runs from gunfire nearby, and only turns on the player if the player attacks this one personally: then it chases and
    /// punches, having no weapon. Forgets it all when the player dies. Some stagger home drunk at night; one who was hurt limps from
    /// then on; a slow car knocks them over and they get back up.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class PedestrianAI : MonoBehaviour
    {
        public float PunchDamage = 10f;
        public float PunchRange = Melee.Reach;
        public float PunchEvery = 1.15f;
        public float FearDistance = 9f;

        public static int PunchesLanded;

        public bool Hostile { get; private set; }
        public bool Fleeing => Time.time < fleeUntil;
        public Health Health { get; private set; }
        public CharacterRig Rig { get; private set; }

        NavMeshAgent agent;
        float walkSpeed, fleeUntil, nextThink, nextPunch, punchLandsAt = -1f, staggerUntil;
        float standUntil, nextGesture, drunkRoll;
        bool hurt, wasDown;

        public static int Gestured, StoppedToStand;
        /// <summary>Standing still for a while (looking around, gesturing).</summary>
        public bool Standing => Time.time < standUntil;
        public CharacterRig.Gait Gait => Rig.Style;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.updateRotation = true;
            Health = GetComponent<Health>();
            Rig = GetComponentInChildren<CharacterRig>();
            Rig.Holstered = true;
            walkSpeed = Random.Range(1.15f, 1.75f);
            drunkRoll = Random.value;
            Health.Damaged += OnDamaged;
            Health.Died += OnDied;
        }

        public void Setup(Color tint)
        {
            Rig.SetTint(tint);
            agent.speed = walkSpeed;
            agent.angularSpeed = 300f;
            Wander();
        }

        PlayerController Player => GameManager.Instance != null ? GameManager.Instance.Player : null;

        public void Warp(Vector3 pos)
        {
            if (!agent.enabled) return;
            agent.Warp(pos);
            Wander();
        }

        void Wander()
        {
            var city = CityData.Instance;
            if (city == null || !agent.enabled || !agent.isOnNavMesh) return;
            // The nearest few points first, so people stroll along rather than march across town.
            Vector3 best = transform.position;
            float bestScore = float.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = city.PatrolPoints[Random.Range(0, city.PatrolPoints.Length)];
                float s = Vector3.Distance(p, transform.position);
                if (s > 6f && s < bestScore) { bestScore = s; best = p; }
            }
            standUntil = 0f;
            agent.isStopped = false;
            UpdateGait();
            agent.speed = CalmSpeed;
            agent.SetDestination(best + Random.insideUnitSphere.Flat() * 4f);
        }

        /// <summary>Drunk at night for about a quarter of the people; limping once hurt.</summary>
        void UpdateGait()
        {
            Rig.Style = hurt ? CharacterRig.Gait.Injured
                      : DayNightCycle.NightAmount > 0.5f && drunkRoll < 0.27f ? CharacterRig.Gait.Drunk
                      : CharacterRig.Gait.Normal;
        }

        float CalmSpeed => Rig.Style == CharacterRig.Gait.Normal ? walkSpeed : walkSpeed * 0.65f;

        /// <summary>Arrived somewhere: often stays a few seconds, looking around or gesturing, turned to anyone standing close by.</summary>
        void StopAWhile()
        {
            if (Random.value > 0.5f) { Wander(); return; }
            StoppedToStand++;
            standUntil = Time.time + Random.Range(3.5f, 9f);
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
            nextGesture = Time.time + Random.Range(0.4f, 1.5f);
        }

        void StandTick()
        {
            // Someone else standing close: turn to them, as if talking.
            PedestrianAI other = null;
            var gm = GameManager.Instance;
            if (gm != null)
                foreach (var p in gm.Pedestrians)
                    if (p != null && p != this && p.Standing && (p.transform.position - transform.position).sqrMagnitude < 16f) { other = p; break; }
            if (other != null)
            {
                Vector3 to = (other.transform.position - transform.position).Flat();
                if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to), Time.deltaTime * 200f);
            }
            if (Time.time >= nextGesture && Rig.Style == CharacterRig.Gait.Normal)
            {
                // In company mostly the talking ones, alone mostly looking around (the last gesture).
                int g = other != null ? Random.Range(0, Mathf.Max(1, Rig.GestureCount - 1)) : Random.value < 0.6f ? Rig.GestureCount - 1 : Random.Range(0, Rig.GestureCount);
                Rig.PlayGesture(g);
                Gestured++;
                nextGesture = Time.time + Random.Range(3f, 6f);
            }
            if (!Standing) Wander();
        }

        public void Scare(Vector3 from)
        {
            if (Hostile || !agent.enabled || !agent.isOnNavMesh) return;
            if (Time.time < fleeUntil - 3f) return;
            fleeUntil = Time.time + Random.Range(4f, 7f);
            standUntil = 0f;
            agent.isStopped = false;
            agent.speed = Random.Range(3.8f, 5f);
            Vector3 away = (transform.position - from).Flat().normalized;
            Vector3 goal = transform.position + away * 22f + Random.insideUnitSphere.Flat() * 6f;
            if (NavMesh.SamplePosition(goal, out var h, 12f, NavMesh.AllAreas)) agent.SetDestination(h.position);
        }

        public void Calm()
        {
            Hostile = false;
            punchLandsAt = -1f;
            fleeUntil = 0f;
            Wander();
        }

        void OnDamaged(HitZone zone, Vector3 point, Vector3 dir, Health source)
        {
            Rig.Flinch(dir);
            if (!Health.Dead && !hurt) { hurt = true; UpdateGait(); }
            standUntil = 0f;
            if (Health.LastCause == DamageCause.Melee) staggerUntil = Time.time + 0.5f;
            var p = Player;
            if (p != null && source == p.Health && !p.Health.Dead) Hostile = true;
        }

        void OnDied(HitZone zone, Health source)
        {
            Hostile = false;
            if (agent.enabled)
            {
                if (agent.isOnNavMesh) agent.isStopped = true;
                agent.enabled = false;
            }
            Rig.Velocity = Vector3.zero;
            Rig.Die(Health.LastDir);
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            var a = GameAudio.Instance;
            a?.PlayAt(a.Death, transform.position, 0.7f);
            Fader.OutAndDestroy(gameObject, 8.1f);
        }

        void Update()
        {
            if (Health.Dead || !agent.enabled) return;
            Rig.Velocity = agent.velocity;
            Rig.AimPoint = transform.position + transform.forward * 10f + Vector3.up * 1.3f;
            if (!agent.isOnNavMesh) return;

            // Knocked over: lie and get up, then carry on (or run, if there is still something to run from).
            if (Rig.Down)
            {
                if (!wasDown) { wasDown = true; agent.isStopped = true; agent.velocity = Vector3.zero; standUntil = 0f; }
                Rig.Velocity = Vector3.zero;
                return;
            }
            if (wasDown) { wasDown = false; agent.isStopped = false; if (!Hostile) Wander(); }

            if (Hostile) { Fight(); return; }
            if (Standing) { StandTick(); return; }

            if (Time.time >= nextThink)
            {
                nextThink = Time.time + Random.Range(0.3f, 0.6f);
                // Someone walking up with a gun in hand: run.
                var pl = Player;
                if (pl != null && pl.Armed && pl.CurrentVehicle == null && !pl.Health.Dead
                    && (pl.transform.position - transform.position).sqrMagnitude < FearDistance * FearDistance
                    && !Physics.Linecast(transform.position + Vector3.up * 1.5f, pl.transform.position + Vector3.up * 1.5f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                {
                    fleeUntil = 0f;     // keeps running for as long as the threat is near
                    Scare(pl.transform.position);
                }
                if (Time.time > fleeUntil) agent.speed = CalmSpeed;
                if (!agent.pathPending && agent.remainingDistance < 1.2f)
                {
                    if (Time.time > fleeUntil) StopAWhile();
                    else Wander();
                }
            }
        }

        void Fight()
        {
            var p = Player;
            if (p == null || p.Health.Dead) return;
            Vector3 to = (p.transform.position - transform.position).Flat();
            float d = to.magnitude;
            bool canReach = p.CurrentVehicle == null;

            // Just been hit: reels for a moment instead of walking straight back.
            if (Time.time < staggerUntil) { agent.isStopped = true; return; }

            // Up to arm's length, and no closer: nobody walks into the person they are hitting.
            bool closeEnough = d < Melee.StandOff + 0.04f;
            if (closeEnough != agent.isStopped) { agent.isStopped = closeEnough; if (closeEnough) agent.velocity = Vector3.zero; }
            if (!closeEnough && Time.time >= nextThink)
            {
                nextThink = Time.time + 0.2f;
                agent.speed = 5f;
                agent.SetDestination(p.transform.position);
            }

            if (d < PunchRange + 0.4f && to.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to), Time.deltaTime * 500f);

            if (canReach && d < PunchRange && Time.time >= nextPunch)
            {
                nextPunch = Time.time + PunchEvery;
                punchLandsAt = Time.time + CharacterRig.PunchLandsAfter;
                Melee.Square(gameObject, transform.position, p.transform.position);
                Rig.PlayPunch(p.Rig);
            }
            if (punchLandsAt > 0f && Time.time >= punchLandsAt)
            {
                punchLandsAt = -1f;
                if (d < PunchRange + 0.25f && canReach)
                {
                    PunchesLanded++;
                    Melee.Land(Health, p.Health, PunchDamage);
                }
            }
        }
    }
}
