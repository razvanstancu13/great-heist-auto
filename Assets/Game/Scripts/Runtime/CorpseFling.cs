using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>A body thrown by a car or a blast: flies, tumbles, leaves blood behind, and hits the ground with a splat and a pool.</summary>
    public class CorpseFling : MonoBehaviour
    {
        public Vector3 Velocity;
        public Vector3 Spin;
        Transform body;
        CharacterRig rig;
        float nextDrip, flying;

        public void Begin(Transform bodyVisual, Vector3 velocity)
        {
            body = bodyVisual;
            rig = body != null ? body.GetComponent<CharacterRig>() : null;
            rig?.GoLimp();
            Velocity = velocity;
            Spin = new Vector3(Random.Range(-300f, 300f), Random.Range(-120f, 120f), Random.Range(-300f, 300f));
        }

        void Update()
        {
            float dt = Time.deltaTime;
            flying += dt;
            if (flying > 6f)
            {
                // StayGrounded brings it down from here.
                if (body != null) body.rotation = Quaternion.Euler(0, body.eulerAngles.y, 0);
                rig?.LieDown();
                Destroy(this);
                return;
            }
            Velocity += Vector3.down * (14f * dt);
            Vector3 step = Velocity * dt;

            if (Physics.Raycast(transform.position + Vector3.up * 0.3f, step.normalized, out var hit, step.magnitude + 0.3f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                FxPool.Instance?.Blood(hit.point + Vector3.up * 0.2f, hit.normal, hit.normal, 1.2f);
                FxPool.Instance?.BloodSplat(hit.point, hit.normal, 1.8f);
                var a = GameAudio.Instance;
                a?.PlayAt(a.Gore, hit.point, 0.9f);
                if (Mathf.Abs(hit.normal.y) > 0.6f && Velocity.magnitude < 7f)
                {
                    transform.position = hit.point + Vector3.up * 0.02f;
                    if (body != null) body.rotation = Quaternion.Euler(0, body.eulerAngles.y, 0);
                    rig?.LieDown();
                    FxPool.Instance?.BloodPool(hit.point);
                    Destroy(this);
                    return;
                }
                // Bounces off whatever it hit.
                Velocity = Vector3.Reflect(Velocity, hit.normal) * 0.35f;
                transform.position = hit.point + hit.normal * 0.35f;
                return;
            }
            transform.position += step;
            if (body != null) body.Rotate(Spin * dt, Space.World);

            if (Time.time > nextDrip)
            {
                nextDrip = Time.time + 0.05f;
                FxPool.Instance?.Blood(transform.position + Vector3.up * 0.8f, Vector3.up, Random.onUnitSphere, 0.5f);
            }
        }
    }
}
