using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GreatHeistAuto.EditorTools
{
    /// <summary>Collects boxes, quads and meshes per material; one merged mesh per material per chunk. The tint goes in the vertex colour.</summary>
    public class MeshAccum
    {
        public readonly List<Vector3> V = new();
        public readonly List<Vector3> N = new();
        public readonly List<Vector2> UV = new();
        public readonly List<Color> C = new();
        public readonly List<int> T = new();

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, Color col)
        {
            int i = V.Count;
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            V.Add(a); V.Add(b); V.Add(c); V.Add(d);
            N.Add(n); N.Add(n); N.Add(n); N.Add(n);
            UV.Add(uvA); UV.Add(uvB); UV.Add(uvC); UV.Add(uvD);
            C.Add(col); C.Add(col); C.Add(col); C.Add(col);
            T.Add(i); T.Add(i + 1); T.Add(i + 2); T.Add(i); T.Add(i + 2); T.Add(i + 3);
        }

        /// <summary>Flat rectangle facing up with UV in world metres / tile.</summary>
        public void Flat(float x0, float z0, float x1, float z1, float y, float tile, Color col)
        {
            Quad(new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0),
                new Vector2(x0, z0) / tile, new Vector2(x0, z1) / tile, new Vector2(x1, z1) / tile, new Vector2(x1, z0) / tile, col);
        }

        /// <summary>Vertical wall from a to b (seen from the side where b is to the right of a), UV tile metres.</summary>
        public void Wall(Vector3 a, Vector3 b, float y0, float y1, float uScale, float vStart, float vEnd, Color col)
        {
            var a0 = new Vector3(a.x, y0, a.z); var b0 = new Vector3(b.x, y0, b.z);
            var a1 = new Vector3(a.x, y1, a.z); var b1 = new Vector3(b.x, y1, b.z);
            Quad(a0, a1, b1, b0, new Vector2(0, vStart), new Vector2(0, vEnd), new Vector2(uScale, vEnd), new Vector2(uScale, vStart), col);
        }

        public void Add(Mesh m, Matrix4x4 local, Color col, int submesh = 0)
        {
            int start = V.Count;
            var verts = m.vertices; var norms = m.normals; var uvs = m.uv; var tris = m.GetTriangles(submesh);
            var nm = local.inverse.transpose;
            for (int i = 0; i < verts.Length; i++)
            {
                V.Add(local.MultiplyPoint3x4(verts[i]));
                N.Add(norms.Length > 0 ? nm.MultiplyVector(norms[i]).normalized : Vector3.up);
                UV.Add(uvs.Length > 0 ? uvs[i] : Vector2.zero);
                C.Add(col);
            }
            bool flip = local.determinant < 0f;
            for (int i = 0; i < tris.Length; i += 3)
            {
                T.Add(start + tris[i]);
                T.Add(start + tris[flip ? i + 2 : i + 1]);
                T.Add(start + tris[flip ? i + 1 : i + 2]);
            }
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            m.SetVertices(V); m.SetNormals(N); m.SetUVs(0, UV); m.SetColors(C); m.SetTriangles(T, 0);
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }
    }
}
