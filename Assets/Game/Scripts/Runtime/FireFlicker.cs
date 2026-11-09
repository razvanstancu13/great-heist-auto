using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>Makes a fire's light dance.</summary>
    public class FireFlicker : MonoBehaviour
    {
        Light l;
        float baseIntensity, seed;

        void Awake()
        {
            l = GetComponent<Light>();
            baseIntensity = l.intensity;
            seed = Random.value * 100f;
        }

        void Update() => l.intensity = baseIntensity * Mathf.Lerp(0.6f, 1.25f, Mathf.PerlinNoise(seed, Time.time * 7f));
    }
}
