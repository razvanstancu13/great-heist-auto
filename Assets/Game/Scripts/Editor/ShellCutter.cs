using System.Collections.Generic;
using UnityEngine;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>
    /// Cuts pieces out of a car's single-mesh shell along clean edges: triangles are clipped against a convex outline seen from the side
    /// (in z and y), so holes are round and door edges are straight, whatever the triangles of the generated mesh happen to be.
    /// </summary>
    public class ShellCutter
    {
        public struct Vert { public Vector3 P, N; public Vector2 UV; public Color C; }

        List<Vert[]> tris = new();

        public ShellCutter(Mesh body)
        {
            var v = body.vertices; var n = body.normals; var uv = body.uv; var c = body.colors; var t = body.triangles;
            bool hasColor = c != null && c.Length == v.Length;
            Vert At(int i) => new Vert { P = v[i], N = n[i], UV = uv[i], C = hasColor ? c[i] : Color.white };
            for (int i = 0; i < t.Length; i += 3) tris.Add(new[] { At(t[i]), At(t[i + 1]), At(t[i + 2]) });
        }

        public int TriangleCount => tris.Count;

        static Vert Mix(Vert a, Vert b, float k) => new Vert
        {
            P = Vector3.Lerp(a.P, b.P, k), N = Vector3.Slerp(a.N, b.N, k).normalized, UV = Vector2.Lerp(a.UV, b.UV, k), C = Color.Lerp(a.C, b.C, k)
        };

        /// <summary>Takes everything inside the outline (points in z, y, counter-clockwise or clockwise) from triangles that pass the filter; returns it as polygons.</summary>
        public List<Vert[]> Take(Vector2[] outline, System.Func<Vert[], bool> filter)
        {
            float area = 0f;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
                area += a.x * b.y - b.x * a.y;
            }
            float sign = Mathf.Sign(area);
            var taken = new List<Vert[]>();
            var kept = new List<Vert[]>(tris.Count);
            foreach (var tri in tris)
            {
                if (!filter(tri)) { kept.Add(tri); continue; }
                var inside = new List<Vert>(tri);
                for (int e = 0; e < outline.Length && inside.Count >= 3; e++)
                {
                    Vector2 a = outline[e], b = outline[(e + 1) % outline.Length];
                    Vector2 edge = b - a;
                    float Side(Vert q) => sign * (edge.x * (q.P.y - a.y) - edge.y * (q.P.z - a.x));
                    var inPart = new List<Vert>(); var outPart = new List<Vert>();
                    for (int i = 0; i < inside.Count; i++)
                    {
                        Vert p = inside[i], q = inside[(i + 1) % inside.Count];
                        float sp = Side(p), sq = Side(q);
                        if (sp >= 0f) inPart.Add(p);
                        if (sp <= 0f) outPart.Add(p);
                        if ((sp > 0f && sq < 0f) || (sp < 0f && sq > 0f))
                        {
                            Vert m = Mix(p, q, sp / (sp - sq));
                            inPart.Add(m); outPart.Add(m);
                        }
                    }
                    if (outPart.Count >= 3) AddFan(kept, outPart);
                    inside = inPart;
                }
                if (inside.Count >= 3) taken.Add(inside.ToArray());
            }
            tris = kept;
            return taken;
        }

        static void AddFan(List<Vert[]> to, List<Vert> poly)
        {
            for (int i = 1; i < poly.Count - 1; i++) to.Add(new[] { poly[0], poly[i], poly[i + 1] });
        }

        /// <summary>What is left of the shell.</summary>
        public Mesh Remainder(string name)
        {
            var a = new MeshAccum();
            foreach (var t in tris) Emit(a, t, Vector3.zero);
            return a.Build(name);
        }

        /// <summary>Polygons as a mesh of their own, with the origin moved to 'origin'.</summary>
        public static Mesh ToMesh(List<Vert[]> polys, Vector3 origin, string name, Vector3 lining)
        {
            var a = new MeshAccum();
            var fan = new List<Vert[]>();
            foreach (var p in polys) AddFan(fan, new List<Vert>(p));
            foreach (var t in fan) Emit(a, t, origin);
            // The inside of the door: the same shape, set in and turned round, dark, so an open door is a solid thing from behind too.
            Color dark = new(.06f, .06f, .065f);
            foreach (var t in fan)
            {
                var back = new Vert[3];
                for (int i = 0; i < 3; i++)
                {
                    back[2 - i] = t[i];
                    back[2 - i].P += lining;
                    back[2 - i].N = -t[i].N;
                    back[2 - i].C = dark;
                }
                Emit(a, back, origin);
            }
            return a.Build(name);
        }

        static void Emit(MeshAccum a, Vert[] t, Vector3 origin)
        {
            int s = a.V.Count;
            foreach (var q in t)
            {
                a.V.Add(q.P - origin); a.N.Add(q.N); a.UV.Add(q.UV); a.C.Add(q.C);
            }
            a.T.Add(s); a.T.Add(s + 1); a.T.Add(s + 2);
        }

        /// <summary>A round outline in z, y.</summary>
        public static Vector2[] Circle(float z, float y, float radius, int sides = 28)
        {
            var o = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                o[i] = new Vector2(z + Mathf.Cos(a) * radius, y + Mathf.Sin(a) * radius);
            }
            return o;
        }

        public static Vector2[] Rect(float z0, float y0, float z1, float y1) =>
            new[] { new Vector2(z0, y0), new Vector2(z1, y0), new Vector2(z1, y1), new Vector2(z0, y1) };
    }
}
