using System;
using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// Fades a whole object in or out through the TintLit shader's dissolve, for anything that appears or goes away: corpses, wrecks, dropped
    /// guns, people and cars coming back. The object gets its own copies of its materials only while fading, so it costs nothing otherwise.
    /// </summary>
    public class Fader : MonoBehaviour
    {
        const string Keyword = "_GHA_FADE";
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        /// <summary>How many objects are fading now: for the automated test.</summary>
        public static int Active;
        /// <summary>Fades finished since the start: for the automated test.</summary>
        public static int Finished;

        class Entry { public Renderer R; public Material[] Original; public Material[] Copies; }
        readonly List<Entry> entries = new();
        float value, target, speed;
        Action done;
        bool fading;

        public static void In(GameObject go, float seconds = 0.7f) => Get(go).Begin(0f, 1f, seconds, null);

        public static void Out(GameObject go, float seconds, Action then) => Get(go).Begin(1f, 0f, seconds, then);

        /// <summary>Waits, fades out, then removes the object.</summary>
        public static void OutAndDestroy(GameObject go, float wait, float seconds = 0.9f)
        {
            var f = Get(go);
            f.StartCoroutine(f.Later(wait, seconds));
        }

        System.Collections.IEnumerator Later(float wait, float seconds)
        {
            yield return new WaitForSeconds(wait);
            Begin(1f, 0f, seconds, () => Destroy(gameObject));
        }

        static Fader Get(GameObject go)
        {
            var f = go.GetComponent<Fader>();
            return f != null ? f : go.AddComponent<Fader>();
        }

        void Begin(float from, float to, float seconds, Action then)
        {
            Restore();
            foreach (var r in GetComponentsInChildren<Renderer>(false))
            {
                if (r is ParticleSystemRenderer) continue;
                var original = r.sharedMaterials;
                bool any = false;
                var copies = new Material[original.Length];
                for (int i = 0; i < original.Length; i++)
                {
                    copies[i] = original[i];
                    if (original[i] == null || !original[i].HasProperty(FadeId)) continue;
                    copies[i] = new Material(original[i]);
                    copies[i].EnableKeyword(Keyword);
                    copies[i].SetFloat(FadeId, from);
                    any = true;
                }
                if (!any) continue;
                r.sharedMaterials = copies;
                entries.Add(new Entry { R = r, Original = original, Copies = copies });
            }
            value = from; target = to; speed = 1f / Mathf.Max(0.01f, seconds); done = then;
            fading = true;
            Active++;
            enabled = true;
        }

        void Update()
        {
            if (!fading) { enabled = false; return; }
            value = Mathf.MoveTowards(value, target, speed * Time.deltaTime);
            foreach (var e in entries)
                foreach (var m in e.Copies)
                    if (m != null && m.HasProperty(FadeId)) m.SetFloat(FadeId, value);
            if (value != target) return;
            var then = done;
            done = null;
            Restore();
            Finished++;
            then?.Invoke();
        }

        void Restore()
        {
            if (fading) { fading = false; Active--; }
            foreach (var e in entries)
            {
                if (e.R != null) e.R.sharedMaterials = e.Original;
                for (int i = 0; i < e.Copies.Length; i++)
                    if (e.Copies[i] != e.Original[i] && e.Copies[i] != null) Destroy(e.Copies[i]);
            }
            entries.Clear();
        }

        void OnDestroy() => Restore();
    }
}
