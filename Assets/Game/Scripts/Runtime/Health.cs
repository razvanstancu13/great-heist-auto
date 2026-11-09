using System;
using UnityEngine;

namespace GreatHeistAuto
{
    public enum HitZone { Body, Head }
    public enum DamageCause { Bullet, Vehicle, Explosion, Melee }

    /// <summary>The damage rule of the game: a head shot kills, a body shot takes 25 %, being run over kills.</summary>
    public class Health : MonoBehaviour
    {
        public const float BodyDamage = 25f;
        public const float MaxHealth = 100f;

        public float Current { get; private set; } = MaxHealth;
        public bool Dead => Current <= 0f;
        public bool Invulnerable { get; set; }

        // What happened last, read by the gore and death code.
        public DamageCause LastCause { get; private set; }
        public Vector3 LastImpulse { get; private set; }
        public float LastAmount { get; private set; }
        public HitZone LastZone { get; private set; }
        /// <summary>The way the last damage travelled (a bullet's flight, a car's motion).</summary>
        public Vector3 LastDir { get; private set; }

        /// <summary>zone, world position of the hit, direction the damage travelled, who did it.</summary>
        public event Action<HitZone, Vector3, Vector3, Health> Damaged;
        public event Action<HitZone, Health> Died;

        public static float DamageFor(HitZone zone) => zone == HitZone.Head ? MaxHealth : BodyDamage;

        public void TakeHit(HitZone zone, Vector3 point, Vector3 dir, Health shooter) =>
            Apply(DamageFor(zone), zone, point, dir, shooter, DamageCause.Bullet, Vector3.zero);

        public void Apply(float amount, HitZone zone, Vector3 point, Vector3 dir, Health source, DamageCause cause, Vector3 impulse)
        {
            if (Dead || Invulnerable) return;
            LastCause = cause;
            LastImpulse = impulse;
            LastAmount = amount;
            LastZone = zone;
            LastDir = dir;
            Current = Mathf.Max(0f, Current - amount);
            Damaged?.Invoke(zone, point, dir, source);
            if (Dead) Died?.Invoke(zone, source);
        }

        public void Restore() => Current = MaxHealth;
    }
}
