using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GreatHeistAuto
{
    /// <summary>Post-processing that follows the action: motion blur by speed, depth of field while aiming, a red pulse when hurt.</summary>
    [DefaultExecutionOrder(50)]
    public class CinematicFx : MonoBehaviour
    {
        public Volume Volume;
        public PlayerController Player;

        /// <summary>The automated test compares pictures with and without.</summary>
        public static bool ForceBlurOff;
        public float RunBlur = 0.2f;    // at a full sprint
        public float DriveBlur = 0.8f;  // at top speed

        MotionBlur blur;
        DepthOfField dof;
        ChromaticAberration chroma;
        ColorAdjustments grade;
        Vignette vignette;
        float pain, baseSaturation;

        void Start()
        {
            var p = Volume.profile;   // a private copy, so the asset on disk stays as it is
            p.TryGet(out blur);
            p.TryGet(out dof);
            p.TryGet(out chroma);
            p.TryGet(out grade);
            p.TryGet(out vignette);
            if (grade != null) baseSaturation = grade.saturation.value;
            Player.Health.Damaged += (_, _, _, _) => pain = 1f;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            pain = Mathf.MoveTowards(pain, 0f, dt * 1.5f);

            // Motion blur: subtle on foot, strong in a car, in proportion to speed.
            float target = 0f;
            var v = Player.CurrentVehicle;
            // Mode CameraAndObjects in the profile: the world streaks past, what moves with the camera stays sharp.
            if (v != null) target = Mathf.Lerp(0f, DriveBlur, Mathf.Clamp01(v.Speed / 28f));
            else target = Mathf.Lerp(0f, RunBlur, Mathf.Clamp01((Player.MoveSpeed - 1f) / 5.6f));
            if (ForceBlurOff) target = 0f;
            if (blur != null)
            {
                blur.intensity.value = Mathf.Lerp(blur.intensity.value, target, dt * 6f);
                blur.active = blur.intensity.value > 0.01f;
            }

            // Depth of field: the world behind the target softens while aiming.
            if (dof != null)
            {
                dof.active = Player.AdsAmount > 0.01f;
                float dist = Vector3.Distance(Player.Cam.transform.position, Player.Rig.AimPoint);
                dof.gaussianStart.value = Mathf.Lerp(dof.gaussianStart.value, Mathf.Max(8f, dist * 1.15f), dt * 8f);
                dof.gaussianEnd.value = dof.gaussianStart.value * 2.6f;
                dof.gaussianMaxRadius.value = 1.1f * Player.AdsAmount;
            }

            if (chroma != null) chroma.intensity.value = 0.09f + pain * 0.5f + (v != null ? Mathf.Clamp01(v.Speed / 30f) * 0.14f : 0f);
            if (vignette != null) vignette.intensity.value = 0.3f + pain * 0.2f + Player.AdsAmount * 0.08f;
            if (grade != null)
            {
                float hp = Player.Health.Current / Health.MaxHealth;
                grade.saturation.value = baseSaturation - (1f - hp) * 28f;
            }
        }
    }
}
