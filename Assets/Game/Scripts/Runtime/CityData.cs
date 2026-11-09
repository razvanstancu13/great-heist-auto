using System;
using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>A place to hide next to an obstacle. Normal points away from the obstacle.</summary>
    [Serializable]
    public struct CoverPoint
    {
        public Vector3 Position;
        public Vector3 Normal;
        public bool Low;          // low cover: crouch hides, standing shoots over it. Tall cover: step out sideways to shoot.
    }

    /// <summary>Everything the game needs to know about the generated city, filled in by the CityBuilder.</summary>
    public class CityData : MonoBehaviour
    {
        public static CityData Instance { get; private set; }

        public Vector3 PlayerStart;
        public float PlayerStartYaw;
        public Vector3[] EnemySpawns;
        public Vector3[] PatrolPoints;
        public CoverPoint[] Cover;
        public Vector3[] Lamps;

        readonly Dictionary<int, CharacterRig> reservations = new();

        void Awake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        public bool Reserve(int cover, CharacterRig who)
        {
            if (reservations.TryGetValue(cover, out var r) && r != null && r != who) return false;
            reservations[cover] = who;
            return true;
        }

        public void Release(int cover, CharacterRig who)
        {
            if (reservations.TryGetValue(cover, out var r) && r == who) reservations.Remove(cover);
        }

        public bool IsFree(int cover, CharacterRig who) =>
            !reservations.TryGetValue(cover, out var r) || r == null || r == who;
    }
}
