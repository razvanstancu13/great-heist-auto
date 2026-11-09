using UnityEngine;

namespace GreatHeistAuto
{
    public enum WeaponKind { Pistol, Rifle }

    /// <summary>One gun. Damage is not here: the head / body rule of Health applies to every gun.</summary>
    [CreateAssetMenu(menuName = "Great Heist Auto/Weapon")]
    public class WeaponData : ScriptableObject
    {
        public string DisplayName = "Gun";
        public WeaponKind Kind;
        public GameObject Model;

        [Header("Handling")]
        public float RoundsPerMinute = 400;
        public bool Automatic;
        public int MagazineSize = 15;
        public float ReloadSeconds = 1.6f;
        public float SpreadDegrees = 0.6f;
        public float BulletSpeed = 220f;
        public float RecoilDegrees = 1.2f;

        [Header("Model points, local to the model, metres (set by the builder)")]
        public Vector3 ModelEuler;
        public float ModelScale = 1f;
        public Vector3 GripPoint;
        public Vector3 SupportPoint;
        public Vector3 MuzzlePoint;
        public float SightHeight = 0.06f;    // top of the gun above the grip point

        [Header("Sound")]
        public AudioClip[] ShotClips;

        public float SecondsPerShot => 60f / Mathf.Max(1f, RoundsPerMinute);
    }
}
