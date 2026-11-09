using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// Times the city's first frames for the loading log: one of these runs before every other script (order -10000), its partner
    /// after (order 10000), so the gaps between them are all the objects' Awake, all the Start calls, and the first frame's drawing.
    /// tools/check_loading.py prints the lines.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public class LoadTimingFirst : MonoBehaviour
    {
        public static float AwakeAt, StartAt;
        void Awake() => AwakeAt = Time.realtimeSinceStartup;
        void Start() => StartAt = Time.realtimeSinceStartup;
    }

    [DefaultExecutionOrder(10000)]
    public class LoadTimingLast : MonoBehaviour
    {
        float startedDone;
        int frames;

        void Awake() => Debug.Log($"[LOAD] timing: all Awake {(Time.realtimeSinceStartup - LoadTimingFirst.AwakeAt) * 1000f:F0} ms");

        void Start()
        {
            startedDone = Time.realtimeSinceStartup;
            Debug.Log($"[LOAD] timing: all Start {(startedDone - LoadTimingFirst.StartAt) * 1000f:F0} ms");
        }

        void Update()
        {
            // The frames after: how long each took from the end of the last one (drawing included), for the first few.
            if (frames++ > 4) { enabled = false; return; }
            Debug.Log($"[LOAD] timing: city frame {frames} took {Time.unscaledDeltaTime * 1000f:F0} ms");
        }
    }
}
