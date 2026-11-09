using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// The city comes in segments. The builder saves the heavy parts (merged meshes, trees, parked cars) switched off, nearest the
    /// player first; this switches them on a batch per frame behind the loading screen. Setting up and first drawing hundreds of
    /// objects in one frame stalled the loading spinner; in batches every frame stays short. The batch grows while frames are quick
    /// and shrinks when one is slow. Colliders, lamps, the NavMesh and the game's systems are never switched off.
    /// </summary>
    public class SegmentLoader : MonoBehaviour
    {
        public GameObject[] Segments;

        /// <summary>Everything is on: the loading screen may go.</summary>
        public static bool Done { get; private set; } = true;
        public static float Progress { get; private set; } = 1f;

        const float TargetFrame = 1f / 40f;     // seconds: a frame this long or shorter still turns the spinner smoothly
        int next;
        float batch = 24f;

        void Awake()
        {
            Done = Segments == null || Segments.Length == 0;
            Progress = Done ? 1f : 0f;
        }

        void Update()
        {
            if (Done) return;
            // The last frame's length says how much the last batch cost, drawing included.
            float last = Time.unscaledDeltaTime;
            batch = last > TargetFrame * 1.4f ? Mathf.Max(4f, batch * 0.5f) : last < TargetFrame * 0.7f ? Mathf.Min(400f, batch * 1.5f) : batch;
            int end = Mathf.Min(Segments.Length, next + Mathf.CeilToInt(batch));
            for (; next < end; next++)
                if (Segments[next] != null) Segments[next].SetActive(true);
            Progress = (float)next / Segments.Length;
            if (next >= Segments.Length)
            {
                Done = true;
                Debug.Log($"[LOAD] {Segments.Length} city segments on, {Time.frameCount} frames in");
                enabled = false;
            }
        }

        void OnDestroy() { Done = true; Progress = 1f; }

        /// <summary>For the builder: switch off the children of these parts of the city and hand them to a loader, nearest
        /// <paramref name="first"/> first.</summary>
        public static SegmentLoader Prepare(Transform cityRoot, Vector3 first, params string[] parts)
        {
            var list = new List<GameObject>();
            foreach (var part in parts)
            {
                var t = cityRoot.Find(part);
                if (t == null) { Debug.LogWarning("[BUILD] no city part " + part); continue; }
                foreach (Transform c in t) if (c.gameObject.activeSelf) list.Add(c.gameObject);
            }
            list.Sort((a, b) => SqrDist(a, first).CompareTo(SqrDist(b, first)));
            foreach (var g in list) g.SetActive(false);
            var loader = cityRoot.gameObject.AddComponent<SegmentLoader>();
            loader.Segments = list.ToArray();
            return loader;
        }

        static float SqrDist(GameObject g, Vector3 p)
        {
            var r = g.GetComponentInChildren<Renderer>();
            Vector3 c = r != null ? r.bounds.center : g.transform.position;
            return (c - p).sqrMagnitude;
        }
    }
}
