using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>
    /// One entry point that makes the whole game from the imported art: Tools > Great Heist Auto > Build Everything,
    /// or tools/build_game.py in batch mode. Everything in Assets/Game/Generated and Assets/Game/Scenes can be regenerated.
    /// </summary>
    public static class Builder
    {
        const int CitySeed = 20261103;
        const string CityScene = "Assets/Game/Scenes/City.unity";
        const string MenuScene = "Assets/Game/Scenes/MainMenu.unity";
        const string LabScene = "Assets/Game/Scenes/AnimLab.unity";

        [MenuItem("Tools/Great Heist Auto/Build Everything")]
        public static void BuildAll()
        {
            Art.ClearCache();
            ProjectSetup();
            Art.ConfigureImports();
            AssetDatabase.DeleteAsset($"{Art.Gen}/City");
            AssetDatabase.DeleteAsset($"{Art.Gen}/Clips");
            Art.EnsureFolder($"{Art.Gen}/Materials");
            var weapons = Actors.BuildWeapons();
            var player = Actors.BuildPlayerPrefab(weapons);
            var enemies = new[] { "enemy_a", "enemy_b", "enemy_c" }.Select(Actors.BuildEnemyPrefab).ToArray();
            var vehicles = Actors.BuildVehiclePrefabs();
            var pedestrians = new[] { "ped_a", "ped_b" }.Select(Actors.BuildPedestrianPrefab).ToArray();
            Art.EnsureFolder("Assets/Game/Scenes");
            BuildMenuScene();
            BuildCityScene(player, enemies, weapons, vehicles, pedestrians);
            BuildLabScene(player, enemies, pedestrians, weapons, vehicles["car"]);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MenuScene, true), new EditorBuildSettingsScene(CityScene, true), new EditorBuildSettingsScene(LabScene, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[BUILD] OK everything built");
        }

        /// <summary>Batch entry: build everything and then the Windows player. Args: -ghaoutput path/GreatHeistAuto.exe</summary>
        public static void BuildPlayerBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            string output = "Build/GreatHeistAuto.exe";
            bool skipContent = args.Contains("-ghaskipcontent");
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-ghaoutput") output = args[i + 1];
            if (!skipContent) BuildAll();
            BuildPlayer(output);
        }

        /// <summary>The same build from inside a running Editor (tools/build_game.py, when the project is open): its log goes to the given file.</summary>
        public static string BuildInEditor(string output, bool skipContent, string logFile)
        {
            var lines = new System.Text.StringBuilder();
            void Keep(string text, string stack, LogType type)
            {
                lines.AppendLine(text);
                if (type == LogType.Exception) lines.AppendLine(stack);
            }
            Application.logMessageReceived += Keep;
            try
            {
                if (!skipContent) BuildAll();
                BuildPlayer(output);
            }
            finally
            {
                Application.logMessageReceived -= Keep;
                System.IO.File.WriteAllText(logFile, lines.ToString());
                EditorSceneManager.OpenScene(MenuScene);
            }
            return "built";
        }

        public static void BuildPlayer(string output)
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { MenuScene, CityScene, LabScene },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[BUILD] player {report.summary.result} size {report.summary.totalSize / (1024 * 1024)} MB errors {report.summary.totalErrors}");
            if (report.summary.result != BuildResult.Succeeded) throw new System.Exception("player build failed");
        }

        static void ProjectSetup()
        {
            // Layers.
            var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tags.FindProperty("layers");
            void SetLayer(int i, string n) { layers.GetArrayElementAtIndex(i).stringValue = n; }
            SetLayer(Layers.World, "World"); SetLayer(Layers.Hitbox, "Hitbox"); SetLayer(Layers.Character, "Character"); SetLayer(Layers.Debris, "Debris");
            tags.ApplyModifiedPropertiesWithoutUndo();

            for (int i = 0; i < 32; i++)
            {
                Physics.IgnoreLayerCollision(Layers.Hitbox, i, true);
                Physics.IgnoreLayerCollision(Layers.Debris, i, i != Layers.World);
            }
            Physics.IgnoreLayerCollision(Layers.Character, Layers.Character, false);

            PlayerSettings.productName = "Great Heist Auto";
            PlayerSettings.companyName = "LAGP";
            PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // The PC render pipeline everywhere, with shadows that reach down a street.
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            if (asset != null)
            {
                GraphicsSettings.defaultRenderPipeline = asset;
                for (int i = 0; i < QualitySettings.names.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = asset;
                }
                asset.shadowDistance = 140f;
                asset.shadowCascadeCount = 3;
                asset.supportsCameraOpaqueTexture = false;
                asset.msaaSampleCount = 2;
                EditorUtility.SetDirty(asset);
            }
        }

        // ---- scenes ----

        static void BuildMenuScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            camGo.AddComponent<AudioListener>();
            var go = new GameObject("MainMenu");
            var menu = go.AddComponent<MainMenu>();
            menu.Background = AssetDatabase.LoadAssetAtPath<VideoClip>($"{Art.Root}/Video/menu.mp4");
            menu.BackgroundStill = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art.Root}/UI/menu_key.png");
            menu.Logo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art.Root}/UI/logo.png");
            menu.Music = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/music_menu_0.ogg");
            var clicks = Art.Clips("ui_click");
            menu.Click = clicks.Length > 0 ? clicks[0] : null;
            EditorSceneManager.SaveScene(scene, MenuScene);
        }

        /// <summary>The animation test bench: every character, clip, gun and pose on a treadmill floor (see AnimLab).</summary>
        static void BuildLabScene(GameObject player, GameObject[] enemies, GameObject[] pedestrians, WeaponData[] weapons, GameObject car)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.52f, .54f, .58f);
            var sunGo = new GameObject("Sun");
            sunGo.transform.rotation = Quaternion.Euler(52f, 35f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.5f; sun.shadows = LightShadows.Soft;
            sunGo.AddComponent<UniversalAdditionalLightData>();

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f; cam.farClipPlane = 300f; cam.fieldOfView = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.42f, .46f, .52f);
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UniversalAdditionalCameraData>();

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Treadmill";
            floor.layer = Layers.World;
            floor.transform.position = new Vector3(0, -0.05f, 0);
            floor.transform.localScale = new Vector3(60f, 0.1f, 60f);
            var floorMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            floorMat.SetFloat("_Smoothness", 0.1f);
            Art.EnsureFolder($"{Art.Gen}/Materials");
            AssetDatabase.DeleteAsset($"{Art.Gen}/Materials/lab_floor.mat");
            AssetDatabase.CreateAsset(floorMat, $"{Art.Gen}/Materials/lab_floor.mat");
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;

            var lab = new GameObject("AnimLab").AddComponent<AnimLab>();
            lab.Cam = cam; lab.Floor = floor.GetComponent<MeshRenderer>(); lab.Weapons = weapons; lab.CarPrefab = car;
            AnimLab.Entry E(string name, GameObject prefab, Color tint) => new AnimLab.Entry
            {
                Name = name, Prefab = prefab, Tint = tint,
                Clips = Actors.AllClips(name),
            };
            lab.Characters = new[]
            {
                E("player", player, Color.white),
                E("enemy_a", enemies[0], Color.white), E("enemy_b", enemies[1], Color.white), E("enemy_c", enemies[2], Color.white),
                E("ped_a", pedestrians[0], Color.white), E("ped_b", pedestrians[1], Color.white),
            };
            EditorSceneManager.SaveScene(scene, LabScene);
        }

        static void BuildCityScene(GameObject playerPrefab, GameObject[] enemyPrefabs, WeaponData[] weapons, System.Collections.Generic.Dictionary<string, GameObject> vehicles, GameObject[] pedestrians)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var (sun, sky, volume) = SetupLighting();

            var cityRoot = new GameObject("City");
            CityBuilder.Build(cityRoot.transform, CitySeed, vehicles);
            var data = cityRoot.GetComponent<CityData>();

            // Camera.
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.08f; cam.farClipPlane = 1800f; cam.fieldOfView = 58f;
            camGo.AddComponent<AudioListener>();
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            // Player.
            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            player.transform.SetPositionAndRotation(data.PlayerStart, Quaternion.Euler(0, data.PlayerStartYaw, 0));
            var pc = player.GetComponent<PlayerController>();
            pc.Cam = cam;

            // Systems.
            var sys = new GameObject("Game");
            var fx = sys.AddComponent<FxPool>();
            string shaderParticles = "Universal Render Pipeline/Particles/Unlit";
            Texture2D Ui(string n) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art.Root}/UI/{n}.png");
            var muzzle = Ui("muzzle");
            var smoke = Ui("smoke_soft");
            var hole = Ui("bullet_hole");
            fx.flashMat = Art.FxMat("flash", shaderParticles, muzzle, Color.white, true);
            fx.smokeMat = Art.FxMat("smoke", shaderParticles, smoke, new Color(1.1f, 1.08f, 1.05f, .6f), false);
            fx.bloodMat = Art.FxMat("blood", shaderParticles, Ui("blood_a"), Color.white, false);
            fx.fireMat = Art.FxMat("fire", shaderParticles, Ui("fire"), new Color(1.4f, 1.2f, 1f, 1f), true);
            fx.explosionMat = Art.FxMat("explosion", shaderParticles, Ui("explosion"), new Color(1.6f, 1.3f, 1f, 1f), true);
            fx.burnSmokeMat = Art.FxMat("burnsmoke", shaderParticles, smoke, new Color(.24f, .23f, .22f, .5f), false);
            fx.exhaustMat = Art.FxMat("exhaust", shaderParticles, smoke, new Color(1f, 1f, 1f, .3f), false);
            fx.sparkMat = Art.FxMat("spark", shaderParticles, null, new Color(1.6f, 1f, .45f, 1f), true);
            fx.skidMat = Art.FxMat("skid", shaderParticles, null, new Color(.018f, .018f, .02f, .8f), false);
            fx.splatMats = new[]
            {
                Art.Decal("splat_a", Ui("blood_a")),
                Art.Decal("splat_b", Ui("blood_b")),
                Art.Decal("splat_c", Ui("blood_c")),
            };
            fx.poolMat = Art.Decal("pool", Ui("blood_pool"));
            fx.scorchMat = Art.FxMat("scorch", shaderParticles, smoke, new Color(.015f, .014f, .012f, 1.6f), false);
            var chunk = Art.Plain("m_chunk", Shader.Find("GreatHeistAuto/TintLit"));
            chunk.SetColor("_Color", new Color(.34f, .03f, .03f)); chunk.SetFloat("_SmoothLight", .75f); chunk.SetFloat("_SmoothDark", .75f);
            EditorUtility.SetDirty(chunk);
            fx.chunkMat = chunk;
            fx.holeMat = Art.FxMat("hole", shaderParticles, hole, new Color(.2f, .19f, .18f, 1f), false);
            var bullets = sys.AddComponent<BulletSystem>();
            bullets.tracerMat = Art.FxMat("tracer", shaderParticles, null, new Color(1f, .85f, .5f, .9f), true);

            var audio = sys.AddComponent<GameAudio>();
            audio.Reload = Art.Clips("reload"); audio.Empty = Art.Clips("empty"); audio.HitBody = Art.Clips("hit_body");
            audio.Headshot = Art.Clips("headshot"); audio.Impact = Art.Clips("impact"); audio.Whiz = Art.Clips("whiz");
            audio.Footstep = Art.Clips("footstep"); audio.Death = Art.Clips("death");
            audio.Honk = Art.Clips("honk"); audio.Explosion = Art.Clips("explosion"); audio.Crash = Art.Clips("crash"); audio.Door = Art.Clips("door");
            audio.CarHit = Art.Clips("car_hit"); audio.Punch = Art.Clips("punch"); audio.Gore = Art.Clips("gore"); audio.Thunder = Art.Clips("thunder");
            AudioClip Loop(string n) => AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/{n}_0.ogg");
            audio.Engine = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/engine_0.wav");
            audio.SkidLoop = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/skid_loop_0.wav");
            audio.NightAmbience = Loop("night_amb"); audio.FireLoop = Loop("fire"); audio.Rain = Loop("rain");
            audio.Ambience = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/city_amb_0.ogg");
            audio.Music = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/music_combat_0.ogg");
            audio.MusicCalm = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/music_calm_0.ogg");
            audio.MusicCar = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Art.Root}/Audio/music_car_0.ogg");

            var gm = sys.AddComponent<GameManager>();
            gm.Player = pc;
            gm.EnemyPrefabs = enemyPrefabs;
            gm.GrenadePrefab = Actors.BuildGrenadePrefab();
            gm.EnemyWeapons = System.Array.FindAll(weapons, w => w.Kind == WeaponKind.Pistol);   // only the player has rifles
            gm.EnemyTints = new[]
            {
                new Color(.75f,.18f,.14f), new Color(.2f,.32f,.62f), new Color(.2f,.45f,.25f), new Color(.75f,.55f,.15f),
                new Color(.35f,.35f,.38f), new Color(.5f,.25f,.55f), new Color(.85f,.85f,.85f), new Color(.15f,.15f,.17f),
            };
            gm.PedestrianPrefabs = pedestrians;
            // Coloured enough to tell people apart, muted enough to belong in this city.
            gm.PedestrianTints = new[]
            {
                new Color(.56f,.24f,.21f), new Color(.25f,.34f,.5f), new Color(.66f,.56f,.32f), new Color(.31f,.44f,.33f),
                new Color(.58f,.4f,.46f), new Color(.38f,.32f,.47f), new Color(.62f,.42f,.27f), new Color(.29f,.46f,.49f),
                new Color(.74f,.71f,.64f), new Color(.42f,.42f,.44f),
            };
            var hudGo = new GameObject("HUD Root");
            var hud = hudGo.AddComponent<GameHud>();
            hud.Player = pc;
            hud.BloodScreen = new[] { Ui("blood_a"), Ui("blood_b"), Ui("blood_c") };

            var probe = BakeReflections(cityRoot.transform);

            var cycle = sys.AddComponent<DayNightCycle>();
            cycle.Sun = sun; cycle.Sky = sky; cycle.Probe = probe;
            cycle.Flare = sun.GetComponent<LensFlareComponentSRP>();

            var weather = sys.AddComponent<WeatherSystem>();
            weather.Cycle = cycle;
            weather.RainMat = Art.FxMat("rain", shaderParticles, null, new Color(.72f, .78f, .85f, .34f), false);
            weather.SplashMat = Art.FxMat("splash", shaderParticles, smoke, new Color(.8f, .85f, .9f, .5f), false);

            var lightsGo = new GameObject("Street Lights");
            lightsGo.AddComponent<StreetLights>();

            var cine = sys.AddComponent<CinematicFx>();
            cine.Volume = volume; cine.Player = pc;

            // Last, after everything that needs the whole city (reflections): its heavy parts are switched on in segments while loading.
            var segments = SegmentLoader.Prepare(cityRoot.transform, data.PlayerStart, "Meshes", "Trees", "Cars");
            var timing = new GameObject("Load Timing");
            timing.AddComponent<LoadTimingFirst>(); timing.AddComponent<LoadTimingLast>();
            Debug.Log($"[BUILD] city loads in segments: {segments.Segments.Length} parts");

            EditorSceneManager.SaveScene(scene, CityScene);
        }

        static (Light sun, Material sky, Volume volume) SetupLighting()
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, .93f, .82f);
            sun.intensity = 2.05f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = .92f;
            sun.shadowBias = 0.06f; sun.shadowNormalBias = 0.5f;
            sunGo.transform.rotation = Quaternion.Euler(50f, 140f, 0f);
            RenderSettings.sun = sun;

            // A lens flare drawn by URP: a soft halo on the sun and ghosts along the line through the screen centre.
            string flarePath = $"{Art.Gen}/Materials/sun_flare.asset";
            AssetDatabase.DeleteAsset(flarePath);
            var flare = ScriptableObject.CreateInstance<LensFlareDataSRP>();
            LensFlareDataElementSRP E(float pos, float size, Color c, float power, float soft = 0.1f) => new LensFlareDataElementSRP
            {
                flareType = SRPLensFlareType.Circle, position = pos, uniformScale = size, tint = c, localIntensity = power,
                blendMode = SRPLensFlareBlendMode.Additive, modulateByLightColor = true, fallOff = 1f, edgeOffset = soft,
            };
            flare.elements = new[]
            {
                E(0f, 3f, new Color(1f, .88f, .7f), 0.22f, 1f),
                E(0f, 0.9f, new Color(1f, .97f, .9f), 0.45f, 1f),
                E(0.45f, 0.3f, new Color(.5f, .75f, 1f), 0.13f, 0.55f),
                E(0.8f, 0.55f, new Color(.55f, 1f, .7f), 0.07f, 0.55f),
                E(1.25f, 0.22f, new Color(1f, .55f, .3f), 0.16f, 0.55f),
                E(1.7f, 0.85f, new Color(.7f, .55f, 1f), 0.05f, 0.55f),
                E(2.15f, 0.4f, new Color(1f, .8f, .5f), 0.1f, 0.55f),
            };
            AssetDatabase.CreateAsset(flare, flarePath);
            var lf = sunGo.AddComponent<LensFlareComponentSRP>();
            lf.lensFlareData = flare;
            lf.intensity = 1f;
            lf.attenuationByLightShape = false;
            lf.useOcclusion = true;
            lf.occlusionRadius = 0.04f;
            lf.sampleCount = 16;
            lf.allowOffScreen = false;

            Texture2D Tx(string n) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art.Root}/Textures/{n}.png");
            var sky = Art.Plain("sky", Shader.Find("GreatHeistAuto/Sky"));
            sky.SetTexture("_DayTex", Tx("sky"));
            sky.SetTexture("_DuskTex", Tx("sky_dusk"));
            sky.SetTexture("_NightTex", Tx("sky_night"));
            sky.SetVector("_SunDir", -sunGo.transform.forward);
            sky.SetColor("_FogCol", new Color(.72f, .76f, .82f));
            sky.SetFloat("_FogAmount", 0.04f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;
            Shader.SetGlobalFloat("_GHA_Night", 0f);

            RenderSettings.ambientMode = AmbientMode.Custom;
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(new Color(.6f, .62f, .68f));
            RenderSettings.ambientProbe = sh;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(.72f, .76f, .82f);
            RenderSettings.fogDensity = 0.0016f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;

            // The film look: ACES, teal shadows and warm highlights, grain, a soft bloom, a little lens fringe and a vignette.
            // Motion blur and depth of field start at zero and are driven by CinematicFx.
            var volGo = new GameObject("Post Processing");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            var profilePath = $"{Art.Gen}/Materials/post.asset";
            AssetDatabase.DeleteAsset(profilePath);
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(p, profilePath);
            var tone = p.Add<Tonemapping>(true); tone.mode.Override(TonemappingMode.ACES);
            var bloom = p.Add<Bloom>(true); bloom.intensity.Override(0.55f); bloom.threshold.Override(1f); bloom.scatter.Override(0.68f);
            var grade = p.Add<ColorAdjustments>(true); grade.contrast.Override(13f); grade.saturation.Override(-6f); grade.postExposure.Override(0.05f);
            var split = p.Add<SplitToning>(true); split.shadows.Override(new Color(.46f, .52f, .56f)); split.highlights.Override(new Color(.62f, .55f, .47f)); split.balance.Override(0f);
            var vig = p.Add<Vignette>(true); vig.intensity.Override(0.26f); vig.smoothness.Override(0.45f);
            var grain = p.Add<FilmGrain>(true); grain.type.Override(FilmGrainLookup.Medium2); grain.intensity.Override(0.2f); grain.response.Override(0.75f);
            var chroma = p.Add<ChromaticAberration>(true); chroma.intensity.Override(0.09f);
            var blur = p.Add<MotionBlur>(true); blur.mode.Override(MotionBlurMode.CameraAndObjects); blur.quality.Override(MotionBlurQuality.High); blur.intensity.Override(0f); blur.clamp.Override(0.12f);
            var dof = p.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Gaussian); dof.gaussianStart.Override(30f); dof.gaussianEnd.Override(80f); dof.gaussianMaxRadius.Override(0f);
            foreach (var c in p.components) AssetDatabase.AddObjectToAsset(c, p);
            EditorUtility.SetDirty(p);
            vol.sharedProfile = p;
            return (sun, sky, vol);
        }

        static ReflectionProbe BakeReflections(Transform cityRoot)
        {
            var go = new GameObject("Reflections");
            go.transform.SetParent(cityRoot, false);
            go.transform.position = new Vector3(0, 24f, 0);
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.size = new Vector3(1800, 500, 1800);
            probe.resolution = 256;
            probe.boxProjection = false;
            probe.hdr = true;
            probe.farClipPlane = 1300f;
            string path = $"{Art.Gen}/City/reflections.exr";
            if (!Lightmapping.BakeReflectionProbe(probe, path))
                Debug.LogWarning("[BUILD] reflection probe bake failed, windows will reflect the skybox only");
            return probe;
        }
    }
}
