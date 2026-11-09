using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>An explosion's area damage, for a car going up and for a grenade: the same rules, a different size.</summary>
    public static class Blast
    {
        /// <summary>Any explosion, where and how big (radius): the player's camera shakes from it.</summary>
        public static event System.Action<Vector3, float> Happened;

        /// <summary>
        /// Certain death within <paramref name="sureKill"/> metres, falling off to nothing at <paramref name="radius"/>. Cover does not save
        /// anyone from the middle of it. Cars take damage and are pushed; <paramref name="except"/> (the car that exploded) is left out.
        /// </summary>
        public static void Damage(Vector3 c, float radius, float sureKill, Health source, Vehicle except = null)
        {
            Happened?.Invoke(c, radius);
            var hits = Physics.OverlapSphere(c, radius, (1 << Layers.Character) | Layers.WorldMask, QueryTriggerInteraction.Collide);
            var done = new HashSet<Health>();
            var cars = new HashSet<Vehicle>();
            foreach (var col in hits)
            {
                var car = col.GetComponentInParent<Vehicle>();
                if (car != null)
                {
                    if (car == except || !cars.Add(car)) continue;
                    float d = Vector3.Distance(car.transform.position, c);
                    car.TakeDamage(Mathf.Lerp(70f, 8f, d / radius), car.transform.position, Vector3.up, source);
                    var rb = car.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic) rb.AddExplosionForce(rb.mass * 7f * radius / 11f, c, radius, 1f, ForceMode.Impulse);
                    continue;
                }
                var h = col.GetComponentInParent<Health>();
                if (h == null || h.Dead || !done.Add(h)) continue;
                Vector3 pos = h.transform.position + Vector3.up;
                float dist = Vector3.Distance(pos, c);
                float dmg = dist < sureKill ? Health.MaxHealth : Mathf.Lerp(90f, 0f, (dist - sureKill) / (radius - sureKill));
                if (dmg < 1f) continue;
                Vector3 dir = (pos - c).normalized;
                h.Apply(dmg, HitZone.Body, pos, dir, source, DamageCause.Explosion, (dir + Vector3.up) * (dmg * 0.14f));
            }
        }
    }
}
