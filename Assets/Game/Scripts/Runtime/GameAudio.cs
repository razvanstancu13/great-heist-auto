using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>Pooled one-shots with pitch variation, plus the ambience and music loops. Clips are assigned by the scene builder.</summary>
    public class GameAudio : MonoBehaviour
    {
        public static GameAudio Instance { get; private set; }

        public AudioClip[] Reload, Empty, HitBody, Headshot, Impact, Whiz, Footstep, Death, Honk, Explosion, Crash, Door, Gore, Thunder, CarHit, Punch;
        public AudioClip Ambience, NightAmbience, Music, MusicCalm, MusicCar, Engine, FireLoop, Rain, SkidLoop;
        public float AmbienceVolume = 0.5f, MusicVolume = 0.55f;

        AudioSource dayAmb, nightAmb, hostileMusic, calmMusic, carMusic;
        /// <summary>Which music is playing: for the automated test.</summary>
        public string CurrentMusic { get; private set; } = "calm";
        const float MusicFadeSeconds = 1.6f;
        /// <summary>What is left of the ambience inside a car.</summary>
        public const float InCarAmbience = 0.1f;
        /// <summary>How much louder the car's music plays than the music on foot.</summary>
        public const float CarMusicBoost = 1.3f;
        /// <summary>1 on foot, down to InCarAmbience in a car. Rain follows it too.</summary>
        public float AmbienceLevel { get; private set; } = 1f;
        public float CarMusicVolume => carMusic != null ? carMusic.volume : 0f;

        readonly List<AudioSource> pool = new();
        int next;

        void Awake()
        {
            Instance = this;
            for (int i = 0; i < 28; i++)
            {
                var go = new GameObject("Sfx" + i);
                go.transform.SetParent(transform);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false; s.spatialBlend = 1f; s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 6f; s.maxDistance = 220f; s.dopplerLevel = 0f;
                pool.Add(s);
            }
            dayAmb = StartLoop(Ambience, AmbienceVolume);
            nightAmb = StartLoop(NightAmbience, 0f);
            hostileMusic = StartLoop(Music, 0f);
            calmMusic = StartLoop(MusicCalm, MusicVolume);
            carMusic = StartLoop(MusicCar, 0f);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        AudioSource StartLoop(AudioClip clip, float volume)
        {
            if (clip == null) return null;
            var s = gameObject.AddComponent<AudioSource>();
            s.clip = clip; s.loop = true; s.volume = volume; s.spatialBlend = 0f; s.Play();
            return s;
        }

        void Update()
        {
            float n = DayNightCycle.NightAmount;
            var player = GameManager.Instance != null ? GameManager.Instance.Player : null;
            bool inCar = player != null && player.CurrentVehicle != null;
            // The doors shut out the city.
            AmbienceLevel = Mathf.MoveTowards(AmbienceLevel, inCar ? InCarAmbience : 1f, Time.deltaTime / MusicFadeSeconds);
            if (dayAmb != null) dayAmb.volume = AmbienceVolume * (1f - n) * AmbienceLevel;
            if (nightAmb != null) nightAmb.volume = AmbienceVolume * n * AmbienceLevel;

            // The player's car has its own music; on foot it follows the mood of the city.
            var gm = GameManager.Instance;
            CurrentMusic = gm != null && gm.Player != null && gm.Player.CurrentVehicle != null ? "car" : gm != null && gm.Hostile ? "hostile" : "calm";
            Blend(hostileMusic, CurrentMusic == "hostile");
            Blend(calmMusic, CurrentMusic == "calm");
            Blend(carMusic, CurrentMusic == "car", CarMusicBoost);
        }

        void Blend(AudioSource s, bool on, float boost = 1f)
        {
            if (s != null) s.volume = Mathf.MoveTowards(s.volume, on ? MusicVolume * boost : 0f, MusicVolume / MusicFadeSeconds * Time.deltaTime);
        }

        public void PlayAt(AudioClip[] clips, Vector3 pos, float volume = 1f, float minDistance = 6f)
        {
            if (clips == null || clips.Length == 0) return;
            var s = pool[next]; next = (next + 1) % pool.Count;
            s.transform.position = pos;
            s.pitch = Random.Range(0.94f, 1.06f);
            s.minDistance = minDistance;
            s.PlayOneShot(clips[Random.Range(0, clips.Length)], volume);
        }
    }
}
