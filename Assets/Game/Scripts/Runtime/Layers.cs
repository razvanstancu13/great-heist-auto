using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>Physics layers, set up by the project builder (Editor/ProjectSetup).</summary>
    public static class Layers
    {
        public const int World = 8, Hitbox = 9, Character = 10, Debris = 11;
        public const int WorldMask = 1 << World;
        public const int HitboxMask = 1 << Hitbox;
        public const int BulletMask = WorldMask | HitboxMask;
    }
}
