using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace GreatHeistAuto
{
    /// <summary>
    /// The state of the city. It starts calm: armed men walk around with their guns put away, among pedestrians. Attacking any enemy,
    /// by bullet, car or blast, turns all of them hostile. There are no waves: every enemy has a home spot, and a few seconds after
    /// dying a new one appears there. The player's death calms everything down again, and the player respawns at the starting point.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public PlayerController Player;
        public GameObject[] EnemyPrefabs;       // 3 models, tinted at spawn
        public GameObject GrenadePrefab;        // what enemies throw
        public WeaponData[] EnemyWeapons;
        public Color[] EnemyTints;
        public GameObject[] PedestrianPrefabs;  // 2 models, tinted vividly at spawn
        public Color[] PedestrianTints;
        public int EnemyCount = 44, PedestrianCount = 60;     // about 14 of the enemies are at the wheel
        public int TrafficDrivers = 14;
        public float EnemyRespawnMin = 5f, EnemyRespawnMax = 8f;
        public float DriverChance = 0.1f;
        public float RespawnDelay = 3f, SpawnInvulnerable = 3f;
        public bool Hold;   // no spawning (the automated test's photo tour)

        public bool Hostile { get; private set; }
        public int Kills { get; private set; }
        public int Headshots { get; private set; }
        public int Deaths { get; private set; }
        public IReadOnlyList<EnemyAI> Alive => alive;
        public IReadOnlyList<PedestrianAI> Pedestrians => peds;

        /// <summary>The city's mood changed.</summary>
        public event Action<bool> HostilityChanged;
        public event Action<EnemyAI, HitZone> EnemyKilled;
        public event Action PlayerDied, PlayerRespawned;

        class Slot
        {
            public Vector3 Home;
            public float Yaw;
            public int Model, Weapon, Tint;
            public EnemyAI Current;
            public float RespawnAt = -1f;
        }

        readonly List<Slot> slots = new();
        readonly List<EnemyAI> alive = new();
        readonly List<PedestrianAI> peds = new();
        float nextPed, nextRecycle, nextTraffic;
        CityData city;

        void Awake()
        {
            Instance = this;
            for (int i = 0; i < 32; i++)
            {
                Physics.IgnoreLayerCollision(Layers.Hitbox, i, true);
                Physics.IgnoreLayerCollision(Layers.Debris, i, i != Layers.World);
            }
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Start()
        {
            city = CityData.Instance;
            Player.Health.Died += OnPlayerDied;
            Player.Health.Damaged += (_, _, dir, _) => Player.Rig.Flinch(dir);
            BuildSlots();
        }

        /// <summary>Home spots spread over the city, none right next to where the player starts.</summary>
        void BuildSlots()
        {
            var spots = new List<Vector3>(city.EnemySpawns);
            for (int i = spots.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (spots[i], spots[j]) = (spots[j], spots[i]);
            }
            foreach (var s in spots)
            {
                if (slots.Count >= EnemyCount) break;
                if (Vector3.Distance(s, city.PlayerStart) < 45f) continue;
                if (!NavMesh.SamplePosition(s, out var hit, 6f, NavMesh.AllAreas)) continue;
                slots.Add(new Slot
                {
                    Home = hit.position,
                    Yaw = UnityEngine.Random.Range(0f, 360f),
                    Model = UnityEngine.Random.Range(0, EnemyPrefabs.Length),
                    Weapon = UnityEngine.Random.Range(0, EnemyWeapons.Length),
                    Tint = UnityEngine.Random.Range(0, EnemyTints.Length),
                    RespawnAt = 0f,
                });
            }
        }

        // ---- hostility ----

        /// <summary>Everyone is told: an enemy was attacked by the player.</summary>
        public void ReportEnemyAttacked(Health attacker)
        {
            if (attacker == null || attacker != Player.Health) return;
            MakeHostile();
        }

        public void MakeHostile()
        {
            if (Hostile) return;
            Hostile = true;
            foreach (var e in alive) if (e != null && !e.Health.Dead) e.SetHostile(true);
            HostilityChanged?.Invoke(true);
        }

        public void MakeCalm()
        {
            bool was = Hostile;
            Hostile = false;
            foreach (var e in alive) if (e != null && !e.Health.Dead) e.SetHostile(false);
            foreach (var p in peds) if (p != null && !p.Health.Dead) p.Calm();
            if (was) HostilityChanged?.Invoke(false);
        }

        // ---- spawning ----

        void Update()
        {
            alive.RemoveAll(e => e == null || e.Health.Dead);
            peds.RemoveAll(p => p == null || p.Health.Dead);
            if (Hold) return;

            foreach (var s in slots)
            {
                if (s.Current != null && !s.Current.Health.Dead) continue;
                if (s.RespawnAt < 0f) { s.RespawnAt = Time.time + UnityEngine.Random.Range(EnemyRespawnMin, EnemyRespawnMax); continue; }
                if (Time.time < s.RespawnAt) continue;
                // Never out of thin air right beside the player: it waits for them to move on.
                if (Vector3.Distance(s.Home, Player.transform.position) < 12f) continue;
                SpawnEnemy(s);
            }

            if (peds.Count < PedestrianCount && Time.time >= nextPed)
            {
                SpawnPedestrian();
                nextPed = Time.time + 0.15f;
            }
            if (Time.time >= nextTraffic)
            {
                nextTraffic = Time.time + 0.5f;
                TopUpTraffic();
            }
            if (Time.time >= nextRecycle)
            {
                // The city is big and the crowd is not: whoever is far away walks back in nearer the player.
                nextRecycle = Time.time + 1.5f;
                foreach (var p in peds)
                {
                    if (p == null || p.Hostile) continue;
                    if (Vector3.Distance(p.transform.position, Player.transform.position) > 190f)
                    {
                        Vector3 s = PickSpawn(50f, 110f);
                        if (NavMesh.SamplePosition(s, out var h, 6f, NavMesh.AllAreas)) p.Warp(h.position);
                    }
                }
            }
        }

        /// <summary>
        /// Keeps cars on the roads of a calm city: someone walking about out of sight takes the wheel of a parked car, out of sight too,
        /// at a distance from the player where the traffic will be met.
        /// </summary>
        void TopUpTraffic()
        {
            if (Hostile || EnemyAI.NoNewDrivers || EnemyAI.ActiveDrivers >= TrafficDrivers) return;
            Vector3 pp = Player.transform.position;
            Vector3 eye = pp + Vector3.up * 1.4f;
            bool OutOfSight(Vector3 at) => Physics.Linecast(at + Vector3.up * 1.5f, eye, Layers.WorldMask, QueryTriggerInteraction.Ignore);
            Vehicle car = null;
            float bestScore = float.MinValue;
            foreach (var v in Vehicle.All)
            {
                if (v == null || v.HasDriver || v.OnFire || v.Exploded) continue;
                float d = Vector3.Distance(v.transform.position, pp);
                if (d < 60f || d > 170f) continue;
                float score = UnityEngine.Random.value;
                if (score > bestScore && OutOfSight(v.transform.position)) { bestScore = score; car = v; }
            }
            if (car == null) return;
            EnemyAI driver = null;
            float nearest = float.MaxValue;
            foreach (var e in alive)
            {
                if (e == null || e.Health.Dead || e.StateName != "Patrol") continue;
                float d = Vector3.Distance(e.transform.position, car.transform.position);
                if (d < nearest && Vector3.Distance(e.transform.position, pp) > 40f && OutOfSight(e.transform.position)) { nearest = d; driver = e; }
            }
            if (driver != null) driver.DriveImmediately(car);
        }

        Vector3 PickSpawn(float nearest, float ideal)
        {
            // Out of sight, about the wanted distance from the player, with a little randomness.
            Vector3 pp = Player.transform.position;
            Vector3 best = city.EnemySpawns[0];
            float bestScore = float.MinValue;
            foreach (var s in city.EnemySpawns)
            {
                float d = Vector3.Distance(s, pp);
                if (d < nearest) continue;
                float score = -Mathf.Abs(d - ideal) + UnityEngine.Random.Range(0f, 35f);
                if (!Physics.Linecast(s + Vector3.up * 1.5f, pp + Vector3.up * 1.4f, Layers.WorldMask, QueryTriggerInteraction.Ignore)) score -= 200f;
                if (score > bestScore) { bestScore = score; best = s; }
            }
            return best;
        }

        void SpawnEnemy(Slot s)
        {
            var go = Instantiate(EnemyPrefabs[s.Model], s.Home, Quaternion.Euler(0, s.Yaw, 0));
            var e = go.GetComponent<EnemyAI>();
            e.Setup(EnemyWeapons[s.Weapon], EnemyTints[s.Tint], 1f);
            s.Current = e;
            s.RespawnAt = -1f;
            alive.Add(e);
            // A few set off in a parked car near their spot.
            if (EnemyAI.ActiveDrivers < TrafficDrivers && UnityEngine.Random.value < DriverChance)
            {
                var car = Vehicle.Nearest(s.Home, 45f, true);
                if (car != null && !car.OnFire && Vector3.Distance(car.transform.position, Player.transform.position) > 40f) e.DriveImmediately(car);
            }
        }

        /// <summary>Where this enemy's home spot is (read by the automated test).</summary>
        public Vector3 HomeOf(EnemyAI e)
        {
            foreach (var s in slots) if (s.Current == e) return s.Home;
            return Vector3.zero;
        }

        void SpawnPedestrian()
        {
            if (PedestrianPrefabs == null || PedestrianPrefabs.Length == 0) return;
            Vector3 at = PickSpawn(25f, 60f) + UnityEngine.Random.insideUnitSphere.Flat() * 8f;
            if (!NavMesh.SamplePosition(at, out var hit, 6f, NavMesh.AllAreas)) return;
            MakePedestrian(hit.position, UnityEngine.Random.Range(0, PedestrianPrefabs.Length));
        }

        public PedestrianAI MakePedestrian(Vector3 pos, int model)
        {
            var go = Instantiate(PedestrianPrefabs[model], pos, Quaternion.Euler(0, UnityEngine.Random.Range(0, 360f), 0));
            var p = go.GetComponent<PedestrianAI>();
            p.Setup(PedestrianTints[UnityEngine.Random.Range(0, PedestrianTints.Length)]);
            peds.Add(p);
            return p;
        }

        // ---- reports ----

        public void ReportKill(EnemyAI e, HitZone zone, Health shooter)
        {
            alive.Remove(e);
            if (shooter != Player.Health) return;
            Kills++;
            if (zone == HitZone.Head) Headshots++;
            EnemyKilled?.Invoke(e, zone);
        }

        public void ReportGunfire(Vector3 pos)
        {
            // When they are already fighting, gunfire brings in anyone within earshot; pedestrians run from it either way.
            if (Hostile)
                foreach (var e in alive)
                    if (e != null && Vector3.Distance(e.transform.position, pos) < 60f) e.Alert(pos);
            foreach (var p in peds)
                if (p != null && !p.Health.Dead && Vector3.Distance(p.transform.position, pos) < 32f) p.Scare(pos);
        }

        public void ReportSighting(Vector3 pos, EnemyAI who)
        {
            foreach (var e in alive)
                if (e != null && e != who && Vector3.Distance(e.transform.position, who.transform.position) < 45f) e.Alert(pos);
        }

        void OnPlayerDied(HitZone zone, Health shooter)
        {
            Deaths++;
            Player.Rig.Die(Player.Health.LastDir);
            var a = GameAudio.Instance;
            a?.PlayAt(a.Death, Player.transform.position, 1f);
            MakeCalm();
            PlayerDied?.Invoke();
            StartCoroutine(RespawnRoutine());
        }

        IEnumerator RespawnRoutine()
        {
            yield return new WaitForSeconds(RespawnDelay - 0.7f);
            Fader.Out(Player.gameObject, 0.7f, null);
            yield return new WaitForSeconds(0.7f);
            Player.Respawn(city.PlayerStart, Quaternion.Euler(0, city.PlayerStartYaw, 0));
            Fader.In(Player.gameObject);
            Player.Health.Invulnerable = true;
            PlayerRespawned?.Invoke();
            yield return new WaitForSeconds(SpawnInvulnerable);
            Player.Health.Invulnerable = false;
        }
    }
}
