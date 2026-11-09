using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// Every lamp's bulb and light pool are always drawn at night by the NightGlow shader. Real lights are too expensive for
    /// hundreds of lamps, so a small pool of them follows the camera, standing at the nearest lamps to actually light characters and cars.
    /// </summary>
    public class StreetLights : MonoBehaviour
    {
        public int PoolSize = 28;
        public float Intensity = 55f;

        readonly List<Light> pool = new();
        Vector3[] lamps;
        float nextAssign;
        readonly List<(float d, int i)> order = new();

        void Start()
        {
            lamps = CityData.Instance != null ? CityData.Instance.Lamps : null;
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("StreetLight");
                go.transform.SetParent(transform);
                go.transform.rotation = Quaternion.Euler(90f, 0, 0);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot; l.spotAngle = 118f; l.innerSpotAngle = 50f; l.range = 17f;
                l.color = new Color(1f, .82f, .58f); l.shadows = LightShadows.None; l.enabled = false;
                pool.Add(l);
            }
        }

        void Update()
        {
            float night = DayNightCycle.NightAmount;
            bool on = night > 0.05f && lamps != null && lamps.Length > 0;
            if (on && Time.time >= nextAssign)
            {
                nextAssign = Time.time + 0.5f;
                var cam = Camera.main;
                Vector3 c = cam != null ? cam.transform.position : Vector3.zero;
                order.Clear();
                for (int i = 0; i < lamps.Length; i++) order.Add(((lamps[i] - c).sqrMagnitude, i));
                order.Sort((a, b) => a.d.CompareTo(b.d));
                for (int i = 0; i < pool.Count && i < order.Count; i++) pool[i].transform.position = lamps[order[i].i];
            }
            foreach (var l in pool)
            {
                l.enabled = on;
                if (on) l.intensity = Intensity * night;
            }
        }
    }
}
