using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>What the player wants this frame: read from the devices, or written by the automated test.</summary>
    public struct PlayerCommand
    {
        public Vector2 Move;       // x right, y forward, -1..1
        public Vector2 Look;       // degrees this frame
        public bool Fire, FirePressed, Ads, Jump, JumpHeld, Sprint, Reload, Interact, Honk, ToggleLights;
        public bool CrouchHeld, Orbit;
        public int SelectSlot;     // -1 = keep, -2 = put the gun away
        public int CycleSlot;      // -1, 0, +1
    }
}
