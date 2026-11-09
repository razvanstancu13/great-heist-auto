using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// What a decal may lie on: solid things whose collider is exactly their visible surface, such as roads, pavements and walls.
    /// Not cars (they drive away, and their box is larger than the shell) and not colliders named "Rough" (hedges, benches, crates: their box
    /// only roughly matches the mesh, so a decal on it would hang in the air).
    /// </summary>
    public static class Surface
    {
        static readonly RaycastHit[] Buffer = new RaycastHit[16];

        public static bool Paintable(Collider c) =>
            c != null && c.attachedRigidbody == null && c is BoxCollider && !c.name.StartsWith("Rough");

        /// <summary>The nearest paintable surface along a ray, looking straight through anything else (so: the road under a parked car).</summary>
        public static bool FindPaintable(Vector3 from, Vector3 dir, float distance, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(from, dir, Buffer, distance, Layers.WorldMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (Buffer[i].distance < nearest && Paintable(Buffer[i].collider))
                {
                    nearest = Buffer[i].distance;
                    best = Buffer[i];
                }
            return nearest < float.MaxValue;
        }

        /// <summary>
        /// The largest size up to 'size' at which a round decal lies fully on the plane through pos: 0 if it does not fit at all.
        /// A decal crossing a kerb or the edge of a wall would stick out into the air.
        /// </summary>
        public static float Fit(Vector3 pos, Vector3 normal, float size)
        {
            if (!FindPaintable(pos + normal * 0.06f, -normal, 0.14f, out _)) return 0f;
            Vector3 t = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 b = Vector3.Cross(normal, t);
            foreach (float scale in Scales)
                if (Covers(pos, normal, t, b, size * scale)) return size * scale;
            return 0f;
        }

        static readonly float[] Scales = { 1f, 0.75f, 0.55f, 0.4f, 0.28f };

        static bool Covers(Vector3 pos, Vector3 normal, Vector3 t, Vector3 b, float size)
        {
            float r = size * 0.5f;
            for (int ring = 1; ring <= 2; ring++)
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI * 0.25f;
                    Vector3 q = pos + (t * Mathf.Cos(a) + b * Mathf.Sin(a)) * (r * ring * 0.5f);
                    if (!FindPaintable(q + normal * 0.25f, -normal, 0.4f, out var hit)) return false;
                    if (Mathf.Abs(Vector3.Dot(hit.point - pos, normal)) > 0.035f) return false;
                }
            return true;
        }
    }
}
