using UnityEngine;
using UnityEngine.Rendering;

namespace GreatHeistAuto
{
    /// <summary>
    /// Three minutes of day, three of night. Moves the sun, or the moon at night, drives the sky shader, ambient light, fog colour and
    /// reflections, and tells every shader how dark it is so windows and lamps can light up.
    /// </summary>
    public class DayNightCycle : MonoBehaviour
    {
        public static float NightAmount { get; private set; }

        public Light Sun;
        public Material Sky;
        public ReflectionProbe Probe;
        public LensFlareComponentSRP Flare;
        public float DaySeconds = 180f, NightSeconds = 180f;
        [Range(0f, 1f)] public float StartTime = 0.08f;
        public float SunAzimuth = 140f;

        /// <summary>0 to 0.5 is day (sunrise at 0, sunset at 0.5), 0.5 to 1 is night.</summary>
        public float Time01 { get; set; }
        public bool Paused { get; set; }
        public float Overcast { get; set; }   // from the weather
        public float FogColorMix { get; private set; }
        public Color FogColor { get; private set; }

        static readonly int NightId = Shader.PropertyToID("_GHA_Night");

        void Start() => Time01 = StartTime;

        void Update()
        {
            if (!Paused)
            {
                float dur = Time01 < 0.5f ? DaySeconds : NightSeconds;
                Time01 = Mathf.Repeat(Time01 + Time.deltaTime * 0.5f / dur, 1f);
            }
            Apply();
        }

        void Apply()
        {
            bool isDay = Time01 < 0.5f;
            float dayPhase = Time01 / 0.5f;
            float nightPhase = (Time01 - 0.5f) / 0.5f;
            float sunElev = isDay ? Mathf.Sin(dayPhase * Mathf.PI) : -Mathf.Sin(nightPhase * Mathf.PI);

            // sunElev runs -1..1. Full day above 0.25; a red band around the horizon; full night below -0.15.
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.3f, sunElev));
            float night = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.04f, -0.14f, sunElev));
            float dusk = Mathf.Clamp01(1f - day - night);
            NightAmount = night;
            Shader.SetGlobalFloat(NightId, night);

            // The sun crosses the sky; at night the same light turns into a dim moon on a fixed high path.
            Quaternion sunRot = Quaternion.Euler(0, SunAzimuth, 0) * Quaternion.Euler(Mathf.Lerp(4f, 176f, dayPhase), 0, 0);
            Quaternion moonRot = Quaternion.Euler(0, SunAzimuth + 70f, 0) * Quaternion.Euler(Mathf.Lerp(25f, 155f, nightPhase), 0, 0);
            Sun.transform.rotation = isDay ? sunRot : moonRot;

            float cloud = 1f - Overcast * 0.72f;
            if (isDay)
            {
                float sunUp = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.2f, sunElev));
                Sun.color = Color.Lerp(new Color(1f, .5f, .24f), new Color(1f, .93f, .82f), day);
                Sun.intensity = 2.05f * sunUp * cloud;
            }
            else
            {
                float moonUp = Mathf.SmoothStep(0f, 1f, Mathf.Sin(nightPhase * Mathf.PI) * 4f);
                Sun.color = new Color(.55f, .68f, 1f);
                Sun.intensity = 0.6f * moonUp * cloud;
            }

            Color amb = day * new Color(.72f, .74f, .78f) + dusk * new Color(.62f, .47f, .44f) + night * new Color(.17f, .2f, .3f);
            Color gnd = day * new Color(.46f, .43f, .38f) + dusk * new Color(.3f, .22f, .22f) + night * new Color(.08f, .09f, .13f);
            amb = Color.Lerp(amb, amb.grayscale * Color.white * .9f, Overcast * .6f);
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight((amb + gnd) * 0.5f);
            sh.AddDirectionalLight(Vector3.up, (amb - gnd) * 1.1f, 1f);
            RenderSettings.ambientProbe = sh;
            RenderSettings.reflectionIntensity = Mathf.Lerp(0.12f, 1f, day + dusk * .5f) * cloud;
            if (Probe != null) Probe.intensity = Mathf.Lerp(0.12f, 1f, day + dusk * .5f) * cloud;

            Color fog = day * new Color(.72f, .76f, .82f) + dusk * new Color(.75f, .5f, .4f) + night * new Color(.05f, .07f, .12f);
            fog = Color.Lerp(fog, fog.grayscale * Color.white, Overcast * .75f);
            FogColor = fog;
            RenderSettings.fogColor = fog;

            if (Sky != null)
            {
                Sky.SetFloat("_DayW", day);
                Sky.SetFloat("_DuskW", dusk);
                Sky.SetFloat("_NightW", night);
                Sky.SetFloat("_Overcast", Overcast);
                Sky.SetVector("_SunDir", -Sun.transform.forward);
                Sky.SetFloat("_IsDay", isDay ? 1f : 0f);
                Sky.SetColor("_FogCol", fog);
                Sky.SetFloat("_FogAmount", Mathf.Clamp01(RenderSettings.fogDensity * 26f));
            }

            if (Flare != null)
            {
                bool show = isDay && sunElev > 0.02f && Overcast < 0.5f;
                Flare.enabled = show;
                Flare.intensity = show ? Mathf.Lerp(0.55f, 1f, day) * (1f - Overcast * 1.6f) : 0f;
            }
        }
    }
}
