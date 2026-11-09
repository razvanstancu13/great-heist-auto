using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// A thrown hand grenade: flies on a lobbed arc to land at the target, bounces and rolls, and goes off after its fuse. Thrown by
    /// enemies at where they last saw the player.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Grenade : MonoBehaviour
    {
        public const float Fuse = 3f, Radius = 6.5f, SureKill = 2.2f;

        public static int Thrown, Exploded;
        public static event System.Action<Grenade> ThrownEvent;

        Health owner;
        float goesOffAt;

        /// <summary>
        /// Throws one from <paramref name="from"/> so that it lands on <paramref name="target"/>, rising at least <paramref name="arc"/> metres
        /// above the higher of the two (over a low wall). Returns it, or null without a prefab.
        /// </summary>
        public static Grenade Throw(GameObject prefab, Vector3 from, Vector3 target, Health owner, float arc = 1.6f)
        {
            if (prefab == null) return null;
            var go = Instantiate(prefab, from, Random.rotation);
            var g = go.GetComponent<Grenade>();
            g.owner = owner;
            g.goesOffAt = Time.time + Fuse;
            var rb = go.GetComponent<Rigidbody>();
            rb.linearVelocity = LaunchVelocity(from, target, arc);
            rb.angularVelocity = Random.insideUnitSphere * 8f;
            Thrown++;
            ThrownEvent?.Invoke(g);
            return g;
        }

        /// <summary>The start velocity of a ballistic arc from a to b whose top is <paramref name="arc"/> above the higher end.</summary>
        public static Vector3 LaunchVelocity(Vector3 a, Vector3 b, float arc)
        {
            float g = -Physics.gravity.y;
            float top = Mathf.Max(a.y, b.y) + arc;
            float up = Mathf.Sqrt(2f * g * (top - a.y));
            float tUp = up / g, tDown = Mathf.Sqrt(2f * (top - b.y) / g);
            Vector3 flat = (b - a).Flat();
            return flat / (tUp + tDown) + Vector3.up * up;
        }

        public Vector3 Position => transform.position;
        public float SecondsLeft => goesOffAt - Time.time;

        void Update()
        {
            if (Time.time < goesOffAt) return;
            Vector3 c = transform.position + Vector3.up * 0.2f;
            FxPool.Instance?.Explosion(c);
            var a = GameAudio.Instance;
            a?.PlayAt(a.Explosion, c, 0.85f, 26f);
            Exploded++;
            Blast.Damage(c, Radius, SureKill, owner);
            GameManager.Instance?.ReportGunfire(c);
            Destroy(gameObject);
        }

        void OnCollisionEnter(Collision hit)
        {
            // A heavy steel body: bounces low and loses its speed quickly.
            var rb = GetComponent<Rigidbody>();
            rb.linearVelocity *= 0.55f;
        }
    }
}
