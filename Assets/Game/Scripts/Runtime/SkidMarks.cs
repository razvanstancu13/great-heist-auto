using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>Tyre marks on the road: one mesh, a ring of short strips that fade away. A strip ends where the ground steps, so none hangs over a kerb.</summary>
    public class SkidMarks : MonoBehaviour
    {
        public static SkidMarks Instance { get; private set; }

        public const float Lifetime = 14f;
        const int Capacity = 1400;
        const float MinStep = 0.22f, Lift = 0.028f;

        Vector3[] verts;
        Vector3[] normals;
        Color[] colors;
        float[] born, strength;
        int next;
        Mesh mesh;
        bool dirty;
        float nextFade;
        struct Track { public Vector3 Point, Left, Right; public float Time; }
        readonly Dictionary<long, Track> tracks = new();

        /// <summary>Strips laid since the start: for the automated test.</summary>
        public int Laid { get; private set; }
        /// <summary>Strips still showing.</summary>
        public int Showing { get; private set; }

        public void Setup(Material material)
        {
            Instance = this;
            verts = new Vector3[Capacity * 4];
            normals = new Vector3[Capacity * 4];
            colors = new Color[Capacity * 4];
            born = new float[Capacity];
            strength = new float[Capacity];
            var tris = new int[Capacity * 6];
            var uvs = new Vector2[Capacity * 4];
            for (int i = 0; i < Capacity; i++)
            {
                int v = i * 4, t = i * 6;
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v + 1; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
                uvs[v] = Vector2.zero; uvs[v + 1] = Vector2.right; uvs[v + 2] = Vector2.up; uvs[v + 3] = Vector2.one;
                born[i] = -100f;
            }
            mesh = new Mesh { name = "SkidMarks", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.MarkDynamic();
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 5000f);

            var go = new GameObject("SkidMarks");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>A tyre sliding at this point. Called every physics step while it slides; a gap in the calls starts a new mark.</summary>
        public void Add(long tyre, Vector3 point, Vector3 normal, Vector3 side, float width, float amount)
        {
            Vector3 lift = normal * Lift;
            Vector3 half = Vector3.ProjectOnPlane(side, normal).normalized * (width * 0.5f);
            Vector3 left = point - half + lift, right = point + half + lift;
            bool joined = tracks.TryGetValue(tyre, out var t) && Time.time - t.Time < 0.12f;
            if (joined)
            {
                Vector3 d = point - t.Point;
                if (d.sqrMagnitude < MinStep * MinStep) { t.Time = Time.time; tracks[tyre] = t; return; }
                if (Mathf.Abs(d.y) < 0.03f && d.sqrMagnitude < 9f)
                {
                    int v = next * 4;
                    verts[v] = t.Left; verts[v + 1] = t.Right; verts[v + 2] = left; verts[v + 3] = right;
                    for (int k = 0; k < 4; k++) normals[v + k] = normal;
                    born[next] = Time.time;
                    strength[next] = amount;
                    next = (next + 1) % Capacity;
                    Laid++;
                    dirty = true;
                }
            }
            tracks[tyre] = new Track { Point = point, Left = left, Right = right, Time = Time.time };
        }

        void LateUpdate()
        {
            if (Time.time >= nextFade)
            {
                nextFade = Time.time + 0.25f;
                int showing = 0;
                for (int i = 0; i < Capacity; i++)
                {
                    float a = strength[i] * Mathf.Clamp01(1f - (Time.time - born[i]) / Lifetime);
                    if (a > 0f) showing++;
                    var c = new Color(1f, 1f, 1f, a);
                    int v = i * 4;
                    colors[v] = c; colors[v + 1] = c; colors[v + 2] = c; colors[v + 3] = c;
                }
                Showing = showing;
                mesh.colors = colors;
            }
            if (dirty)
            {
                dirty = false;
                mesh.vertices = verts;
                mesh.normals = normals;
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 5000f);
            }
        }
    }
}
