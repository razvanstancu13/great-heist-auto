using System;
using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>
    /// The road system as data: intersections, right-hand-traffic lanes between them, and turn connectors across each
    /// intersection. Nothing drives on it yet; a car only needs GetNearestLane, then to follow Lane.Points and pick from
    /// NextConnectors at the end. The automated playthrough (AutoTest) checks that every lane leads somewhere and
    /// that every lane can be reached from the first one.
    /// </summary>
    public class RoadNetwork : MonoBehaviour
    {
        [Serializable]
        public class Lane
        {
            public int FromNode, ToNode;
            public Vector3[] Points;
            public float SpeedLimit = 12f;
            public int[] NextConnectors = Array.Empty<int>();
        }

        [Serializable]
        public class Connector
        {
            public int FromLane, ToLane, Node;
            public Vector3[] Points;
            public int Turn;      // -1 left, 0 straight, +1 right
        }

        public Vector3[] Nodes = Array.Empty<Vector3>();
        public Lane[] Lanes = Array.Empty<Lane>();
        public Connector[] Connectors = Array.Empty<Connector>();
        public Vector3[] Crossings = Array.Empty<Vector3>();

        /// <summary>Traffic slows here: every intersection, with its crosswalks, and every mid-block zebra crossing.</summary>
        public bool InSlowZone(Vector3 pos, float reach)
        {
            float r2 = reach * reach;
            foreach (var n in Nodes) if ((n - pos).sqrMagnitude < r2) return true;
            foreach (var c in Crossings) if ((c - pos).sqrMagnitude < r2) return true;
            return false;
        }

        /// <summary>The lane closest to a position, and the point index on it.</summary>
        public int GetNearestLane(Vector3 pos, out int pointIndex)
        {
            int best = -1; pointIndex = 0;
            float bestD = float.MaxValue;
            for (int l = 0; l < Lanes.Length; l++)
            {
                var pts = Lanes[l].Points;
                for (int p = 0; p < pts.Length; p++)
                {
                    float d = (pts[p] - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = l; pointIndex = p; }
                }
            }
            return best;
        }

        /// <summary>Breadth-first over lanes: how many lanes can be reached from the given one.</summary>
        public int CountReachable(int startLane)
        {
            var seen = new HashSet<int> { startLane };
            var queue = new Queue<int>();
            queue.Enqueue(startLane);
            while (queue.Count > 0)
            {
                foreach (int c in Lanes[queue.Dequeue()].NextConnectors)
                    if (seen.Add(Connectors[c].ToLane)) queue.Enqueue(Connectors[c].ToLane);
            }
            return seen.Count;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            foreach (var l in Lanes)
                for (int i = 1; i < l.Points.Length; i++) Gizmos.DrawLine(l.Points[i - 1] + Vector3.up * .2f, l.Points[i] + Vector3.up * .2f);
            Gizmos.color = Color.yellow;
            foreach (var c in Connectors)
                for (int i = 1; i < c.Points.Length; i++) Gizmos.DrawLine(c.Points[i - 1] + Vector3.up * .2f, c.Points[i] + Vector3.up * .2f);
        }
#endif
    }
}
