using System;
using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>All bullets: real travel time (so they can be dodged), a swept raycast per frame, a tracer, then the hit.</summary>
    public class BulletSystem : MonoBehaviour
    {
        public static BulletSystem Instance { get; private set; }

        /// <summary>Raised when a bullet of the given team passes near someone: position on the bullet line, shooter's position.</summary>
        public static event Action<Vector3, Vector3, Health> BulletFired;

        /// <summary>Every landed bullet: victim, zone, shooter. Used by the HUD (hit marker).</summary>
        public static event Action<Health, HitZone, Health> BulletLanded;

        class Bullet
        {
            public Vector3 Pos, Dir;
            public float Speed, Life;
            public int Team;
            public Health Shooter;
            public LineRenderer Line;
        }

        readonly List<Bullet> live = new();
        readonly Stack<LineRenderer> pool = new();
        readonly RaycastHit[] hits = new RaycastHit[16];
        public Material tracerMat;
        const float MaxLife = 2.5f;
        public const float CarDamagePerBullet = 7f;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Fire(Vector3 origin, Vector3 dir, float speed, int team, Health shooter)
        {
            var line = pool.Count > 0 ? pool.Pop() : MakeLine();
            line.gameObject.SetActive(true);
            line.SetPosition(0, origin); line.SetPosition(1, origin);
            live.Add(new Bullet { Pos = origin, Dir = dir.normalized, Speed = speed, Team = team, Shooter = shooter, Line = line });
            BulletFired?.Invoke(origin, dir.normalized, shooter);
        }

        LineRenderer MakeLine()
        {
            var go = new GameObject("Tracer");
            go.transform.SetParent(transform);
            var l = go.AddComponent<LineRenderer>();
            l.sharedMaterial = tracerMat;
            l.positionCount = 2; l.useWorldSpace = true;
            l.startWidth = 0.012f; l.endWidth = 0.035f;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            return l;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var b = live[i];
                b.Life += dt;
                float step = b.Speed * dt;
                bool done = b.Life > MaxLife;
                if (TryHit(b, step, out var hit))
                {
                    ApplyHit(b, hit);
                    b.Pos = hit.point;
                    done = true;
                }
                else b.Pos += b.Dir * step;

                b.Line.SetPosition(0, b.Pos - b.Dir * Mathf.Min(step * 1.2f, 14f));
                b.Line.SetPosition(1, b.Pos);

                if (done)
                {
                    b.Line.gameObject.SetActive(false);
                    pool.Push(b.Line);
                    live.RemoveAt(i);
                }
            }
        }

        bool TryHit(Bullet b, float step, out RaycastHit best)
        {
            int n = Physics.RaycastNonAlloc(b.Pos, b.Dir, hits, step, Layers.BulletMask, QueryTriggerInteraction.Collide);
            best = default;
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.distance >= bestDist) continue;
                if (h.collider.gameObject.layer == Layers.Hitbox)
                {
                    var box = h.collider.GetComponent<Hitbox>();
                    if (box == null || box.Team == b.Team || box.Owner == null || box.Owner.Dead) continue;
                }
                best = h; bestDist = h.distance; found = true;
            }
            // A head and the chest can both lie on the line: the head wins only if it is nearer, which the sort above did.
            return found;
        }

        void ApplyHit(Bullet b, RaycastHit h)
        {
            if (h.collider.gameObject.layer == Layers.Hitbox)
            {
                var box = h.collider.GetComponent<Hitbox>();
                box.Owner.TakeHit(box.Zone, h.point, b.Dir, b.Shooter);
                BulletLanded?.Invoke(box.Owner, box.Zone, b.Shooter);
                GameAudio.Instance?.PlayAt(box.Zone == HitZone.Head ? GameAudio.Instance.Headshot : GameAudio.Instance.HitBody, h.point, 0.9f);
            }
            else
            {
                FxPool.Instance?.Impact(h.point, h.normal);
                GameAudio.Instance?.PlayAt(GameAudio.Instance.Impact, h.point, 0.55f);
                var car = h.collider.GetComponentInParent<Vehicle>();
                if (car != null)
                {
                    FxPool.Instance?.VehicleHit(h.point, h.normal);
                    car.TakeDamage(CarDamagePerBullet, h.point, h.normal, b.Shooter);
                }
            }
        }
    }
}
