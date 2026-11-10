using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GreatHeistAuto
{
    /// <summary>
    /// Automated playthrough, only active with the command line argument -ghatest (tools/verify_game.py passes it).
    /// Clicks Play on the menu, takes screenshots, plays with a bot that replaces the keyboard and mouse, and checks the rules:
    /// body shot = 25, head shot = kill, death respawns at the start, enemies reach cover, peek and dodge.
    /// Writes "[TEST] ..." lines to the player log, ending with "[TEST] RESULT OK" or "[TEST] RESULT FAIL ...".
    /// </summary>
    public class AutoTest : MonoBehaviour
    {
        string shotDir;
        float duration = 150f;
        int shots, bodyHits, headHits, ruleErrors;
        float fpsSum; int fpsFrames; float fpsMin = 9999f;
        Vector2 botMove, botLook;
        bool botFire, botPressed, botReload, botCrouch, botAds, botSprint, botInteract, botHonk, botOrbit, botBrake;
        int botCycle;
        int botSlot = -1;
        bool holdFire;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var args = System.Environment.GetCommandLineArgs();
            if (!args.Contains("-ghatest")) return;
            var go = new GameObject("AutoTest");
            DontDestroyOnLoad(go);
            var t = go.AddComponent<AutoTest>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-ghashots") t.shotDir = args[i + 1];
                if (args[i] == "-ghaseconds") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t.duration);
            }
            if (string.IsNullOrEmpty(t.shotDir)) t.shotDir = Path.Combine(Application.persistentDataPath, "ghashots");
            Directory.CreateDirectory(t.shotDir);
        }

        void RuleError(int which) { ruleErrors++; Log("ERROR rule check " + which + " failed"); }

        static void Log(string s) => Debug.Log("[TEST] " + s);

        IEnumerator Start()
        {
            Application.runInBackground = true;
            Application.logMessageReceived += OnLog;
            Log($"start {Screen.width}x{Screen.height} {SystemInfo.graphicsDeviceName}");
            yield return new WaitForSecondsRealtime(4f);
            yield return Shot("menu");
            var play = FindObjectsByType<Button>(FindObjectsInactive.Exclude).FirstOrDefault(b => b.name == "Play");
            var title = GameObject.Find("Title");
            Log($"menu play button={(play != null)} title={(title != null)}");
            if (play == null || title == null) { Fail("menu incomplete"); yield break; }
            play.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.25f);
            bool loadingShown = GameObject.Find("LoadingSpinner") != null;
            Log($"loading screen shown: {loadingShown}");
            if (!loadingShown) { Fail("no loading screen"); yield break; }
            yield return Shot("loading_screen");
            yield return new WaitUntil(() => SceneManager.GetActiveScene().name == "City" && GameManager.Instance != null && GameManager.Instance.Player != null && SegmentLoader.Done);
            yield return new WaitForSeconds(1.5f);
            yield return Run();
        }

        bool finished;

        void Fail(string why)
        {
            if (finished) return;
            finished = true;
            Log("RESULT FAIL " + why);
            Application.Quit(1);
        }

        void OnLog(string text, string stack, LogType type)
        {
            if (type == LogType.Exception) Fail("exception: " + text + " " + stack.Split('\n')[0]);
        }

        void Update()
        {
            if (Time.realtimeSinceStartup > duration + 480f) Fail($"not finished after {Time.realtimeSinceStartup:F0} s");
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, $"{shots++:00}_{name}.png"));
            yield return null;
        }

        PlayerCommand BotCommand()
        {
            var c = new PlayerCommand
            {
                Move = botMove, Look = botLook, Fire = botFire, FirePressed = botPressed, Ads = botAds, CrouchHeld = botCrouch,
                Reload = botReload, SelectSlot = botSlot, Sprint = botSprint, Jump = botJump, JumpHeld = botBrake, Interact = botInteract, Honk = botHonk, Orbit = botOrbit, CycleSlot = botCycle
            };
            botJump = false; botReload = false; botSlot = -1; botPressed = false; botInteract = false; botCycle = 0;
            return c;
        }

        IEnumerator Run()
        {
            var gm = GameManager.Instance;
            var player = gm.Player;
            var city = CityData.Instance;
            player.CommandOverride = BotCommand;
            gm.Hold = true;
            player.Health.Invulnerable = true;

            var roads = FindAnyObjectByType<RoadNetwork>();
            int all = roads.Lanes.Length, reach = roads.CountReachable(0);
            Log($"roads nodes={roads.Nodes.Length} lanes={all} connectors={roads.Connectors.Length} reachable={reach}");
            if (reach != all) RuleError(1);
            Log($"cover points={city.Cover.Length} spawns={city.EnemySpawns.Length} navmesh={NavMesh.CalculateTriangulation().indices.Length / 3} tris");

            // The rule, directly on the health component.
            var hp = player.GetComponent<Health>();
            hp.Invulnerable = false;
            hp.Restore();
            hp.TakeHit(HitZone.Body, player.transform.position, Vector3.forward, null);
            Log($"rule body hit: health {hp.Current} (want 75)");
            if (!Mathf.Approximately(hp.Current, 75f)) RuleError(2);
            hp.Restore();
            hp.Invulnerable = true;

            Log($"at start: armed={player.Armed}, holstered={player.Rig.Holstered}");
            if (player.Armed) RuleError(60);

            // Tour: every gun.
            yield return new WaitForSeconds(1f);
            yield return Shot("third_person");
            for (int w = 0; w < 4; w++)
            {
                botSlot = w; yield return null; yield return new WaitForSeconds(0.5f);
                yield return Shot($"third_person_gun{w}");
            }
            // Scrolling on from the last gun puts it away, as does 0, and scrolling on again takes the first.
            botSlot = 3; yield return null; yield return null;
            botCycle = 1; yield return null; yield return null;
            Log($"scroll past the last gun: armed={player.Armed}");
            if (player.Armed) RuleError(61);
            yield return new WaitForSeconds(0.3f);
            yield return Shot("unarmed_third_person");
            botCycle = 1; yield return null; yield return null;
            Log($"scroll on: gun slot {player.Slot}");
            if (player.Slot != 0) RuleError(62);
            botSlot = -2; yield return null; yield return null;
            if (player.Armed) RuleError(63);
            botSlot = 2; yield return null; yield return null;
            yield return Overviews(player);
            botSlot = 2;
            yield return Rules(player);
            yield return GunAnimations(player);
            yield return CarCloseUps(player);
            yield return TimeAndWeather(player);
            yield return Cars(player);
            yield return CarExtras(player);
            yield return Punches(player);
            yield return Peace(player);

            // Fight until the time is up.
            gm.EnemyKilled += (e, z) => Log($"kill zone={z} total={gm.Kills} hostile={gm.Hostile}");
            BulletSystem.BulletLanded += OnLanded;
            float end = Time.time + duration;
            float nextShot = Time.time + 8f, nextCheck = 0f;
            while (Time.time < end)
            {
                BotThink(player, gm);
                if (Time.time > nextShot) { nextShot = Time.time + 12f; yield return Shot("fight"); }
                if (Time.time > nextCheck)
                {
                    nextCheck = Time.time + 10f;
                    var states = string.Join(",", gm.Alive.Where(e => e != null).GroupBy(e => e.StateName).Select(g => $"{g.Key}:{g.Count()}"));
                    int floating = FxPool.Instance.CountFloating(out int decals);
                    floatingDecals = Mathf.Max(floatingDecals, floating); decalsSeen = Mathf.Max(decalsSeen, decals);
                    foreach (var body in StayGrounded.All) if (body.Gap > 0.25f) hangingBodies++;
                    int driving = 0, crawling = 0; float speeds = 0f;
                    foreach (var v in Vehicle.All)
                        if (v != null && v.HasDriver && v != player.CurrentVehicle) { driving++; speeds += v.Speed; if (v.Speed < 1f) crawling++; }
                    Log($"traffic: {driving} cars, average {speeds / Mathf.Max(1, driving) * 3.6f:F0} km/h, {crawling} standing");
                    Log($"t={Time.time:F0} fps={1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):F0} hp={player.Health.Current} kills={gm.Kills} states={states} cover={EnemyAI.CoverReachedCount} peeks={EnemyAI.PeekCount} dodges={EnemyAI.DodgeCount} deaths={gm.Deaths}");
                }
                float f = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
                fpsSum += f; fpsFrames++;
                if (Time.frameCount % 30 == 0) fpsMin = Mathf.Min(fpsMin, f);
                yield return null;
            }
            BulletSystem.BulletLanded -= OnLanded;

            // Death: a head shot kills, and the player comes back at the starting point.
            holdFire = true; botMove = Vector2.zero; botLook = Vector2.zero; botFire = false;
            gm.Hold = true;
            foreach (var e in gm.Alive) if (e != null) Destroy(e.gameObject);   // nobody else may kill the player during the checks
            gm.MakeCalm();
            if (player.Health.Dead) yield return new WaitUntil(() => !player.Health.Dead);
            yield return new WaitForSeconds(3.5f);   // respawn invulnerability
            float safety = Time.time + 8f;
            while (player.Health.Dead && Time.time < safety) yield return null;
            yield return new WaitForSeconds(gm.SpawnInvulnerable + 0.2f);
            yield return Shot("before_death");
            // Something left over (a burning car, a last bullet) may have got the player meanwhile: wait for the respawn.
            safety = Time.time + 10f;
            while (player.Health.Dead && Time.time < safety) yield return null;
            yield return new WaitForSeconds(0.1f);
            player.Health.Invulnerable = true;
            int deaths = gm.Deaths;
            player.Health.Invulnerable = false;
            player.Health.TakeHit(HitZone.Head, player.transform.position, Vector3.forward, null);
            Log($"rule head shot: dead={player.Health.Dead} deaths {deaths}->{gm.Deaths}");
            if (!player.Health.Dead || gm.Deaths != deaths + 1) RuleError(3);
            yield return new WaitForSeconds(1.2f);
            yield return Shot("dead");
            yield return new WaitForSeconds(gm.RespawnDelay + 0.5f);
            float dist = Vector3.Distance(player.transform.position.Flat(), city.PlayerStart.Flat());
            Log($"respawn: dead={player.Health.Dead} hp={player.Health.Current} distance to start={dist:F2} city hostile={gm.Hostile}");
            if (gm.Hostile) RuleError(47);
            if (player.Health.Dead || dist > 1.5f || player.Health.Current < 100f) RuleError(4);
            yield return Shot("respawned");

            // Four body shots kill.
            yield return new WaitForSeconds(gm.SpawnInvulnerable + 0.2f);
            int before = gm.Deaths;
            player.Health.Invulnerable = false;
            for (int i = 0; i < 4; i++) player.Health.TakeHit(HitZone.Body, player.transform.position, Vector3.forward, null);
            Log($"rule four body shots: dead={player.Health.Dead}");
            if (!player.Health.Dead || gm.Deaths != before + 1) RuleError(5);

            float avg = fpsSum / Mathf.Max(1, fpsFrames);
            Log($"summary kills={gm.Kills} headshots={gm.Headshots} bodyHits={bodyHits} headHits={headHits} playerDeaths={gm.Deaths} cover={EnemyAI.CoverReachedCount} peeks={EnemyAI.PeekCount} dodges={EnemyAI.DodgeCount} avgFps={avg:F0} minFps={fpsMin:F0}");

            Log($"enemy shots: {EnemyAI.ShotsFired}, sent wide {EnemyAI.ShotsWide}");
            Log($"fades finished: {Fader.Finished}, running {Fader.Active}");
            if (Fader.Finished == 0) problems0 = "no fade ran";
            Log($"decals: up to {decalsSeen} at once, at worst {floatingDecals} partly off their surface; {hangingBodies} sightings of a hanging body");
            var problems = new System.Collections.Generic.List<string>();
            if (problems0.Length > 0) problems.Add(problems0);
            if (floatingDecals > 0) problems.Add($"{floatingDecals} decals off their surface");
            if (hangingBodies > 3) problems.Add($"{hangingBodies} sightings of a hanging body");
            if (ruleErrors > 0) problems.Add($"{ruleErrors} rule errors");
            if (gm.Kills == 0) problems.Add("no kills");
            if (EnemyAI.CoverReachedCount == 0) problems.Add("no enemy reached cover");
            if (EnemyAI.PeekCount == 0) problems.Add("no enemy peeked");
            if (EnemyAI.DodgeCount == 0) problems.Add("no enemy dodged");
            if (avg < 25f) problems.Add($"low fps {avg:F0}");
            if (problems.Count > 0) Fail(string.Join("; ", problems));
            else { Log("RESULT OK"); Application.Quit(0); }
        }

        void OnLanded(Health victim, HitZone zone, Health shooter)
        {
            var player = GameManager.Instance.Player;
            if (shooter != player.Health) return;
            if (zone == HitZone.Head) { headHits++; if (!victim.Dead) { RuleError(6); Log("ERROR head hit did not kill"); } }
            else { bodyHits++; if (!Mathf.Approximately(victim.LastAmount, Health.BodyDamage)) { RuleError(7); Log("ERROR body hit did not take 25: " + victim.LastAmount); } }
        }

        void BotThink(PlayerController p, GameManager gm)
        {
            if (holdFire) return;
            if (!p.Armed) botSlot = 2;
            EnemyAI target = null; float best = 1e9f;
            Vector3 eye = p.Cam.transform.position;
            foreach (var e in gm.Alive)
            {
                if (e == null || e.Health.Dead) continue;
                float d = Vector3.Distance(p.transform.position, e.transform.position);
                bool see = !Physics.Linecast(eye, e.transform.position + Vector3.up * 1.2f, Layers.WorldMask, QueryTriggerInteraction.Ignore);
                if (see) d *= 0.3f;
                if (d < best) { best = d; target = e; }
            }
            botLook = Vector2.zero; botFire = false; botMove = Vector2.zero;
            if (target == null) return;
            Vector3 aimAt = target.transform.position + Vector3.up * ((Time.frameCount / 400) % 4 == 3 ? 1.62f : 1.15f);
            bool visible = !Physics.Linecast(eye, aimAt, Layers.WorldMask, QueryTriggerInteraction.Ignore);
            Vector3 dir = aimAt - eye;
            float wantYaw = Quaternion.LookRotation(dir).eulerAngles.y;
            float wantPitch = Mathf.DeltaAngle(0f, Quaternion.LookRotation(dir).eulerAngles.x);
            float maxStep = 260f * Time.deltaTime;
            botLook.x = Mathf.Clamp(Mathf.DeltaAngle(p.Yaw, wantYaw), -maxStep, maxStep);
            botLook.y = -Mathf.Clamp(wantPitch - p.Pitch, -maxStep, maxStep);
            if (visible && Mathf.Abs(Mathf.DeltaAngle(p.Yaw, wantYaw)) < 3f)
            {
                botFire = true;
                botPressed = Time.frameCount % 12 == 0;
                botAds = true;
            }
            else botAds = false;
            if (!visible || dir.magnitude > 30f)
            {
                var path = new NavMeshPath();
                if (NavMesh.SamplePosition(p.transform.position, out var a, 3f, NavMesh.AllAreas) &&
                    NavMesh.SamplePosition(target.transform.position, out var b, 3f, NavMesh.AllAreas) &&
                    NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) && path.corners.Length > 1)
                {
                    Vector3 to = (path.corners[1] - p.transform.position).Flat().normalized;
                    Vector3 local = Quaternion.Euler(0, -p.Yaw, 0) * to;
                    botMove = new Vector2(local.x, local.z);
                }
            }
            if (p.Armed && p.Gun.InMag == 0) botReload = true;
        }

        IEnumerator Overviews(PlayerController player)
        {
            // Look at the city from a free camera, for the screenshots only.
            var cam = player.Cam;
            var stops = new[]
            {
                (new Vector3(-222f, 2f, -224f), new Vector3(2, 40, 0), "street_corner"),
                (new Vector3(-36f, 3f, -216f), new Vector3(3, 0, 0), "road_view"),
                (new Vector3(-36f, 2f, -72f), new Vector3(5, 45, 0), "street_mid"),
                (new Vector3(-300f, 190f, -300f), new Vector3(26, 45, 0), "aerial"),
                (new Vector3(0f, 470f, 0f), new Vector3(89, 0, 0), "plan"),
            };
            player.enabled = false;
            foreach (var (pos, euler, name) in stops)
            {
                cam.transform.SetPositionAndRotation(pos, Quaternion.Euler(euler));
                yield return new WaitForSeconds(0.25f);
                yield return Shot(name);
            }
            player.enabled = true;
            yield return null;
            yield return Poses(player);
        }

        /// <summary>Aiming zooms and stops running; bullet holes go after a second; a pose of running with each kind of gun.</summary>
        IEnumerator Rules(PlayerController p)
        {
            Log($"fov base={p.BaseFov}");

            // Standing still, the middle button swings the camera around the player.
            float bodyYaw = p.Yaw;
            float camBefore = p.Cam.transform.eulerAngles.y;
            botOrbit = true;
            botLook = new Vector2(3f, 0.4f);
            for (int i = 0; i < 40; i++) yield return null;
            botLook = Vector2.zero;
            float swung = Mathf.Abs(Mathf.DeltaAngle(camBefore, p.Cam.transform.eulerAngles.y));
            Log($"orbit: camera swung {swung:F0} degrees, player turned {Mathf.Abs(Mathf.DeltaAngle(bodyYaw, p.Yaw)):F1}");
            if (swung < 30f || Mathf.Abs(Mathf.DeltaAngle(bodyYaw, p.Yaw)) > 0.5f) RuleError(64);
            yield return Shot("orbit_camera");
            botOrbit = false;
            yield return new WaitForSeconds(1f);
            float after = Mathf.Abs(Mathf.DeltaAngle(camBefore, p.Cam.transform.eulerAngles.y));
            Log($"orbit released: {after:F1} degrees from behind the player");
            if (after > 3f) RuleError(65);
            // Without Shift the player walks, on the walk clip; with Shift the legs run.
            botMove = Vector2.up;
            yield return new WaitForSeconds(0.8f);
            string walkClip = p.Rig.MainClip(); float walked = p.MoveSpeed;
            Log($"no shift: clip {walkClip} at {walked:F1} m/s");
            // The walk: plain, gun-out (eight-way, forward) or the limp when hurt.
            bool walkLegs = walkClip == "Walk" || walkClip == "ArmedWalkF" || walkClip == "InjuredWalk";
            if (!walkLegs || walked > p.WalkSpeed + 0.2f) RuleError(67);
            botSprint = true;
            yield return new WaitForSeconds(1f);
            float sprintSpeed = p.MoveSpeed; float sprintFov = p.Cam.fieldOfView;
            Log($"shift: clip {p.Rig.MainClip()} at {sprintSpeed:F1} m/s");
            if (p.Rig.MainClip() != "Sprint" || sprintSpeed < p.SprintSpeed - 0.5f) RuleError(68);
            botAds = true;
            yield return new WaitForSeconds(0.9f);
            Log($"aim: speed {sprintSpeed:F1} -> {p.MoveSpeed:F1}, fov {sprintFov:F0} -> {p.Cam.fieldOfView:F0}");
            if (p.MoveSpeed > p.AimSpeed + 0.3f) RuleError(20);
            if (p.Cam.fieldOfView > sprintFov - 15f) RuleError(21);
            yield return Shot("aim_third_person");
            botMove = Vector2.zero; botSprint = false; botAds = false;
            yield return new WaitForSeconds(0.5f);

            var fx = FxPool.Instance;
            // A hole needs a real surface: not the air, not a box that only roughly matches a hedge or a bench, not a car.
            int holes0 = CountActive("Hole");
            fx.Impact(p.transform.position + p.transform.forward * 2f + Vector3.up, -p.transform.forward);
            if (CountActive("Hole") != holes0) { Log("hole made in mid-air"); RuleError(73); }
            foreach (var rough in FindObjectsByType<BoxCollider>(FindObjectsSortMode.None))
                if (rough.name == "RoughBox")
                {
                    fx.Impact(new Vector3(rough.bounds.center.x, rough.bounds.max.y, rough.bounds.center.z), Vector3.up);
                    if (CountActive("Hole") != holes0) { Log("hole made on a rough box"); RuleError(74); }
                    break;
                }
            if (Surface.FindPaintable(p.transform.position + Vector3.up + p.transform.forward * 2f, Vector3.down, 3f, out var solid)) fx.Impact(solid.point, solid.normal);
            int holes = CountActive("Hole");
            yield return new WaitForSeconds(1.15f);
            Log($"bullet hole: {holes} -> {CountActive("Hole")} after 1.15 s");
            if (holes == 0 || CountActive("Hole") != 0) RuleError(22);

            // Running with a gun, seen from the side: the animation runs on the spot.
            Vector3 c = p.transform.position;
            foreach (int slot in new[] { 0, 2 })
            {
                botSlot = slot; yield return null; yield return new WaitForSeconds(0.3f);
                p.enabled = false;
                p.Rig.Velocity = p.transform.forward * p.SprintSpeed;
                p.Rig.AimWeightTarget = 0f;
                p.Cam.transform.position = c + p.transform.right * 2.6f + Vector3.up * 1.3f + p.transform.forward * .4f;
                p.Cam.transform.LookAt(c + Vector3.up * 1f);
                for (int i = 0; i < 3; i++)
                {
                    yield return new WaitForSeconds(0.45f + i * 0.13f);
                    yield return Shot($"run_gun{slot}_{i}");
                }
                p.Rig.Velocity = Vector3.zero;
                p.Rig.AimWeightTarget = 1f;
                p.enabled = true;
                yield return null;
            }
        }

        static int CountActive(string name)
        {
            int n = 0;
            foreach (Transform t in FxPool.Instance.transform) if (t.name == name && t.gameObject.activeSelf) n++;
            return n;
        }

        /// <summary>Each gun in each way of moving, seen from the front quarter, several frames each: the animation runs on the spot.</summary>
        IEnumerator GunAnimations(PlayerController p)
        {
            Vector3 c = p.transform.position;
            var rig = p.Rig;
            foreach (int slot in new[] { 0, 2 })
            {
                botSlot = slot; yield return null; yield return new WaitForSeconds(0.3f);
                p.enabled = false;
                var cam = p.Cam.transform;
                cam.position = c + p.transform.right * 2.3f + p.transform.forward * 2.0f + Vector3.up * 1.15f;
                cam.LookAt(c + Vector3.up * 0.95f);
                rig.AimPoint = c + p.transform.forward * 30f + Vector3.up * 1.3f;
                foreach (var (name, speed, crouch, aim) in new[] { ("crouch_idle", 0f, true, 1f), ("crouch_walk", 1.9f, true, 1f), ("aim_walk", 1.6f, false, 1f), ("jog", 3.6f, false, 1f), ("sprint", 6.6f, false, 0f) })
                {
                    rig.Crouching = crouch;
                    rig.Velocity = p.transform.forward * speed;
                    rig.AimWeightTarget = aim;
                    yield return new WaitForSeconds(0.7f);
                    for (int i = 0; i < 3; i++)
                    {
                        yield return Shot($"anim_gun{slot}_{name}_{i}");
                        yield return new WaitForSeconds(0.11f + i * 0.07f);
                    }
                }
                rig.Crouching = false; rig.Velocity = Vector3.zero; rig.AimWeightTarget = 1f;
                p.enabled = true;
                yield return null;
            }
        }

        /// <summary>Pictures of each car model close up: lamps on at night, wheels, and the steering from above.</summary>
        IEnumerator CarCloseUps(PlayerController p)
        {
            var cycle = FindAnyObjectByType<DayNightCycle>();
            cycle.Paused = true;
            var cam = p.Cam.transform;
            p.enabled = false;
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var v in Vehicle.All)
            {
                if (!seen.Add(v.BodyRenderers[0].GetComponent<MeshFilter>().sharedMesh.name)) continue;
                if (v.HasDriver || v.transform.position.y > 0.05f) { seen.Remove(v.BodyRenderers[0].GetComponent<MeshFilter>().sharedMesh.name); continue; }
                string id = "car" + seen.Count;
                v.SetDrivenVisuals(true);
                v.ForceLit = true;
                v.enabled = true;
                Transform t = v.transform;
                foreach (var (time, tag) in new[] { (0.25f, "day"), (0.75f, "night") })
                {
                    cycle.Time01 = time;
                    foreach (var (off, side) in new[] { (new Vector3(1.6f, 1.15f, 5.4f), "front"), (new Vector3(-1.6f, 1.15f, -5.4f), "rear"), (new Vector3(4.6f, 0.75f, 0.2f), "side") })
                    {
                        cam.position = t.TransformPoint(off);
                        cam.LookAt(t.position + Vector3.up * 0.65f);
                        yield return new WaitForSeconds(0.25f);
                        yield return Shot($"close_{id}_{tag}_{side}");
                    }
                }
                cycle.Time01 = 0.25f;
                v.SetPreviewSteer(1f);
                cam.position = t.TransformPoint(new Vector3(1.9f, 0.8f, v.BodySize.z * .5f + 1.6f));
                cam.LookAt(t.TransformPoint(new Vector3(0.7f, 0.3f, v.WheelLocal[1].z)));
                yield return new WaitForSeconds(0.25f);
                yield return Shot($"close_{id}_steer_right");
                v.SetPreviewSteer(0f);
                v.SetDrivenVisuals(false);
                v.ForceLit = false;
                v.enabled = false;
                if (seen.Count >= 5) break;     // every car model: sedan, SUV, Dacia 1310, Oltcit, Trabant
            }
            // Blood on the ground, by day and by night.
            Vector3 spot = p.transform.position + p.transform.forward * 9f;
            spot.y = 0.2f;
            if (Physics.Raycast(spot + Vector3.up, Vector3.down, out var g, 4f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                var fx = FxPool.Instance;
                fx.BloodPool(g.point);
                fx.BloodSplat(g.point + p.transform.right * 1.8f, g.normal, 1.4f);
                fx.BloodSplat(g.point - p.transform.right * 1.6f + p.transform.forward, g.normal, 1f);
                cycle.Time01 = 0.25f;
                cam.position = g.point + Vector3.up * 1.7f - p.transform.forward * 3.2f;
                cam.LookAt(g.point);
                yield return new WaitForSeconds(5.2f);
                yield return Shot("blood_day");
                cycle.Time01 = 0.75f;
                yield return new WaitForSeconds(0.4f);
                yield return Shot("blood_night");
            }
            cycle.Time01 = 0.2f;
            cycle.Paused = false;
            p.enabled = true;
            yield return null;
        }

        /// <summary>Every part of the day, then fog and rain, from street level.</summary>
        IEnumerator TimeAndWeather(PlayerController p)
        {
            var cycle = FindAnyObjectByType<DayNightCycle>();
            var weather = FindAnyObjectByType<WeatherSystem>();
            cycle.Paused = true;
            weather.Locked = true;
            p.enabled = false;
            Transform cam = p.Cam.transform;
            void Place(Vector3 pos, Vector3 euler) => cam.SetPositionAndRotation(pos, Quaternion.Euler(euler));
            Vector3 street = new(0.5f, 2.2f, -180f);
            foreach (var (t, name) in new[] { (0.03f, "sunrise"), (0.13f, "morning"), (0.25f, "noon"), (0.47f, "sunset"), (0.53f, "dusk"), (0.75f, "night") })
            {
                cycle.Time01 = t;
                Place(street, new Vector3(4f, 0f, 0f));
                yield return new WaitForSeconds(0.5f);
                yield return Shot("time_" + name);
            }
            // Look towards the sun from above the rooftops, with the sun off-centre, for the flare.
            cycle.Time01 = 0.13f;
            yield return null;
            Vector3 toSun = -cycle.Sun.transform.forward;
            cam.SetPositionAndRotation(new Vector3(0f, 130f, 0f), Quaternion.LookRotation(toSun + Vector3.down * 0.2f + Vector3.Cross(Vector3.up, toSun) * 0.25f));
            yield return new WaitForSeconds(0.6f);
            yield return Shot("sun_flare");

            // Night, close up on lamps, and from above.
            cycle.Time01 = 0.75f;
            Place(new Vector3(0f, 1.8f, -222f), new Vector3(2f, 12f, 0f));
            yield return new WaitForSeconds(0.8f);
            yield return Shot("night_street");
            Place(new Vector3(-300f, 190f, -300f), new Vector3(26f, 45f, 0f));
            yield return new WaitForSeconds(0.4f);
            yield return Shot("night_aerial");

            Log($"cycle: DaySeconds={cycle.DaySeconds} NightSeconds={cycle.NightSeconds} night amount now {DayNightCycle.NightAmount:F2}");
            if (DayNightCycle.NightAmount < 0.9f || !Mathf.Approximately(cycle.DaySeconds, 180f) || !Mathf.Approximately(cycle.NightSeconds, 180f)) RuleError(23);
            cycle.Time01 = 0.2f;
            yield return new WaitForSeconds(0.2f);
            if (DayNightCycle.NightAmount > 0.05f) RuleError(24);

            weather.ChangeSeconds = 1.5f;
            foreach (var (k, name) in new[] { (WeatherSystem.Kind.Fog, "fog"), (WeatherSystem.Kind.Rain, "rain") })
            {
                weather.Set(k);
                Place(street, new Vector3(4f, 0f, 0f));
                yield return new WaitForSeconds(4f);
                yield return Shot("weather_" + name);
                Log($"weather {name}: fog density {RenderSettings.fogDensity:F4}");
            }
            if (RenderSettings.fogDensity <= weather.ClearFog) RuleError(25);
            cycle.Time01 = 0.75f;
            yield return new WaitForSeconds(0.6f);
            yield return Shot("weather_rain_night");
            weather.Set(WeatherSystem.Kind.Clear);
            yield return new WaitForSeconds(3f);
            weather.ChangeSeconds = 12f;
            weather.Locked = false;
            cycle.Time01 = 0.2f;
            cycle.Paused = false;
            p.enabled = true;
            yield return null;
        }

        /// <summary>The city starts calm, a pedestrian only punches when attacked, attacking an enemy sets them all off, death calms everything.</summary>
        IEnumerator Peace(PlayerController p)
        {
            var gm = GameManager.Instance;
            var city = CityData.Instance;
            gm.MakeCalm();
            foreach (var e in gm.Alive) if (e != null) Destroy(e.gameObject);
            p.Respawn(city.PlayerStart, Quaternion.Euler(0, city.PlayerStartYaw, 0));
            p.Health.Invulnerable = false;

            // A pedestrian: peaceful until attacked, attacks alone, and does not set off the enemies.
            Vector3 near = p.transform.position + p.transform.forward * 5f;
            if (NavMesh.SamplePosition(near, out var nh, 5f, NavMesh.AllAreas)) near = nh.position;
            var ped = gm.MakePedestrian(near, 0);
            yield return new WaitForSeconds(0.8f);
            if (ped.Hostile || ped.Fleeing) RuleError(40);
            yield return Shot("pedestrian_walking");
            // Empty hands: they carry on. A gun in hand nearby: they run.
            botSlot = 0; yield return null;
            ped.Warp(near); Freeze(ped.gameObject);
            float fleeBy = Time.time + 3f;
            while (Time.time < fleeBy && !ped.Fleeing) yield return null;
            yield return new WaitForSeconds(1f);
            Log($"gun out near a pedestrian: fleeing={ped.Fleeing}, {Vector3.Distance(ped.transform.position, p.transform.position):F1} m away, running at {ped.Rig.Velocity.magnitude:F1} m/s");
            if (!ped.Fleeing || ped.Rig.Velocity.magnitude < 2f) RuleError(66);
            yield return Shot("pedestrian_fleeing");
            botSlot = -2; yield return null;
            ped.Health.TakeHit(HitZone.Body, ped.transform.position + Vector3.up * 1.2f, -p.transform.forward, p.Health);
            Log($"pedestrian attacked: hostile={ped.Hostile} city hostile={gm.Hostile}");
            if (!ped.Hostile || gm.Hostile) RuleError(41);
            float hpBefore = p.Health.Current;
            float until = Time.time + 12f;
            int punches = PedestrianAI.PunchesLanded;
            while (Time.time < until && PedestrianAI.PunchesLanded == punches) yield return null;
            Log($"punched: {PedestrianAI.PunchesLanded - punches > 0}, player health {hpBefore} -> {p.Health.Current}");
            if (PedestrianAI.PunchesLanded == punches || p.Health.Current >= hpBefore) RuleError(42);
            yield return Shot("pedestrian_punching");
            p.Health.Apply(Health.MaxHealth, HitZone.Body, p.transform.position, Vector3.forward, null, DamageCause.Bullet, Vector3.zero);
            yield return null;
            Log($"player died: pedestrian hostile={ped.Hostile}");
            if (ped.Hostile) RuleError(43);
            yield return new WaitForSeconds(gm.RespawnDelay + gm.SpawnInvulnerable + 0.3f);
            if (ped != null) Destroy(ped.gameObject);

            // Calm enemies: walking, weapons away, not interested, even when the player shoots near them.
            gm.Hold = false;
            float wait = Time.time + 12f;
            while (Time.time < wait && gm.Alive.Count < gm.EnemyCount - 3) yield return null;
            int armed = 0;
            foreach (var e in gm.Alive) if (e != null && !e.Rig.Holstered) armed++;
            Log($"calm: enemies={gm.Alive.Count} with guns out={armed} pedestrians={gm.Pedestrians.Count} hostile={gm.Hostile}");
            for (int look = 0; look < 3; look++)
            {
                yield return new WaitForSeconds(6f);
                int cars = 0, standing = 0; float speedSum = 0f;
                foreach (var v in Vehicle.All)
                    if (v != null && v.HasDriver && v != p.CurrentVehicle) { cars++; speedSum += v.Speed; if (v.Speed < 1f) standing++; }
                Log($"calm traffic: {cars} cars, average {speedSum / Mathf.Max(1, cars) * 3.6f:F0} km/h, {standing} standing");
            }
            if (gm.Alive.Count == 0 || armed > 0 || gm.Hostile) RuleError(44);
            if (gm.Pedestrians.Count == 0) RuleError(45);
            EnemyAI someone = null;
            foreach (var e in gm.Alive) if (e != null && e.StateName != "Driving") { someone = e; break; }
            if (someone != null)
            {
                p.enabled = false;
                p.Cam.transform.position = someone.transform.position + someone.transform.forward * 4.5f + Vector3.up * 1.5f;
                p.Cam.transform.LookAt(someone.transform.position + Vector3.up * 1.1f);
                yield return new WaitForSeconds(0.3f);
                yield return Shot("enemy_calm");
                p.enabled = true;
            }
            PedestrianAI crowd = gm.Pedestrians.Count > 0 ? gm.Pedestrians[0] : null;
            if (crowd != null)
            {
                p.enabled = false;
                p.Cam.transform.position = crowd.transform.position + crowd.transform.forward * 3.6f + Vector3.up * 1.5f;
                p.Cam.transform.LookAt(crowd.transform.position + Vector3.up * 1f);
                yield return new WaitForSeconds(0.3f);
                yield return Shot("pedestrian_close");
                p.enabled = true;
            }

            // A dead enemy comes back at its home spot after a few seconds.
            if (someone != null && someone.StateName != "Driving")
            {
                Vector3 home = gm.HomeOf(someone);
                var killed = someone;
                someone.Health.Apply(Health.MaxHealth, HitZone.Body, someone.transform.position, Vector3.forward, null, DamageCause.Bullet, Vector3.zero);
                yield return null;
                float t0 = Time.time;
                EnemyAI fresh = null;
                // This one's own return: others may come back in the meantime.
                while (Time.time - t0 < gm.EnemyRespawnMax + 4f && fresh == null)
                {
                    foreach (var e in gm.Alive) if (e != null && e != killed && !e.Health.Dead && gm.HomeOf(e) == home) fresh = e;
                    if (fresh == null) yield return null;
                }
                float homeDist = fresh != null ? Vector3.Distance(fresh.transform.position, home) : -1f;
                Log($"enemy respawn: back after {Time.time - t0:F1} s, {homeDist:F1} m from its home, {gm.Alive.Count} alive");
                if (fresh == null || Time.time - t0 < gm.EnemyRespawnMin - 0.5f) RuleError(53);
                foreach (var e in gm.Alive) if (e != null && e.StateName != "Driving") { someone = e; break; }
            }
            Log($"population: enemies {gm.Alive.Count}/{gm.EnemyCount}, pedestrians {gm.Pedestrians.Count}/{gm.PedestrianCount}");

            // Hit one: they all turn.
            if (someone != null)
            {
                someone.Health.TakeHit(HitZone.Body, someone.transform.position + Vector3.up * 1.2f, Vector3.forward, p.Health);
                armed = 0;
                foreach (var e in gm.Alive) if (e != null && !e.Rig.Holstered) armed++;
                Log($"enemy attacked: hostile={gm.Hostile}, guns out {armed}/{gm.Alive.Count}");
                if (!gm.Hostile || armed != gm.Alive.Count) RuleError(46);
            }
            p.Health.Invulnerable = false;
        }

        static void Freeze(GameObject enemy)
        {
            var ag = enemy.GetComponent<NavMeshAgent>();
            if (ag != null && ag.isOnNavMesh) { ag.ResetPath(); ag.isStopped = true; }
        }

        /// <summary>Get in, drive in both views, honk, run someone over, burn a car and blow it up, watch an enemy drive.</summary>
        int floatingDecals, decalsSeen, hangingBodies;
        string problems0 = "";

        /// <summary>A free car in the clear northbound lane, with the player beside its left door.</summary>
        Vehicle SetUpCar(PlayerController p, float offLane)
        {
            Vehicle car = null;
            foreach (var v in Vehicle.All)
                if (!v.Exploded && !v.HasDriver && Vector3.Distance(v.transform.position, p.transform.position) > 5f && Vector3.Distance(v.transform.position, v.Home) < 0.01f) { car = v; break; }
            var roads = FindAnyObjectByType<RoadNetwork>();
            foreach (var lane in roads.Lanes)
                if (Mathf.Abs(lane.Points[0].x - 1.7f) < 0.5f && lane.Points[^1].z > lane.Points[0].z && Mathf.Abs(lane.Points[0].z + 138f) < 12f)
                    car.Teleport(lane.Points[0] + Vector3.up * 0.02f + Vector3.right * offLane, Quaternion.identity);
            return car;
        }

        IEnumerator CarExtras(PlayerController p)
        {
            var gm = GameManager.Instance;
            botMove = Vector2.zero; botBrake = false; botFire = false;
            if (p.CurrentVehicle != null) { p.LeaveVehicle(true); yield return null; }
            int rifles = 0, slowOk = 0, enemies = 0;
            foreach (var e in gm.Alive)
                if (e != null)
                {
                    enemies++;
                    if (e.GunKind != WeaponKind.Pistol) rifles++;
                    if (Mathf.Approximately(e.SecondsPerShot, e.PlayerSecondsPerShot * 2f)) slowOk++;
                }
            Log($"enemies now: {enemies}, with rifles {rifles}, firing at half rate {slowOk}");
            if (rifles > 0 || slowOk != enemies || enemies == 0) problems0 = "enemy guns are wrong";
            if (EnemyAI.ShotsFired > 40 && Mathf.Abs((float)EnemyAI.ShotsWide / EnemyAI.ShotsFired - EnemyAI.MissChance) > 0.15f) problems0 = "enemy miss share is off";
            // A quiet street: nobody hunting, and the wrecks of the tests before gone.
            gm.MakeCalm();
            p.Respawn(CityData.Instance.PlayerStart, Quaternion.Euler(0, CityData.Instance.PlayerStartYaw, 0));
            p.Health.Invulnerable = true;
            // Nobody else on the road, and the wrecks of the tests before gone.
            EnemyAI.NoNewDrivers = true;
            foreach (var e in gm.Alive) if (e != null && e.StateName == "Driving") e.PulledOut(0);
            yield return new WaitForSeconds(16f);
            gm.MakeCalm();
            foreach (var v in Vehicle.All) v.ParkAtHome();
            yield return new WaitForFixedUpdate();

            // Getting in: the door opens first, the driver is not drawn, and it closes behind.
            var car = SetUpCar(p, 0f);
            car.Protected = true;
            p.Respawn(car.transform.position + car.transform.TransformDirection(new Vector3(-2.4f, 0.2f, 0f)), car.transform.rotation);
            p.Health.Invulnerable = true;
            yield return new WaitForSeconds(0.3f);
            botInteract = true;
            yield return null;
            float opened = 0f, give = Time.time + 3f;
            while (p.CurrentVehicle == null && Time.time < give) { opened = Mathf.Max(opened, car.DoorOpenness(0)); yield return null; }
            yield return Shot("door_opening");
            yield return new WaitForSeconds(2f);
            Log($"in the car: ambience level {GameAudio.Instance.AmbienceLevel:F2}, car music volume {GameAudio.Instance.CarMusicVolume:F2} against {GameAudio.Instance.MusicVolume:F2} on foot");
            if (GameAudio.Instance.AmbienceLevel > 0.2f) RuleError(105);
            if (GameAudio.Instance.CarMusicVolume <= GameAudio.Instance.MusicVolume) RuleError(106);
            Log($"music in the car: {GameAudio.Instance.CurrentMusic}");
            if (p.CurrentVehicle == car && GameAudio.Instance.CurrentMusic != "car") RuleError(99);
            Log($"boarding: door opened to {opened:F2}, in the car={p.CurrentVehicle == car}, body drawn={p.Rig.BodyShown}");
            if (opened < 0.3f) RuleError(75);
            if (p.CurrentVehicle != car) { RuleError(76); yield break; }
            if (p.Rig.BodyShown) RuleError(77);
            give = Time.time + 2f;
            while (car.DoorMoving && Time.time < give) yield return null;
            Log($"door closed again: {car.DoorOpenness(0):F2}");
            if (car.DoorOpenness(0) > 0.01f) RuleError(78);
            yield return Shot("car_health_hud");

            // Pictures of the wheels and the doors as they are on the road.
            var cam = p.Cam.transform;
            p.enabled = false;
            var wheelSky = FindAnyObjectByType<WeatherSystem>();
            float wheelSkyChange = wheelSky.ChangeSeconds;
            wheelSky.ChangeSeconds = 0.5f; wheelSky.Set(WeatherSystem.Kind.Clear);
            yield return new WaitForSeconds(1.2f);
            // A flash on the camera: on a dark car the edges of the wheel arches cannot be judged.
            var flash = new GameObject("TestFlash").AddComponent<Light>();
            flash.type = LightType.Point; flash.range = 12f; flash.intensity = 25f;
            flash.transform.SetParent(cam, false);
            foreach (var (name, local, look) in new[]
            {
                ("wheels_left_front", new Vector3(-3.4f, 0.9f, 2.2f), new Vector3(-0.8f, 0.4f, 1.3f)),
                ("wheels_left_rear", new Vector3(-3.4f, 0.9f, -2.2f), new Vector3(-0.8f, 0.4f, -1.3f)),
                ("wheels_right_front", new Vector3(3.4f, 0.9f, 2.2f), new Vector3(0.8f, 0.4f, 1.3f)),
                ("wheels_right_rear", new Vector3(3.4f, 0.9f, -2.2f), new Vector3(0.8f, 0.4f, -1.3f)),
                ("wheels_side_left", new Vector3(-6f, 0.9f, 0f), Vector3.up * 0.7f),
                ("wheels_side_right", new Vector3(6f, 0.9f, 0f), Vector3.up * 0.7f),
                ("wheels_low_rear", new Vector3(0f, 0.5f, -6f), Vector3.up * 0.5f),
            })
            {
                cam.position = car.transform.TransformPoint(local);
                cam.LookAt(car.transform.TransformPoint(look));
                yield return null;
                yield return Shot(name);
            }
            Destroy(flash.gameObject);
            wheelSky.ChangeSeconds = wheelSkyChange; wheelSky.Locked = false;
            car.OpenDoor(0, 0.8f);
            car.OpenDoor(1, 0.8f);
            yield return new WaitForSeconds(0.32f);
            cam.position = car.transform.TransformPoint(new Vector3(-5f, 1.6f, 2.5f));
            cam.LookAt(car.transform.TransformPoint(new Vector3(0f, 0.8f, 0f)));
            yield return null;
            yield return Shot("doors_open_left");
            cam.position = car.transform.TransformPoint(new Vector3(5f, 1.6f, 2.5f));
            cam.LookAt(car.transform.TransformPoint(new Vector3(0f, 0.8f, 0f)));
            yield return null;
            yield return Shot("doors_open_right");
            p.enabled = true;
            yield return new WaitForSeconds(0.8f);

            // Flat out: the blur is on what goes past. Then Space alone stops it, and Space with steering slides.
            int screechBefore = Vehicle.Screeches;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                botMove = Vector2.up;
                yield return new WaitForSeconds(3.2f);
                if (car.Speed > 10f) break;
                Log($"run-up blocked at {car.Speed * 3.6f:F0} km/h, again");
                botMove = Vector2.zero;
                car.Teleport(SetUpLaneStart(), Quaternion.identity);
                yield return new WaitForSeconds(0.5f);
            }
            Log($"speed for the blur pictures: {car.Speed * 3.6f:F0} km/h; screeches while accelerating: {Vehicle.Screeches - screechBefore}");
            if (Vehicle.Screeches != screechBefore) RuleError(107);
            screechBefore = Vehicle.Screeches;
            float speed0 = car.Speed;
            Vector3 from = car.transform.position;
            botMove = Vector2.zero; botBrake = true;
            float t0 = Time.time;
            int marks0 = SkidMarks.Instance.Laid;
            give = Time.time + 6f;
            bool shot = false;
            while (car.Speed > 0.5f && Time.time < give)
            {
                if (!shot && Time.time - t0 > 0.7f) { shot = true; yield return Shot("braking"); }
                yield return null;
            }
            Log($"Space from {speed0 * 3.6f:F0} km/h: stopped in {Vector3.Distance(from, car.transform.position):F1} m, {Time.time - t0:F1} s, {SkidMarks.Instance.Laid - marks0} mark strips");
            if (car.Speed > 0.5f) RuleError(79);
            if (speed0 > 8f && SkidMarks.Instance.Laid - marks0 < 5) RuleError(80);
            Log($"screeches under braking: {Vehicle.Screeches - screechBefore}");
            if (speed0 > 8f && Vehicle.Screeches == screechBefore) RuleError(108);
            if (speed0 <= 8f) { Log("never got up to speed for the braking test"); RuleError(96); }
            botBrake = false;

            // A run of its own for the blur pictures: the street is only so long. In clear weather, in daylight: rain streaks and dark
            // would drown the sharpness being measured.
            var skies = FindAnyObjectByType<WeatherSystem>();
            float skiesChange = skies.ChangeSeconds;
            skies.ChangeSeconds = 0.5f;
            skies.Set(WeatherSystem.Kind.Clear);
            var clock = FindAnyObjectByType<DayNightCycle>();
            bool clockPaused = clock.Paused;
            float clockTime = clock.Time01;
            clock.Paused = true;
            clock.Time01 = 0.25f;
            botMove = Vector2.zero; botBrake = true;
            yield return new WaitForSeconds(1.5f);
            botBrake = false;
            car.Teleport(SetUpLaneStart(), Quaternion.identity);
            botMove = Vector2.up;
            yield return new WaitForSeconds(2.2f);
            for (int pair = 0; pair < 3; pair++)
            {
                yield return Shot($"blur_on{pair}");
                CinematicFx.ForceBlurOff = true;
                yield return new WaitForSeconds(0.4f);
                yield return Shot($"blur_off{pair}");
                CinematicFx.ForceBlurOff = false;
                yield return new WaitForSeconds(0.4f);
            }
            skies.ChangeSeconds = skiesChange;
            skies.Locked = false;
            clock.Time01 = clockTime;
            clock.Paused = clockPaused;

            float slipMost = 0f;
            bool drifted = false;
            // Three times, to both sides: it has to hold every time, not just once.
            for (int go = 0; go < 3; go++)
            {
                car.Teleport(SetUpLaneStart(), Quaternion.identity);
                botBrake = false;
                botMove = Vector2.up;
                yield return new WaitForSeconds(3f);
                float side = go == 1 ? -1f : 1f;
                botBrake = true; botMove = new Vector2(side, 0.6f);
                marks0 = SkidMarks.Instance.Laid;
                float end = Time.time + 1.3f;
                while (Time.time < end)
                {
                    var rb = car.GetComponent<Rigidbody>();
                    if (rb.linearVelocity.magnitude > 5f) slipMost = Mathf.Max(slipMost, Vector3.Angle(rb.linearVelocity, car.transform.forward));
                    drifted |= car.Drifting;
                    yield return null;
                }
                botBrake = true; botMove = Vector2.zero;
                yield return new WaitForSeconds(0.2f);
            }
            yield return Shot("drifting");
            Log($"drift: slip angle up to {slipMost:F0} degrees, drifting={drifted}, {SkidMarks.Instance.Laid - marks0} strips, upright {Vector3.Dot(car.transform.up, Vector3.up):F2}");
            if (slipMost < 12f || !drifted) RuleError(81);
            if (slipMost > 130f) { Log("the drift became a spin"); RuleError(95); }    // with real tyres a slide may pass sideways; only going round is a spin
            if (Vector3.Dot(car.transform.up, Vector3.up) < 0.8f) RuleError(82);
            botBrake = false; botMove = Vector2.zero;
            botBrake = true;
            yield return new WaitForSeconds(2f);
            botBrake = false;
            p.Cam.transform.position = car.transform.position + Vector3.up * 14f - car.transform.forward * 12f;
            yield return Shot("skid_marks_chase");
            int showing = SkidMarks.Instance.Showing;
            Log($"mark strips showing {showing}");
            if (showing == 0) RuleError(83);

            // Hitting someone makes a thump.
            int thumps = Vehicle.PeopleHit;
            car.Protected = false;
            gm.MakeCalm();    // the drift may have caught someone: this is about people under the wheels, not about being shot at
            car.Repair();
            float healthBefore = car.CurrentHealth;
            var ped = FindAnyObjectByType<PedestrianAI>();
            if (ped != null)
            {
                car.Teleport(SetUpLaneStart(), Quaternion.identity);
                botMove = Vector2.up;
                yield return new WaitForSeconds(2.2f);
                ped.Warp(car.transform.position + car.transform.forward * (car.Speed * 0.7f + 3f));
                give = Time.time + 2.5f;
                while (Vehicle.PeopleHit == thumps && Time.time < give) yield return null;
                Log($"ran someone over: thumps {thumps} -> {Vehicle.PeopleHit}");
                if (Vehicle.PeopleHit == thumps) RuleError(84);
                yield return new WaitForSeconds(0.5f);
                Log($"car health after running someone over: {healthBefore:F0} -> {car.CurrentHealth:F0}, last damage: {car.LastDamage}");
                if (car.CurrentHealth < healthBefore && car.LastDamage.Contains("a crash")) RuleError(109);
                botMove = Vector2.zero;
            }
            botBrake = true;
            yield return new WaitForSeconds(1.5f);
            botBrake = false;

            // Damage shows as smoke, more as it gets worse, before any fire.
            gm.MakeCalm();
            car.Repair();
            float smoke0 = car.SmokeAmount;
            car.TakeDamage(20f, car.transform.position, Vector3.up, null, true);
            float smoke1 = car.SmokeAmount;
            car.TakeDamage(25f, car.transform.position, Vector3.up, null, true);
            float smoke2 = car.SmokeAmount;
            Log($"car smoke: {smoke0:F2} -> {smoke1:F2} -> {smoke2:F2} at health {car.CurrentHealth:F0}, on fire={car.OnFire}");
            if (smoke1 <= smoke0 || smoke2 <= smoke1 || car.OnFire) RuleError(110);
            gm.MakeCalm();
            car.Protected = true;      // the picture is of the smoke: no stray bullet may set it alight meanwhile
            yield return new WaitForSeconds(2.5f);
            p.enabled = false;
            p.Cam.transform.position = car.transform.TransformPoint(new Vector3(-5f, 2.6f, 5f));
            p.Cam.transform.LookAt(car.transform.position + Vector3.up);
            yield return null;
            yield return Shot("car_smoking");
            p.enabled = true;
            car.Protected = false;

            // Getting out: the body is drawn again, the door has moved.
            botInteract = true;
            yield return null; yield return null;
            Log($"got out: body drawn={p.Rig.BodyShown}, door {Mathf.Max(car.DoorOpenness(0), car.DoorOpenness(1)):F2}");
            if (p.CurrentVehicle != null || !p.Rig.BodyShown) RuleError(85);

            // Thrown and landed: on its back in the death pose, never on its feet.
            if (ped != null)
            {
                yield return new WaitForSeconds(2.5f);
                if (ped == null || !ped.Health.Dead) { Log("the body was gone too soon"); }
                else
                {
                    float floorY = Surface.FindPaintable(ped.transform.position + Vector3.up * 2f, Vector3.down, 6f, out var flr) ? flr.point.y : ped.transform.position.y;
                    Log($"run-over body: hips {ped.Rig.HipsAbove(floorY):F2} m up, chest up {ped.Rig.ChestUp:F2}, state {ped.Rig.BodyState}");
                    if (ped.Rig.HipsAbove(floorY) > 0.45f || ped.Rig.ChestUp < 0.6f) RuleError(98);
                    p.enabled = false;
                    p.Cam.transform.position = ped.Rig.ChestPoint + new Vector3(2f, 1.6f, -2f);
                    p.Cam.transform.LookAt(ped.Rig.ChestPoint);
                    yield return null;
                    yield return Shot("corpse_landed");
                    p.enabled = true;
                }
            }

            // Pulling an enemy out of a car: they get out by the door and can be seen, and the player takes the seat.
            car.Protected = false;
            var taker = gm.Alive.FirstOrDefault(e => e != null && !e.Health.Dead && e.StateName != "Driving" && e.StateName != "ToCar");
            if (taker == null) { Log("no enemy free for the carjack test"); RuleError(97); }
            var jacked = SetUpCar(p, 0f);
            if (taker != null && jacked != null)
            {
                // The player goes to the door a moment after it starts off, still slow enough to be stopped.
                taker.DriveImmediately(jacked);
                p.Respawn(jacked.transform.position + jacked.transform.TransformDirection(new Vector3(-2.2f, 0.2f, 0.6f)), jacked.transform.rotation);
                p.Health.Invulnerable = true;
                yield return null; yield return null;
                Log($"beside a driven car: in reach={p.VehicleInReach == jacked}, its speed {jacked.Speed:F1}");
                botInteract = true;
                yield return null;
                give = Time.time + 4f;
                bool seen = false;
                while (p.CurrentVehicle != jacked && Time.time < give)
                {
                    var rig = taker.GetComponentInChildren<CharacterRig>();
                    if (rig.BodyShown && jacked.DriverHealth != taker.Health && !seen) { seen = true; yield return Shot("enemy_got_out"); }
                    yield return null;
                }
                Log($"carjack: enemy out and drawn={seen}, state {taker.StateName}, player in the car={p.CurrentVehicle == jacked}");
                if (!seen || p.CurrentVehicle != jacked) RuleError(86);
                if (p.CurrentVehicle != null) { p.LeaveVehicle(true); yield return null; }
            }

            // A body lying on a car comes down when the car is gone, and the wreck itself lies on the ground.
            Vehicle under = null;
            foreach (var v in Vehicle.All)
                if (!v.Exploded && !v.HasDriver && Vector3.Distance(v.transform.position, v.Home) < 0.01f && Vector3.Distance(v.transform.position, p.transform.position) > 40f) { under = v; break; }
            var corpse = FindObjectsByType<PedestrianAI>(FindObjectsSortMode.None).FirstOrDefault(o => !o.Health.Dead && Vector3.Distance(o.transform.position, p.transform.position) > 30f);
            if (under != null && corpse != null)
            {
                under.Explode(null);
                yield return new WaitForSeconds(4f);
                float wreckGap = 0f;
                var box = under.GetComponent<BoxCollider>();
                if (Surface.FindPaintable(under.transform.position + Vector3.up * 3f, Vector3.down, 12f, out var floor)) wreckGap = box.bounds.min.y - floor.point.y;
                Log($"wreck after 4 s: speed {under.Speed:F1}, gap under its box {wreckGap:F2} m");
                if (under.Speed > 0.5f || wreckGap > 0.6f) RuleError(70);
                corpse.Health.Apply(Health.MaxHealth, HitZone.Body, corpse.transform.position, Vector3.up, null, DamageCause.Melee, Vector3.zero);
                corpse.transform.position = under.GetComponent<BoxCollider>().bounds.center + Vector3.up * (under.GetComponent<BoxCollider>().bounds.extents.y + 0.3f);
                yield return new WaitForSeconds(0.8f);
                float onRoof = corpse.transform.position.y;
                yield return new WaitForSeconds(under.WreckSeconds - 4.8f - 0.5f);
                p.enabled = false;
                p.Cam.transform.position = under.transform.position + new Vector3(5f, 3f, -5f);
                p.Cam.transform.LookAt(under.transform.position + Vector3.up);
                Log($"wreck fading: fades running {Fader.Active}");
                if (Fader.Active == 0) RuleError(104);
                yield return Shot("wreck_fading");
                p.enabled = true;
                yield return new WaitForSeconds(1.5f);
                if (corpse == null) { Log("body removed before the check"); RuleError(71); }
                else
                {
                    float groundY = Surface.FindPaintable(corpse.transform.position + Vector3.up, Vector3.down, 6f, out var g2) ? g2.point.y : 0f;
                    Log($"body on a wreck: {onRoof - groundY:F2} m up on it, {corpse.transform.position.y - groundY:F2} m off the ground once it had gone");
                    if (onRoof - groundY < 0.3f || corpse.transform.position.y - groundY > 0.05f) RuleError(71);
                }
            }
            p.Health.Invulnerable = false;
            EnemyAI.NoNewDrivers = false;
        }

        Vector3 SetUpLaneStart()
        {
            foreach (var lane in FindAnyObjectByType<RoadNetwork>().Lanes)
                if (Mathf.Abs(lane.Points[0].x - 1.7f) < 0.5f && lane.Points[^1].z > lane.Points[0].z && Mathf.Abs(lane.Points[0].z + 138f) < 12f)
                    return lane.Points[0] + Vector3.up * 0.02f;
            return Vector3.up;
        }

        int shotsTaken;
        bool botJump;

        IEnumerator Punches(PlayerController p)
        {
            var gm = GameManager.Instance;
            botMove = Vector2.zero; botBrake = false;
            gm.MakeCalm();
            // Someone with open ground ahead: a push against a hedge moves nobody.
            var ped = FindObjectsByType<PedestrianAI>(FindObjectsSortMode.None).FirstOrDefault(o => !o.Health.Dead
                && !UnityEngine.AI.NavMesh.Raycast(o.transform.position - Vector3.forward * 1.2f, o.transform.position + Vector3.forward * 2.5f, out _, UnityEngine.AI.NavMesh.AllAreas));
            if (ped == null) { RuleError(87); yield break; }
            p.Respawn(ped.transform.position - Vector3.forward * 0.95f, Quaternion.identity);
            p.Health.Invulnerable = true;
            yield return new WaitForSeconds(0.1f);
            if (p.Armed) { botSlot = -2; yield return null; }
            float near = float.MaxValue;
            ped.Warp(p.transform.position + Vector3.forward * 0.95f);
            yield return null;
            Vector3 before = ped.transform.position;
            float hp = ped.Health.Current;
            botFire = true;
            yield return null;
            botFire = false;
            float end = Time.time + CharacterRig.PunchSeconds;
            bool shot = false;
            float pushed = 0f;
            while (Time.time < end + 0.3f)
            {
                pushed = Mathf.Max(pushed, (ped.transform.position - before).Flat().magnitude);
                if (p.Rig.Punching)
                {
                    Vector3 a = p.Rig.ChestPoint, b = ped.Rig.ChestPoint;
                    Vector3 fist = p.Rig.FistPoint;
                    if (fist != Vector3.zero) near = Mathf.Min(near, Vector3.Distance(fist, b));
                }
                if (!shot && ped.Health.Current < hp) { shot = true; yield return Shot("punch_contact"); }
                yield return null;
            }
            Log($"punch: fist came within {near:F2} m of the other's chest centre, their health {hp:F0} -> {ped.Health.Current:F0}, pushed back {pushed:F2} m, that one hostile={ped.Hostile}, city hostile={gm.Hostile}");
            if (ped.Health.Current >= hp) RuleError(88);
            if (near < 0.17f) RuleError(89);
            if (pushed < 0.3f) RuleError(90);
            if (!ped.Hostile || gm.Hostile) RuleError(91);

            // And it hits back: the player is pushed too, and nobody walks into anybody.
            p.Health.Invulnerable = false;
            int landed = PedestrianAI.PunchesLanded;
            Vector3 mine = p.transform.position;
            float closest = float.MaxValue, end2 = Time.time + 4f;
            while (PedestrianAI.PunchesLanded == landed && Time.time < end2)
            {
                closest = Mathf.Min(closest, (ped.transform.position - p.transform.position).Flat().magnitude);
                yield return null;
            }
            yield return new WaitForSeconds(0.3f);
            Log($"punched back: landed={PedestrianAI.PunchesLanded > landed}, player pushed {(p.transform.position - mine).Flat().magnitude:F2} m, closest they stood {closest:F2} m");
            if (PedestrianAI.PunchesLanded == landed) RuleError(92);
            if ((p.transform.position - mine).Flat().magnitude < 0.15f) RuleError(93);
            if (closest < 0.55f) RuleError(94);
            yield return Shot("punch_after");
            ped.Calm();
            p.Respawn(CityData.Instance.PlayerStart, Quaternion.Euler(0, CityData.Instance.PlayerStartYaw, 0));

            // Trees, close and in the distance.
            var treeRoot = GameObject.Find("Colliders");
            CapsuleCollider nearTree = null;
            if (treeRoot != null)
                foreach (var pole in treeRoot.GetComponentsInChildren<CapsuleCollider>())
                    if (pole.radius > 0.2f) { nearTree = pole; break; }
            if (nearTree != null)
            {
                var skies2 = FindAnyObjectByType<WeatherSystem>();
                skies2.ChangeSeconds = 0.5f; skies2.Set(WeatherSystem.Kind.Clear);
                var clock2 = FindAnyObjectByType<DayNightCycle>();
                float clockWas = clock2.Time01; bool pausedWas = clock2.Paused;
                clock2.Paused = true; clock2.Time01 = 0.3f;
                yield return new WaitForSeconds(1.2f);
                p.enabled = false;
                Vector3 tp = nearTree.transform.position;
                foreach (var (name, from, at) in new[]
                {
                    ("tree_close", new Vector3(7f, 2.2f, -7f), new Vector3(0f, 4.2f, 0f)),
                    ("tree_close_back", new Vector3(-7f, 2.2f, 7f), new Vector3(0f, 4.2f, 0f)),
                    ("tree_close_side", new Vector3(8f, 1.8f, 5f), new Vector3(0f, 4.6f, 0f)),
                    ("tree_under", new Vector3(2.2f, 1.6f, -2.2f), new Vector3(0f, 5.5f, 0f)),
                    ("tree_far", new Vector3(22f, 8f, -26f), new Vector3(0f, 3f, 0f)),
                })
                {
                    p.Cam.transform.position = tp + from;
                    p.Cam.transform.LookAt(tp + at);
                    yield return null; yield return null;
                    yield return Shot(name);
                }
                p.enabled = true;
                clock2.Time01 = clockWas; clock2.Paused = pausedWas; skies2.Locked = false;
            }

            // Blood on the yellow centre line and across a zebra crossing: it lies over the paint.
            var paintFx = FxPool.Instance;
            Vector3 onLine = Vector3.zero;
            if (Surface.FindPaintable(new Vector3(0f, 2f, p.transform.position.z + 12f), Vector3.down, 4f, out var lineHit)) onLine = lineHit.point;
            paintFx.BloodSplat(onLine, Vector3.up, 2.2f);
            paintFx.BloodPool(onLine + Vector3.forward * 1.5f);
            p.enabled = false;
            p.Cam.transform.position = onLine + new Vector3(0.2f, 3.5f, -3.5f);
            p.Cam.transform.LookAt(onLine);
            yield return new WaitForSeconds(5.2f);
            yield return Shot("blood_over_marking");
            p.enabled = true;

            // Music: calm in a calm city, the hard one once it turns hostile.
            yield return null;
            Log($"music on foot, calm: {GameAudio.Instance.CurrentMusic}");
            if (GameAudio.Instance.CurrentMusic != "calm") RuleError(100);
            gm.MakeHostile();
            yield return null;
            Log($"music, hostile: {GameAudio.Instance.CurrentMusic}");
            if (GameAudio.Instance.CurrentMusic != "hostile") RuleError(101);
            gm.MakeCalm();

            // A jump: rising from the first moment, falling from the top, landing on the ground.
            yield return new WaitForSeconds(0.4f);
            var clear = new System.Collections.Generic.List<string>();
            float y0 = p.transform.position.y, peak = y0;
            p.Cam.transform.SetParent(null);
            botJump = true;
            yield return null;
            string atStart = "", atFall = "", atLand = "";
            float t0 = Time.time;
            bool wentUp = false;
            int frame = 0;
            while (Time.time - t0 < 1.3f)
            {
                yield return null;
                float h = p.transform.position.y - y0;
                peak = Mathf.Max(peak, p.transform.position.y);
                string state = p.Rig.BodyState;
                if (Time.time - t0 < 0.06f && atStart.Length == 0 && state != "Locomotion") atStart = state;
                if (h > 0.05f) wentUp = true;
                if (p.VerticalSpeed < -1.5f && h > 0.05f) atFall = state;
                if (wentUp && h < 0.01f && atLand.Length == 0) atLand = p.Rig.BodyState;
                if (frame++ % 5 == 0) clear.Add($"{Time.time - t0:F2}s {h:F2}m {state}");
                if (Time.time - t0 > 0.02f + shotsTaken * 0.1f && shotsTaken < 8) { shotsTaken++; yield return Shot($"jump_{shotsTaken}"); }
            }
            Log($"jump: height {peak - y0:F2} m; early state {atStart}; falling state {atFall}; {string.Join(" | ", clear)}");
            if (atFall != "Air") RuleError(102);
            if (peak - y0 < 0.3f) RuleError(103);
            botSlot = 2;
            yield return null;
        }

        IEnumerator Cars(PlayerController p)
        {
            var gm = GameManager.Instance;
            var city = CityData.Instance;
            botAds = false; botFire = false;

            // A car placed in a lane heading north: lanes are kept clear, so nothing is in its way.
            Vehicle car = Vehicle.Nearest(new Vector3(0f, 0f, -144f), 500f, true);
            if (car == null) { RuleError(30); yield break; }
            var roads = FindAnyObjectByType<RoadNetwork>();
            Vector3 laneStart = Vector3.zero;
            foreach (var lane in roads.Lanes)
                if (Mathf.Abs(lane.Points[0].x - 1.7f) < 0.5f && lane.Points[^1].z > lane.Points[0].z && Mathf.Abs(lane.Points[0].z + 138f) < 12f) laneStart = lane.Points[0];
            if (laneStart == Vector3.zero) RuleError(30);
            car.transform.SetPositionAndRotation(laneStart + Vector3.up * 0.02f, Quaternion.identity);
            Physics.SyncTransforms();
            Log($"cars total={Vehicle.All.Count}, using one at {car.transform.position} facing {car.transform.forward}");

            p.Respawn(car.transform.position + car.transform.TransformDirection(new Vector3(-2.2f, 0.2f, 0f)), car.transform.rotation);
            yield return new WaitForSeconds(0.3f);
            Log($"in reach: {(p.VehicleInReach != null)}");
            yield return Shot("car_outside");
            botInteract = true;
            yield return null;
            float boardBy = Time.time + 3f;
            while (p.CurrentVehicle == null && Time.time < boardBy) yield return null;
            Log($"entered car: {(p.CurrentVehicle != null)}");
            if (p.CurrentVehicle != null)
            {
                car = p.CurrentVehicle;
                Vector3 start = car.transform.position;
                botMove = Vector2.up;
                botHonk = true;
                yield return new WaitForSeconds(0.8f);
                botHonk = false;
                yield return new WaitForSeconds(1.7f);
                yield return Shot("car_third_person");
                Log($"after 2.5 s throttle: speed {car.Speed * 3.6f:F0} km/h, moved {Vector3.Distance(car.transform.position, start):F0} m");
                if (car.Speed < 8f) RuleError(31);
                float yaw0 = car.transform.eulerAngles.y;
                botMove = new Vector2(1f, 1f);
                yield return new WaitForSeconds(0.5f);
                float yawTurned = Mathf.DeltaAngle(yaw0, car.transform.eulerAngles.y);
                Log($"steer right: car turned {yawTurned:F1} degrees (right is positive), wheel angle {car.SteerAngle:F1}");
                if (yawTurned <= 0f || car.SteerAngle <= 0f) RuleError(50);
                yield return Shot("car_turn");
                p.enabled = false;
                p.Cam.transform.position = car.transform.position + Vector3.up * 7f;
                p.Cam.transform.rotation = Quaternion.Euler(90f, car.transform.eulerAngles.y, 0f);
                yield return null;
                yield return Shot("car_steer_right_top");
                p.enabled = true;
                botMove = Vector2.up;
                yield return new WaitForSeconds(0.3f);
                botMove = new Vector2(0f, -1f);
                yield return new WaitForSeconds(2.6f);
                botMove = Vector2.zero;

                // Run someone over: back to the clear lane, an enemy in the car's path, and floor it.
                car.Teleport(laneStart + Vector3.up * 0.1f, Quaternion.identity);
                yield return new WaitForSeconds(0.3f);
                var go = Instantiate(gm.EnemyPrefabs[1], car.transform.position + car.transform.forward * 24f, Quaternion.identity);
                var victim = go.GetComponent<EnemyAI>();
                victim.Setup(gm.EnemyWeapons[0], gm.EnemyTints[0], 1f);
                victim.enabled = false;
                Freeze(go);
                botMove = Vector2.up;
                float t0 = Time.time;
                while (Time.time - t0 < 6f && !victim.Health.Dead) yield return null;
                Log($"run over: dead={victim.Health.Dead} cause={victim.Health.LastCause} car speed {car.Speed * 3.6f:F0} km/h");
                if (!victim.Health.Dead || victim.Health.LastCause != DamageCause.Vehicle) RuleError(32);
                yield return Shot("car_kill_1");
                yield return new WaitForSeconds(0.25f);
                yield return Shot("car_kill_2");
                botMove = new Vector2(0f, -0.6f);
                yield return new WaitForSeconds(1.6f);
                botMove = Vector2.zero;
                float wait = Time.time + 4f;
                while (Time.time < wait && Mathf.Abs(car.ForwardSpeed) > 3f) yield return null;
                botInteract = true;
                yield return null; yield return null;
                Log($"got out: in car={(p.CurrentVehicle != null)}");
                if (p.CurrentVehicle != null) RuleError(33);
            }
            else RuleError(34);

            // Burn and explode a car, with an enemy beside it and the player far enough away.
            // A car nobody has touched, standing in its own spot, well away from where the test has been driving.
            Vehicle target = null;
            foreach (var v in Vehicle.All)
                if (!v.Exploded && !v.HasDriver && v != car && v.transform.position.y < 0.05f && Vector3.Distance(v.transform.position, car.transform.position) > 40f && Vector3.Distance(v.transform.position, v.Home) < 0.01f) { target = v; break; }
            if (target == null) { RuleError(35); yield break; }
            Vector3 tp = target.transform.position;
            p.Respawn(tp + target.transform.TransformDirection(new Vector3(0f, 0.3f, -22f)), target.transform.rotation);
            yield return null;
            var go2 = Instantiate(gm.EnemyPrefabs[0], tp + target.transform.right * 3f, Quaternion.identity);
            var bystander = go2.GetComponent<EnemyAI>();
            bystander.Setup(gm.EnemyWeapons[0], gm.EnemyTints[1], 1f);
            bystander.enabled = false;
            Freeze(go2);
            int shots = 0;
            while (!target.OnFire && shots < 30) { target.TakeDamage(BulletSystem.CarDamagePerBullet, tp + Vector3.up, Vector3.up, p.Health); shots++; }
            Log($"car on fire after {shots} bullets, health {target.CurrentHealth:F0}");
            if (!target.OnFire) RuleError(35);
            yield return new WaitForSeconds(1.5f);
            yield return Shot("car_burning");
            float boomBy = Time.time + 16f;
            while (!target.Exploded && Time.time < boomBy) yield return null;
            Log($"exploded by itself: {target.Exploded}; bystander dead={bystander.Health.Dead} cause={bystander.Health.LastCause}; player hp {p.Health.Current}");
            if (!target.Exploded) RuleError(36);
            if (!bystander.Health.Dead || bystander.Health.LastCause != DamageCause.Explosion) RuleError(37);
            yield return new WaitForSeconds(0.12f);
            yield return Shot("car_explosion");
            yield return new WaitForSeconds(1.8f);
            yield return Shot("car_wreck");
            // The wreck goes, and a new car stands in its original spot.
            float back = Time.time + target.WreckSeconds + target.RespawnSeconds + 6f;
            bool vanished = false;
            while (Time.time < back && target.Exploded)
            {
                if (!target.BodyRenderers[0].enabled) vanished = true;
                yield return null;
            }
            float fromHome = Vector3.Distance(target.transform.position, target.Home);
            Log($"wreck vanished: {vanished}; car back: {!target.Exploded}, {fromHome:F2} m from its original spot, health {target.CurrentHealth:F0}");
            if (!vanished || target.Exploded || fromHome > 0.05f || target.CurrentHealth < target.MaxHealth) RuleError(51);

            // An enemy behind the wheel.
            Vehicle parked = null;
            foreach (var v in Vehicle.All)
                if (!v.Exploded && !v.HasDriver && v.transform.position.y < 0.05f && Vector3.Distance(v.transform.position, p.transform.position) > 60f) { parked = v; break; }
            if (parked != null)
            {
                var go3 = Instantiate(gm.EnemyPrefabs[2], parked.ExitPosition(), Quaternion.identity);
                var driver = go3.GetComponent<EnemyAI>();
                driver.Setup(gm.EnemyWeapons[0], gm.EnemyTints[2], 1f);
                // On a straight lane of its own, for a clear test.
                Vector3 lane2 = Vector3.zero;
                foreach (var lane in roads.Lanes)
                    if (Mathf.Abs(lane.Points[0].x - 1.7f) < 0.5f && lane.Points[^1].z > lane.Points[0].z && Mathf.Abs(lane.Points[0].z + 66f) < 12f) lane2 = lane.Points[0];
                parked.Teleport(lane2 + Vector3.up * 0.02f, Quaternion.identity);
                driver.DriveImmediately(parked);
                Vector3 from = parked.transform.position;
                // The lane is not its own any more: there is traffic. It has a while to get going.
                float setOff = Time.time;
                while (Time.time < setOff + 7f && Vector3.Distance(parked.transform.position, from) < 4f) yield return null;
                float moved = Vector3.Distance(parked.transform.position, from);
                Log($"enemy driver: state {driver.StateName}, moved {moved:F0} m in {Time.time - setOff:F1} s, cars entered {EnemyAI.CarsEntered}");
                if (moved < 4f) RuleError(38);
                // Someone steps out in front: the calm driver stops and nobody is run over.
                Vector3 inFront = parked.transform.position + Vector3.forward * (parked.Speed * 1.1f + 11f);
                var walker = gm.MakePedestrian(inFront, 1);
                Freeze(walker.gameObject);
                walker.enabled = false;
                walker.Warp(inFront); Freeze(walker.gameObject);
                float stopBy = Time.time + 7f;
                int stopsBefore = EnemyAI.StoppedForPeople;
                while (Time.time < stopBy && !(parked.Speed < 0.5f && EnemyAI.StoppedForPeople > stopsBefore)) yield return null;
                Log($"driver and person ahead: car speed {parked.Speed * 3.6f:F0} km/h, gap {Vector3.Distance(parked.transform.position, walker.transform.position):F1} m, person dead={walker.Health.Dead}, stopped for people {EnemyAI.StoppedForPeople} frames");
                if (walker.Health.Dead || parked.Speed > 1f) RuleError(52);
                yield return Shot("calm_car_stopped");
                Destroy(walker.gameObject);
                p.enabled = false;
                p.Cam.transform.position = parked.transform.position - parked.transform.forward * 8f + Vector3.up * 3f;
                p.Cam.transform.LookAt(parked.transform.position + Vector3.up);
                yield return null;
                yield return Shot("enemy_driving");
                p.enabled = true;
                if (driver != null) Destroy(driver.gameObject);
            }
            else RuleError(39);

            p.Respawn(city.PlayerStart, Quaternion.Euler(0, city.PlayerStartYaw, 0));
            yield return new WaitForSeconds(0.2f);
        }

        /// <summary>Close-up shots of the character and enemy poses, with free camera positions.</summary>
        IEnumerator Poses(PlayerController player)
        {
            var cam = player.Cam;
            var gm = GameManager.Instance;
            Vector3 p = player.transform.position;
            foreach (int slot in new[] { 0, 2, 3 })
            {
                botSlot = slot; yield return null; yield return new WaitForSeconds(0.4f);
                player.enabled = false;
                foreach (var (off, name) in new[] { (new Vector3(0.2f, 1.45f, 2.3f), "front"), (new Vector3(2.2f, 1.4f, 0.6f), "right"), (new Vector3(-2.2f, 1.4f, 0.6f), "left") })
                {
                    cam.transform.position = p + off;
                    cam.transform.LookAt(p + Vector3.up * 1.1f);
                    yield return new WaitForSeconds(0.15f);
                    yield return Shot($"pose_player_gun{slot}_{name}");
                }
                player.enabled = true;
                yield return null;
            }

            // The three enemy models, tinted, standing then crouching.
            player.enabled = false;
            Vector3 c = p + new Vector3(0, 0, 9f);
            var list = new System.Collections.Generic.List<EnemyAI>();
            for (int i = 0; i < gm.EnemyPrefabs.Length; i++)
            {
                var pos = c + new Vector3((i - 1) * 1.7f, 0, 0);
                if (NavMesh.SamplePosition(pos, out var h, 3f, NavMesh.AllAreas)) pos = h.position;
                var go = Instantiate(gm.EnemyPrefabs[i], pos, Quaternion.Euler(0, 180f, 0));
                var e = go.GetComponent<EnemyAI>();
                e.Setup(gm.EnemyWeapons[i * 2 % gm.EnemyWeapons.Length], gm.EnemyTints[i * 2 % gm.EnemyTints.Length], 1f);
                e.enabled = false;
                Freeze(go);
                e.Rig.Holstered = false;
                list.Add(e);
            }
            cam.transform.position = c + new Vector3(0, 1.5f, -4.2f);
            cam.transform.LookAt(c + Vector3.up * 1f);
            foreach (var e in list) e.Rig.AimPoint = cam.transform.position;
            yield return new WaitForSeconds(0.5f);
            yield return Shot("pose_enemies_standing");
            foreach (var e in list) e.Rig.Crouching = true;
            yield return new WaitForSeconds(0.7f);
            yield return Shot("pose_enemies_crouching");
            foreach (var e in list) Destroy(e.gameObject);
            player.enabled = true;
            yield return null;
        }
    }
}
