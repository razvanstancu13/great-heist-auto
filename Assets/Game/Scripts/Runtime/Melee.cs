using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>A short shove along the ground, through whatever moves this body normally, so it cannot be pushed into walls.</summary>
    public class Knockback : MonoBehaviour
    {
        const float Seconds = 0.22f;
        Vector3 dir;
        float left, speed;
        CharacterController cc;
        UnityEngine.AI.NavMeshAgent agent;

        public static void Apply(GameObject body, Vector3 direction, float distance)
        {
            if (distance <= 0.01f) return;
            var k = body.GetComponent<Knockback>();
            if (k == null) k = body.AddComponent<Knockback>();
            k.dir = direction.Flat().normalized;
            k.left = distance;
            k.speed = distance / Seconds;
            k.enabled = true;
        }

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        }

        void Update()
        {
            if (transform.parent != null || left <= 0f) { enabled = false; return; }
            float step = Mathf.Min(left, speed * Time.deltaTime);
            left -= step;
            Vector3 move = dir * step;
            if (cc != null && cc.enabled) cc.Move(move);
            else if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Move(move);
            else transform.position += move;
        }
    }

    /// <summary>What a landed punch does, whoever threw it.</summary>
    public static class Melee
    {
        /// <summary>How far a punch connects, feet to feet: an arm's length plus the width of the other body.</summary>
        public const float Reach = 1.1f;
        /// <summary>The distance two people end up at when one steps in to punch.</summary>
        public const float StandOff = 0.84f;

        public static void Land(Health attacker, Health target, float damage)
        {
            Vector3 dir = (target.transform.position - attacker.transform.position).Flat().normalized;
            Vector3 point = target.transform.position + Vector3.up * 1.25f - dir * 0.2f;
            target.Apply(damage, HitZone.Body, point, dir, attacker, DamageCause.Melee, Vector3.zero);
            target.GetComponentInChildren<CharacterRig>()?.Flinch(dir);
            Knockback.Apply(target.gameObject, dir, target.Dead ? 0.25f : 0.5f);
            var a = GameAudio.Instance;
            a?.PlayAt(a.Punch, point, 1f, 8f);
        }

        /// <summary>Square up before a punch: step in if the other is a little far, back if standing inside them.</summary>
        public static void Square(GameObject attacker, Vector3 attackerPos, Vector3 targetPos)
        {
            Vector3 to = (targetPos - attackerPos).Flat();
            float d = to.magnitude;
            if (d > 0.01f) Knockback.Apply(attacker, to, 0f);
            if (d > StandOff + 0.05f) Knockback.Apply(attacker, to, d - StandOff);
            else if (d < StandOff - 0.1f) Knockback.Apply(attacker, -to, StandOff - d);
        }
    }
}
