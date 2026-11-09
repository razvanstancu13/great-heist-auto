using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace GreatHeistAuto
{
    /// <summary>
    /// Records a played session to video, only with the command line argument -gharecord (tools/record_gameplay.py passes it).
    /// The game runs in real time. Picture and sound are taken from the game itself, so nothing else on the screen or in the room gets in, and the speakers stay
    /// silent meanwhile. A frame the machine was too slow for repeats the one before, and the count of those is logged.
    /// A bot plays: walking, a fist fight that sets the city off, a gun fight, a stolen car, and the end of that car.
    /// Writes "[REC] ..." lines to the player log, ending with "[REC] DONE".
    /// </summary>
    public class GameplayRecorder : MonoBehaviour
    {
        string videoPath, audioPath, ffmpegPath;
        int fps = 60, totalFrames, framesTaken, framesWritten;
        float seconds = 120f;

        Process ffmpeg;
        Thread writer;
        readonly BlockingCollection<(byte[] bytes, int copies)> toWrite = new(16);
        readonly ConcurrentBag<byte[]> spare = new();
        AudioTap tap;
        int repeated;
        RenderTexture grab;
        bool recording, writerFailed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var args = System.Environment.GetCommandLineArgs();
            if (!args.Contains("-gharecord")) return;
            var go = new GameObject("GameplayRecorder");
            DontDestroyOnLoad(go);
            var r = go.AddComponent<GameplayRecorder>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-ghavideo") r.videoPath = args[i + 1];
                if (args[i] == "-ghaaudio") r.audioPath = args[i + 1];
                if (args[i] == "-ghaffmpeg") r.ffmpegPath = args[i + 1];
                if (args[i] == "-ghaseconds") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r.seconds);
                if (args[i] == "-ghafps") int.TryParse(args[i + 1], out r.fps);
            }
        }

        static void Log(string s) => Debug.Log("[REC] " + s);

        void Quit(int code, string why)
        {
            Log(why);
            Application.Quit(code);
        }

        IEnumerator Start()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.logMessageReceived += (text, stack, type) =>
            {
                if (type == LogType.Exception) Log("exception: " + text + " " + stack.Split('\n')[0]);
            };
            yield return new WaitForSecondsRealtime(2f);
            var play = FindObjectsByType<Button>(FindObjectsInactive.Exclude).FirstOrDefault(b => b.name == "Play");
            if (play == null) { Quit(1, "FAIL no Play button"); yield break; }
            play.onClick.Invoke();
            yield return new WaitUntil(() => SceneManager.GetActiveScene().name == "City" && GameManager.Instance != null && GameManager.Instance.Player != null && SegmentLoader.Done);
            yield return new WaitForSecondsRealtime(1.5f);
            if (!BeginCapture()) { Quit(1, "FAIL could not start the encoder"); yield break; }
            gameObject.AddComponent<GameplayBot>().Seconds = seconds;
            StartCoroutine(CaptureLoop());
        }

        bool BeginCapture()
        {
            int w = Screen.width & ~1, h = Screen.height & ~1;
            grab = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            totalFrames = Mathf.RoundToInt(seconds * fps);
            Log($"start {w}x{h} at {fps} fps, {totalFrames} frames, audio {AudioSettings.outputSampleRate} Hz");
            try
            {
                ffmpeg = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s {w}x{h} -r {fps} -i pipe:0 -c:v libx264 -preset veryfast -crf 20 -maxrate 30M -bufsize 60M -pix_fmt yuv420p \"{videoPath}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                });
                var listener = FindAnyObjectByType<AudioListener>();
                if (listener == null) { Log("no audio listener"); return false; }
                tap = listener.gameObject.AddComponent<AudioTap>();
                tap.Begin(audioPath);
            }
            catch (System.Exception e)
            {
                Log("encoder: " + e.Message);
                return false;
            }
            var stdin = ffmpeg.StandardInput.BaseStream;
            writer = new Thread(() =>
            {
                try
                {
                    foreach (var (frame, copies) in toWrite.GetConsumingEnumerable())
                    {
                        for (int i = 0; i < copies; i++)
                        {
                            stdin.Write(frame, 0, frame.Length);
                            Interlocked.Increment(ref framesWritten);
                        }
                        spare.Add(frame);
                    }
                    stdin.Close();
                }
                catch (System.Exception e)
                {
                    writerFailed = true;
                    Debug.Log("[REC] writing frames: " + e.Message);
                }
            }) { IsBackground = true };
            writer.Start();
            recording = true;
            return true;
        }

        IEnumerator CaptureLoop()
        {
            var end = new WaitForEndOfFrame();
            var clock = Stopwatch.StartNew();
            int inFlight = 0;
            while (framesTaken < totalFrames && !writerFailed)
            {
                yield return end;
                // How many video frames this picture stands for: none when the game runs ahead of the video, several when it fell behind.
                int due = Mathf.Min(totalFrames, (int)(clock.Elapsed.TotalSeconds * fps) + 1);
                int copies = due - framesTaken;
                if (copies <= 0) continue;
                repeated += copies - 1;
                ScreenCapture.CaptureScreenshotIntoRenderTexture(grab);
                inFlight++;
                AsyncGPUReadback.Request(grab, 0, TextureFormat.RGBA32, req =>
                {
                    inFlight--;
                    if (req.hasError) { Log("readback failed"); writerFailed = true; return; }
                    var data = req.GetData<byte>();
                    if (!spare.TryTake(out var bytes) || bytes.Length != data.Length) bytes = new byte[data.Length];
                    data.CopyTo(bytes);
                    toWrite.Add((bytes, copies));    // waits when the encoder is behind
                });
                if (inFlight > 4) AsyncGPUReadback.WaitAllRequests();
                int before = framesTaken / (fps * 10);
                framesTaken = due;
                if (framesTaken / (fps * 10) != before) Log($"{framesTaken}/{totalFrames} frames, {repeated} repeated so far");
            }
            AsyncGPUReadback.WaitAllRequests();
            tap.End();
            recording = false;
            toWrite.CompleteAdding();
            writer.Join();
            ffmpeg.WaitForExit();
            Log($"frames taken {framesTaken}, written {framesWritten}, repeated {repeated}, audio {tap.Seconds:F2} s {tap.Channels} channels, encoder exit {ffmpeg.ExitCode}");
            bool ok = !writerFailed && framesWritten == totalFrames && ffmpeg.ExitCode == 0;
            if (ok) Log("DONE");
            Quit(ok ? 0 : 1, ok ? "closing" : "FAIL capture incomplete");
        }

        void OnDestroy()
        {
            if (recording && tap != null) tap.End();
            if (grab != null) grab.Release();
        }
    }

    /// <summary>On the audio listener: the final mix of the game's sound goes to a file instead of the speakers.</summary>
    public class AudioTap : MonoBehaviour
    {
        FileStream file;
        BinaryWriter output;
        long values;
        int sampleRate;
        volatile bool open;
        public int Channels { get; private set; } = 2;
        public float Seconds => values / (float)Mathf.Max(1, Channels) / Mathf.Max(1, sampleRate);

        public void Begin(string path)
        {
            sampleRate = AudioSettings.outputSampleRate;
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            output = new BinaryWriter(file);
            open = true;
        }

        public void End()
        {
            if (!open) return;
            lock (this) { open = false; output.Flush(); file.Dispose(); }
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (open)
                lock (this)
                {
                    if (open)
                    {
                        Channels = channels;
                        for (int i = 0; i < data.Length; i++) output.Write(data[i]);
                        values += data.Length;
                    }
                }
            System.Array.Clear(data, 0, data.Length);
        }
    }

    /// <summary>The player of the recording: scenes in order, each with a share of the running time.</summary>
    public class GameplayBot : MonoBehaviour
    {
        public float Seconds = 120f;

        PlayerController p;
        GameManager gm;
        RoadNetwork roads;
        float t0;
        Vector2 move, look;
        bool fire, pressed, reload, ads, sprint, jump, interact, honk, brake;
        int slot = -1;
        float targetCarHealthFloor = 75f;    // bullets can set a car alight below 72: it smokes, but lasts the drive

        float Now => Time.time - t0;
        static void Log(string s) => Debug.Log("[REC] " + s);

        PlayerCommand Command()
        {
            var c = new PlayerCommand
            {
                Move = move, Look = look, Fire = fire, FirePressed = pressed, Ads = ads, Reload = reload, SelectSlot = slot,
                Sprint = sprint, Jump = jump, JumpHeld = brake, Interact = interact, Honk = honk,
            };
            reload = false; slot = -1; pressed = false; jump = false; interact = false;
            return c;
        }

        IEnumerator Start()
        {
            gm = GameManager.Instance;
            p = gm.Player;
            roads = FindAnyObjectByType<RoadNetwork>();
            p.CommandOverride = Command;
            t0 = Time.time;
            StartCoroutine(KeepAlive());
            yield return Stroll(Seconds * 0.10f);
            yield return Brawl(Seconds * 0.12f);
            yield return GunFight(Seconds * 0.36f - Now, gun: 2);
            yield return GunFight(Seconds * 0.52f - Now, gun: 3);
            float driveUntil = Seconds * 0.86f;
            while (Now < driveUntil)
            {
                while (p.Health.Dead) yield return null;
                if (p.CurrentVehicle == null) yield return StealCar(driveUntil - Now);
                if (p.CurrentVehicle != null) yield return Drive(driveUntil - Now);
                yield return null;
            }
            yield return Wreck();
            yield return GunFight(Seconds + 5f - Now, gun: 2);
        }

        /// <summary>The recording is of playing the game, not of waiting at the start after a death: health and the car are topped up when nearly gone.</summary>
        IEnumerator KeepAlive()
        {
            while (true)
            {
                if (p.Health.Current < 35f && !p.Health.Dead) { p.Health.Restore(); Log($"t={Now:F0} health topped up"); }
                var car = p.CurrentVehicle;
                if (car != null) car.Protected = car.CurrentHealth < targetCarHealthFloor;
                yield return null;
            }
        }

        // ---- moving about ----

        float lastWantYaw, lastWantPitch;

        /// <summary>
        /// Turn the view smoothly towards a point: fast when far off, easing in at the end, as a hand on a mouse does.
        /// It also follows the point's own movement, or a running target would always be a little ahead of the aim.
        /// </summary>
        float TurnTo(Vector3 point, float maxPerSecond = 220f)
        {
            Vector3 dir = point - p.Cam.transform.position;
            var q = Quaternion.LookRotation(dir);
            float wantYaw = q.eulerAngles.y, wantPitch = Mathf.DeltaAngle(0f, q.eulerAngles.x);
            float dy = Mathf.DeltaAngle(p.Yaw, wantYaw), dp = wantPitch - p.Pitch;
            float gain = Mathf.Min(1f, 9f * Time.deltaTime), most = maxPerSecond * Time.deltaTime;
            float followY = Mathf.Clamp(Mathf.DeltaAngle(lastWantYaw, wantYaw), -1.5f, 1.5f), followP = Mathf.Clamp(wantPitch - lastWantPitch, -1.5f, 1.5f);
            lastWantYaw = wantYaw; lastWantPitch = wantPitch;
            float scale = p.AimingDownSights ? 1f / 0.55f : 1f;    // the same movement of the hand turns less when aiming down the sights
            look = new Vector2(Mathf.Clamp(dy * gain + followY, -most, most) * scale, -Mathf.Clamp(dp * gain + followP, -most, most) * scale);
            return Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dp));
        }

        Vector3 lastPos;
        float blockedFor, sidestepUntil, sidestep = 1f;

        /// <summary>Walking into something the navmesh does not know of, such as a parked car: a few steps aside, or a jump, and on.</summary>
        void GetRoundThings()
        {
            float moved = (p.transform.position - lastPos).Flat().magnitude / Mathf.Max(Time.deltaTime, 0.001f);
            lastPos = p.transform.position;
            if (Now < sidestepUntil) { move = new Vector2(sidestep, -0.4f); sprint = false; return; }
            blockedFor = move.sqrMagnitude > 0.1f && moved < 0.5f ? blockedFor + Time.deltaTime : 0f;
            if (blockedFor > 0.5f)
            {
                blockedFor = 0f;
                sidestep = -sidestep;
                sidestepUntil = Now + Random.Range(0.8f, 1.5f);
                jump = Random.value < 0.3f;
            }
        }

        /// <summary>One frame of going towards a place on foot, along the navmesh. False when there is no way.</summary>
        bool Head(Vector3 place, bool faceIt)
        {
            var path = new NavMeshPath();
            if (!NavMesh.SamplePosition(p.transform.position, out var a, 4f, NavMesh.AllAreas) || !NavMesh.SamplePosition(place, out var b, 4f, NavMesh.AllAreas)) return false;
            if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) || path.corners.Length < 2) return false;
            Vector3 next = path.corners[1];
            if ((next - p.transform.position).Flat().magnitude < 0.6f && path.corners.Length > 2) next = path.corners[2];
            if (faceIt) TurnTo(next + Vector3.up * 1.5f, 150f);
            Vector3 to = (next - p.transform.position).Flat().normalized;
            Vector3 local = Quaternion.Euler(0f, -p.Yaw, 0f) * to;
            move = new Vector2(local.x, local.z);
            GetRoundThings();
            return true;
        }

        EnemyAI NearestEnemy(float within = 1e9f, bool mustSee = false)
        {
            EnemyAI best = null; float bestD = within;
            Vector3 eye = p.Cam.transform.position;
            foreach (var e in gm.Alive)
            {
                if (e == null || e.Health.Dead) continue;
                float d = Vector3.Distance(p.transform.position, e.transform.position);
                bool see = !Physics.Linecast(eye, e.transform.position + Vector3.up * 1.2f, Layers.WorldMask, QueryTriggerInteraction.Ignore);
                if (mustSee && !see) continue;
                if (!see) d *= 2.5f;
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        // ---- the scenes ----

        /// <summary>A walk, a look round, a run with a couple of jumps, in peace.</summary>
        IEnumerator Stroll(float time)
        {
            Log($"t={Now:F0} stroll");
            float end = Now + time;
            Vector3 start = p.transform.position;
            var first = gm.Alive.Where(e => e != null && !e.Health.Dead).OrderBy(e => Vector3.Distance(e.transform.position, start)).FirstOrDefault();
            Log($"first enemy {(first != null ? Vector3.Distance(first.transform.position, start) : -1f):F0} m away");
            Vector3 Goal() => first != null ? first.transform.position : start + p.transform.forward * 60f;
            float nextJump = Now + time * 0.55f;
            while (Now < end)
            {
                float k = 1f - (end - Now) / time;
                move = Vector2.zero; look = Vector2.zero;
                if (k < 0.3f)
                {
                    Head(Goal(), k < 0.05f || k > 0.25f);
                    if (k >= 0.05f && k <= 0.25f) look.x = Mathf.Sin((k - 0.05f) / 0.2f * Mathf.PI * 2f) * 55f * Time.deltaTime;
                }
                else
                {
                    sprint = true;
                    Head(Goal(), true);
                    if (Now > nextJump) { jump = true; nextJump = Now + 2.4f; }
                }
                yield return null;
            }
            sprint = false;
        }

        /// <summary>Fists on the nearest enemy, which sets the whole city off.</summary>
        IEnumerator Brawl(float time)
        {
            var victim = gm.Alive.Where(e => e != null && !e.Health.Dead).OrderBy(e => Vector3.Distance(e.transform.position, p.transform.position)).FirstOrDefault();
            if (victim == null) yield break;
            Log($"t={Now:F0} brawl, enemy {Vector3.Distance(victim.transform.position, p.transform.position):F0} m away");
            float end = Now + time, hitAt = -1f;
            while (Now < end && (hitAt < 0f || Now < hitAt + 3.5f))
            {
                move = Vector2.zero; look = Vector2.zero; fire = false;
                if (hitAt < 0f && gm.Hostile) { hitAt = Now; Log($"t={Now:F0} first punch landed"); }
                if (victim == null || victim.Health.Dead)
                {
                    victim = NearestEnemy(40f);
                    if (victim == null) { yield return null; continue; }
                }
                Vector3 chest = victim.transform.position + Vector3.up * 1.3f;
                TurnTo(chest, 300f);
                float d = (victim.transform.position - p.transform.position).Flat().magnitude;
                if (d > 1.0f) { sprint = d > 1.3f; Head(victim.transform.position, false); }
                else sprint = false;
                fire = d < 1.15f;
                yield return null;
            }
            fire = false; sprint = false;
        }

        /// <summary>A gun fight: aim at whoever is in sight, go after them when nobody is, reload when empty.</summary>
        IEnumerator GunFight(float time, int gun)
        {
            Log($"t={Now:F0} gun fight, gun {gun}, hostile={gm.Hostile}");
            gm.MakeHostile();
            slot = gun;
            yield return null;
            float end = Now + time, nextPick = 0f, strafe = 1f, nextStrafe = 0f;
            EnemyAI target = null;
            while (Now < end)
            {
                move = Vector2.zero; look = Vector2.zero; fire = false; ads = false; sprint = false;
                if (Now > nextPick || target == null || target.Health.Dead) { target = NearestEnemy(); nextPick = Now + 1.5f; }
                if (target != null)
                {
                    Vector3 aim = target.transform.position + Vector3.up * ((Time.frameCount / 300) % 4 == 3 ? 1.62f : 1.2f);
                    Vector3 eye = p.Cam.transform.position;
                    bool visible = !Physics.Linecast(eye, aim, Layers.WorldMask, QueryTriggerInteraction.Ignore);
                    float dist = Vector3.Distance(eye, aim);
                    if (visible && dist < 45f)
                    {
                        float off = TurnTo(aim);
                        ads = off < 12f;
                        if (off < 2.5f)
                        {
                            fire = true;
                            pressed = Time.frameCount % 10 == 0;
                        }
                        if (Now > nextStrafe) { strafe = -strafe; nextStrafe = Now + Random.Range(1.2f, 2.6f); }
                        move = new Vector2(strafe, 0f);
                        GetRoundThings();
                    }
                    else
                    {
                        sprint = dist > 12f;
                        Head(target.transform.position, true);
                    }
                }
                if (p.Armed && p.Gun.InMag == 0) reload = true;
                yield return null;
            }
            fire = false; ads = false; move = Vector2.zero; look = Vector2.zero;
        }

        static bool Sound(Vehicle v) => v != null && !v.Exploded && !v.HasDriver && !v.OnFire && v.CurrentHealth > 90f;

        Vehicle SoundCar() => Vehicle.All.Where(Sound).OrderBy(v => Vector3.Distance(v.transform.position, p.transform.position)).FirstOrDefault();

        /// <summary>Runs to the nearest free car in good order and takes it.</summary>
        IEnumerator StealCar(float time)
        {
            Log($"t={Now:F0} going for a car, kills {gm.Kills}, deaths {gm.Deaths}");
            gm.MakeHostile();    // after a death the city has calmed down: the drive is a getaway all the same
            float end = Now + Mathf.Max(time, 15f);
            slot = -2;
            Vehicle car = null;
            while (Now < end && p.CurrentVehicle == null && !p.Health.Dead)
            {
                move = Vector2.zero; look = Vector2.zero;
                if (car == null || !Sound(car)) car = SoundCar();
                if (car == null) { yield return null; continue; }
                if (p.VehicleInReach != null && Sound(p.VehicleInReach)) { interact = true; yield return null; yield return new WaitForSeconds(0.2f); }
                else
                {
                    sprint = true;
                    Head(car.transform.position, true);
                    if (Time.frameCount % 150 == 0 && p.MoveSpeed < 1f) jump = true;
                }
                yield return null;
            }
            move = Vector2.zero; sprint = false;
            while (p.Boarding) yield return null;
            if (p.CurrentVehicle == null)   // no way through to it: the recording goes on regardless
            {
                car = SoundCar();
                if (car != null && !p.Health.Dead) { p.EnterVehicle(car); Log("could not reach a car in time, placed in the nearest"); }
            }
            Log($"t={Now:F0} in a car: {p.CurrentVehicle != null}");
        }

        /// <summary>Follows the lanes from junction to junction, a stretch from inside, with slides at some of the corners.</summary>
        IEnumerator Drive(float time)
        {
            var car = p.CurrentVehicle;
            if (car == null) yield break;
            Log($"t={Now:F0} driving for {time:F0} s");
            float end = Now + time, stuckFor = 0f, nextHonk = Now + 6f;
            int lane = roads.GetNearestLane(car.transform.position, out int at);
            Vector3[] route = roads.Lanes[lane].Points;
            bool onConnector = false;
            look = Vector2.zero; fire = false; ads = false;
            while (Now < end && p.CurrentVehicle != null)
            {
                // The next point far enough ahead on the route.
                Vector3 pos = car.transform.position;
                float speed = car.Speed, lookAhead = Mathf.Max(7f, speed * 0.75f);
                while (at < route.Length - 1 && Vector3.Distance(route[at].Flat(), pos.Flat()) < lookAhead) at++;
                if (at >= route.Length - 1 && Vector3.Distance(route[^1].Flat(), pos.Flat()) < lookAhead)
                {
                    if (onConnector)
                    {
                        // Off the junction, on to the lane it leads to.
                        lane = pendingLane; route = roads.Lanes[lane].Points; at = 0; onConnector = false;
                    }
                    else
                    {
                        var next = roads.Lanes[lane].NextConnectors;
                        if (next.Length > 0)
                        {
                            int c = next[Random.Range(0, next.Length)];
                            pendingLane = roads.Connectors[c].ToLane; currentTurn = roads.Connectors[c].Turn;
                            route = roads.Connectors[c].Points; at = 0; onConnector = true;
                        }
                        else { lane = roads.GetNearestLane(pos + car.transform.forward * 12f, out at); route = roads.Lanes[lane].Points; }
                    }
                }
                Vector3 aim = route[Mathf.Clamp(at, 0, route.Length - 1)];
                Vector3 local = car.transform.InverseTransformPoint(aim);
                float steer = Mathf.Clamp(local.x / Mathf.Max(3f, lookAhead) * 2.6f, -1f, 1f);

                // Speed for what is ahead: quick on the straight, down for corners, and a slide through some of them.
                bool turning = onConnector && currentTurn != 0;
                float distToEnd = Vector3.Distance(route[^1].Flat(), pos.Flat());
                bool cornerSoon = !onConnector && distToEnd < speed * 1.5f + 6f;
                float want = turning ? 9f : cornerSoon ? 13f : 24f;
                float gas = speed < want - 1f ? 1f : speed > want + 3f ? -0.6f : 0.2f;
                bool slide = turning && speed > 8f && Mathf.Abs(steer) > 0.5f;
                if (slide) { steer = Mathf.Sign(steer); gas = 0.6f; }

                // Someone on the road ahead gets run over.
                foreach (var e in gm.Alive)
                {
                    if (e == null || e.Health.Dead) continue;
                    Vector3 them = car.transform.InverseTransformPoint(e.transform.position);
                    if (them.z < 4f || them.z > 32f || Mathf.Abs(them.x) > them.z * 0.3f) continue;
                    if (!NavMesh.Raycast(pos, e.transform.position, out _, NavMesh.AllAreas) || !Physics.Linecast(pos + Vector3.up, e.transform.position + Vector3.up, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                    {
                        steer = Mathf.Clamp(them.x / them.z * 3f, -1f, 1f);
                        gas = 1f; slide = false;
                        break;
                    }
                }

                // A hit that stops the car: back off, and on again.
                stuckFor = speed < 1.2f && Now - t0 > 0f ? stuckFor + Time.deltaTime : 0f;
                if (stuckFor > 1f)
                {
                    float until = Now + 1.5f;
                    Log($"t={Now:F0} stuck, backing off");
                    while (Now < until && p.CurrentVehicle != null)
                    {
                        move = new Vector2(-steer, -1f); brake = false;
                        yield return null;
                    }
                    lane = roads.GetNearestLane(car.transform.position + car.transform.forward * 6f, out at);
                    route = roads.Lanes[lane].Points; onConnector = false; stuckFor = 0f;
                    continue;
                }
                move = new Vector2(steer, gas);
                brake = slide;
                honk = Now > nextHonk && Now < nextHonk + 0.5f;
                if (Now > nextHonk + 0.5f) nextHonk = Now + Random.Range(9f, 16f);
                yield return null;
            }
            honk = false; brake = false; move = Vector2.zero;
        }
        int pendingLane, currentTurn;

        /// <summary>Hard stop, out of the car, a few steps back, and shoots it until it goes up.</summary>
        IEnumerator Wreck()
        {
            var car = p.CurrentVehicle;
            if (car == null) yield break;
            Log($"t={Now:F0} stopping, car health {car.CurrentHealth:F0}, smoke {car.SmokeAmount:F2}, kills {gm.Kills}, deaths {gm.Deaths}");
            targetCarHealthFloor = 0f;
            car.Protected = false;
            brake = true; move = Vector2.zero;
            float by = Now + 3.5f;
            while (Now < by && Mathf.Abs(car.ForwardSpeed) > 3f) yield return null;
            brake = false;
            interact = true;
            yield return null;
            yield return new WaitForSeconds(0.4f);
            if (p.CurrentVehicle != null) { p.LeaveVehicle(true); Log("left the car directly"); }
            slot = 2;
            float end = Now + 2f;
            while (Now < end)
            {
                look = Vector2.zero;
                TurnTo(car.transform.position + Vector3.up, 260f);
                Vector3 back = (p.transform.position - car.transform.position).Flat().normalized;
                Vector3 l = Quaternion.Euler(0f, -p.Yaw, 0f) * back;
                move = new Vector2(l.x, l.z);
                sprint = false;
                yield return null;
            }
            move = Vector2.zero;
            end = Now + 8f;
            while (Now < end && !car.Exploded)
            {
                look = Vector2.zero; ads = true;
                float off = TurnTo(car.transform.position + Vector3.up * 0.8f);
                fire = off < 4f;
                pressed = fire && Time.frameCount % 10 == 0;
                if (p.Armed && p.Gun.InMag == 0) reload = true;
                yield return null;
            }
            Log($"t={Now:F0} car exploded: {car.Exploded}, on fire {car.OnFire}");
            fire = false; ads = false;
            look = Vector2.zero;
            yield return new WaitForSeconds(1.6f);
        }
    }
}
