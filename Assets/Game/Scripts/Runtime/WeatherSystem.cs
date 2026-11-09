using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// Random weather: spells of clear sky, fog and rain with slow changes between them. Rain falls around the camera and makes the
    /// streets wet (all TintLit materials with a wet response read _GHA_Wet), and it brings the odd lightning strike.
    /// </summary>
    public class WeatherSystem : MonoBehaviour
    {
        public enum Kind { Clear, Fog, Rain, FoggyRain }

        public DayNightCycle Cycle;
        public Material RainMat, SplashMat;
        public float ClearFog = 0.0016f, FogFog = 0.022f, RainFog = 0.0065f;
        public float MinSpell = 55f, MaxSpell = 130f, ChangeSeconds = 12f;

        public Kind Current { get; private set; } = Kind.Clear;
        public bool Locked { get; set; }

        float nextChange, fogNow, rainNow, overcastNow, wetNow, flash, nextLightning;
        ParticleSystem rain, splash;
        AudioSource rainAudio;
        Light sun;
        static readonly int WetId = Shader.PropertyToID("_GHA_Wet");

        void Start()
        {
            fogNow = ClearFog;
            rain = MakeRain();
            splash = MakeSplash();
            rainAudio = gameObject.AddComponent<AudioSource>();
            rainAudio.spatialBlend = 0f; rainAudio.loop = true; rainAudio.volume = 0f;
            if (GameAudio.Instance != null) rainAudio.clip = GameAudio.Instance.Rain;
            if (rainAudio.clip != null) rainAudio.Play();
            nextChange = Time.time + Random.Range(MinSpell * .6f, MaxSpell * .7f);
            sun = Cycle != null ? Cycle.Sun : null;
        }

        public void Set(Kind k, bool lockIt = true)
        {
            Current = k;
            Locked = lockIt;
        }

        ParticleSystem MakeRain()
        {
            var go = new GameObject("Rain");
            go.transform.SetParent(transform);
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = p.main;
            main.loop = true; main.maxParticles = 5000; main.startLifetime = 1.1f; main.startSpeed = 0f;
            main.startSize3D = true;
            main.startSizeX = 0.016f; main.startSizeY = 0.6f; main.startSizeZ = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            var vel = p.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(1.2f, 2.2f); vel.y = new ParticleSystem.MinMaxCurve(-24f, -19f); vel.z = new ParticleSystem.MinMaxCurve(0.2f, 1f);
            var em = p.emission; em.enabled = true; em.rateOverTime = 0f;
            var sh = p.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(42f, 0.5f, 42f);
            var r = p.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = RainMat; r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.03f; r.lengthScale = 1f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            p.Play();
            return p;
        }

        ParticleSystem MakeSplash()
        {
            var go = new GameObject("Splash");
            go.transform.SetParent(transform);
            var p = go.AddComponent<ParticleSystem>();
            p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = p.main;
            main.loop = true; main.maxParticles = 1200; main.startLifetime = 0.22f; main.startSpeed = 0.5f; main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.14f);
            main.simulationSpace = ParticleSystemSimulationSpace.World; main.playOnAwake = false;
            var em = p.emission; em.enabled = true; em.rateOverTime = 0f;
            var sh = p.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(26f, 0.02f, 26f);
            var col = p.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(.8f, 0), new GradientAlphaKey(0, 1) });
            col.color = g;
            var r = p.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = SplashMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            p.Play();
            return p;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (!Locked && Time.time >= nextChange)
            {
                nextChange = Time.time + Random.Range(MinSpell, MaxSpell);
                float roll = Random.value;
                Current = roll < 0.4f ? Kind.Clear : roll < 0.62f ? Kind.Fog : roll < 0.9f ? Kind.Rain : Kind.FoggyRain;
            }

            bool raining = Current == Kind.Rain || Current == Kind.FoggyRain;
            float fogTarget = Current switch { Kind.Fog => FogFog, Kind.FoggyRain => FogFog * 0.8f, Kind.Rain => RainFog, _ => ClearFog };
            float speed = 1f / ChangeSeconds;
            fogNow = Mathf.MoveTowards(fogNow, fogTarget, (FogFog - ClearFog) * speed * dt);
            rainNow = Mathf.MoveTowards(rainNow, raining ? 1f : 0f, speed * dt);
            overcastNow = Mathf.MoveTowards(overcastNow, Current == Kind.Clear ? 0f : Current == Kind.Fog ? 0.55f : 1f, speed * dt);
            wetNow = Mathf.MoveTowards(wetNow, raining ? 1f : 0f, dt * (raining ? 1f / 15f : 1f / 45f));   // roads dry slowly

            RenderSettings.fogDensity = fogNow;
            Shader.SetGlobalFloat(WetId, wetNow);
            if (Cycle != null) Cycle.Overcast = overcastNow;

            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 c = cam.transform.position;
                rain.transform.position = c + Vector3.up * 15f + cam.transform.forward * 7f;
                splash.transform.position = new Vector3(c.x, 0.18f, c.z) + Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up) * 7f;
            }
            var em = rain.emission; em.rateOverTime = 4200f * rainNow;
            var es = splash.emission; es.rateOverTime = 1500f * rainNow;
            if (rainAudio != null) rainAudio.volume = 0.8f * rainNow * (GameAudio.Instance != null ? GameAudio.Instance.AmbienceLevel : 1f);

            // Lightning.
            if (rainNow > 0.7f && Time.time >= nextLightning)
            {
                nextLightning = Time.time + Random.Range(14f, 38f);
                flash = 1f;
                var a = GameAudio.Instance;
                if (a != null && a.Thunder != null && a.Thunder.Length > 0 && cam != null)
                    a.PlayAt(a.Thunder, cam.transform.position + Random.onUnitSphere * 40f, 1f, 80f);
            }
            if (flash > 0f && sun != null)
            {
                flash = Mathf.MoveTowards(flash, 0f, dt * 2.2f);
                // A flicker on top of whatever the cycle set this frame.
                sun.intensity += flash * flash * 3.5f * (Mathf.PerlinNoise(Time.time * 30f, 3f) > .4f ? 1f : .2f);
            }
        }
    }
}
