using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// A corpse lies on whatever is under it, and falls when that goes: a car driving off or exploding, or an end of a flight that went wrong.
    /// </summary>
    public class StayGrounded : MonoBehaviour
    {
        public static readonly List<StayGrounded> All = new();

        const float Gravity = 14f;
        float fallSpeed, nextCheck;

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        /// <summary>How far above whatever is below it this body is now; 0 while something else is still moving it.</summary>
        public float Gap
        {
            get
            {
                if (transform.parent != null || GetComponent<CorpseFling>() != null) return 0f;
                return Ground(out var hit) ? transform.position.y - hit.point.y : 0f;
            }
        }

        bool Ground(out RaycastHit hit) =>
            Physics.Raycast(transform.position + Vector3.up * 0.6f, Vector3.down, out hit, 300f, Layers.WorldMask, QueryTriggerInteraction.Ignore);

        void Update()
        {
            if (transform.parent != null || GetComponent<CorpseFling>() != null) return;
            if (fallSpeed <= 0f && Time.time < nextCheck) return;
            nextCheck = Time.time + 0.2f;
            if (!Ground(out var hit)) return;

            Vector3 p = transform.position;
            float gap = p.y - hit.point.y;
            if (gap < -0.01f) { p.y = hit.point.y; transform.position = p; fallSpeed = 0f; return; }
            if (gap < 0.02f) { fallSpeed = 0f; return; }

            if (fallSpeed <= 0f && gap > 0.3f) GetComponentInChildren<CharacterRig>()?.GoLimp();
            fallSpeed += Gravity * Time.deltaTime;
            float step = fallSpeed * Time.deltaTime;
            if (step < gap) { p.y -= step; transform.position = p; return; }

            p.y = hit.point.y;
            transform.position = p;
            if (fallSpeed > 3f)
            {
                GetComponentInChildren<CharacterRig>()?.LieDown();
                var fx = FxPool.Instance;
                fx?.BloodSplat(hit.point, hit.normal, 1.3f);
                fx?.BloodPool(hit.point);
                var a = GameAudio.Instance;
                a?.PlayAt(a.Gore, hit.point, 0.6f);
            }
            fallSpeed = 0f;
        }
    }
}
