using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>A trigger collider on a bone. Bullets read the zone and the owner from it.</summary>
    public class Hitbox : MonoBehaviour
    {
        public HitZone Zone;
        public Health Owner;
        public int Team;
    }
}
