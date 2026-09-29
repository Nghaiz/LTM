using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Tools.Weapons
{
    /// <summary>
    /// Triangles gathered from source models, moved into a target node's space and baked into one
    /// mesh with a submesh per material. The reskin's only way of producing geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why bake instead of parenting the imported model under the weapon.</b> Every weapon script
    /// reaches its model through the prefab's own nodes: <c>Weapon.FindRenderers</c> collects the
    /// renderers under <c>thirdPersonTransform</c>, <c>CullFpsObjects</c> keeps that node and the
    /// muzzle, <c>ScopedWeapon</c> hides every renderer but the scope's, and the Animator drives the
    /// magazine, bolt and slide by path. A mesh baked into the node's own space keeps all of that as
    /// it was: the node, its path, its components and its animation are untouched and only
    /// <c>MeshFilter.sharedMesh</c> and the materials change.
    /// </para>
    /// <para>
    /// Normals go through the inverse transpose and tangents keep their handedness, so a node with a
    /// non-uniform scale (the sniper's scope is 1.57 x 2.23 x 1.57) still shades correctly.
    /// </para>
    /// </remarks>
    public sealed class BakedGeometry
    {
        private readonly List<Vector3> _positions = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector4> _tangents = new List<Vector4>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly List<List<int>> _triangles = new List<List<int>>();

        public int TriangleCount => _triangles.Sum(t => t.Count) / 3;
        public bool IsEmpty => _positions.Count == 0;
        public IReadOnlyList<Material> Materials => _materials;

        /// <summary>
        /// Appends the triangles <paramref name="triangles"/> of <paramref name="mesh"/>, moved by
        /// <paramref name="toTarget"/>, drawn with <paramref name="material"/>.
        /// </summary>
        public void Add(Mesh mesh, IList<int> triangles, Matrix4x4 toTarget, Material material)
        {
            if (triangles.Count == 0) return;

            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Vector2[] uvs = mesh.uv;
            bool hasNormals = normals.Length == positions.Length;
            bool hasTangents = tangents.Length == positions.Length;
            bool hasUvs = uvs.Length == positions.Length;

            Matrix4x4 normalMatrix = toTarget.inverse.transpose;
            bool mirrored = toTarget.determinant < 0f;

            int slot = _materials.IndexOf(material);
            if (slot < 0)
            {
                slot = _materials.Count;
                _materials.Add(material);
                _triangles.Add(new List<int>());
            }
            List<int> target = _triangles[slot];

            // Only the vertices the triangles use are copied, so a region carved out of a large
            // mesh does not drag the whole vertex buffer along.
            var remap = new Dictionary<int, int>();
            int Vertex(int i)
            {
                if (remap.TryGetValue(i, out int existing)) return existing;
                int index = _positions.Count;
                _positions.Add(toTarget.MultiplyPoint3x4(positions[i]));
                _normals.Add(hasNormals ? normalMatrix.MultiplyVector(normals[i]).normalized : Vector3.up);
                if (hasTangents)
                {
                    Vector4 t = tangents[i];
                    Vector3 d = toTarget.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                    _tangents.Add(new Vector4(d.x, d.y, d.z, mirrored ? -t.w : t.w));
                }
                else _tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                _uvs.Add(hasUvs ? uvs[i] : Vector2.zero);
                remap.Add(i, index);
                return index;
            }

            for (int k = 0; k + 2 < triangles.Count; k += 3)
            {
                int a = Vertex(triangles[k]), b = Vertex(triangles[k + 1]), c = Vertex(triangles[k + 2]);
                if (mirrored) { target.Add(a); target.Add(c); target.Add(b); }
                else { target.Add(a); target.Add(b); target.Add(c); }
            }
        }

        /// <summary>
        /// Writes the geometry into <paramref name="mesh"/>, replacing whatever it held.
        /// </summary>
        /// <remarks>
        /// An existing mesh asset is rewritten through the Mesh API rather than overwritten with
        /// <c>EditorUtility.CopySerialized</c>: that keeps the asset's GUID and file ID as well, but
        /// leaves the Editor drawing the geometry the mesh had before until the next domain reload.
        /// </remarks>
        public void WriteTo(Mesh mesh, string name)
        {
            mesh.Clear();
            mesh.name = name;
            mesh.indexFormat = _positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_positions);
            mesh.SetNormals(_normals);
            mesh.SetTangents(_tangents);
            mesh.SetUVs(0, _uvs);
            mesh.subMeshCount = _triangles.Count;
            for (int s = 0; s < _triangles.Count; s++) mesh.SetTriangles(_triangles[s], s, calculateBounds: false);
            mesh.RecalculateBounds();
        }
    }

    /// <summary>One submesh of one node of a source model, placed in the model's space.</summary>
    public sealed class ModelPiece
    {
        public Mesh Mesh;
        public int SubMesh;
        public Material Material;
        public Transform Node;
        /// <summary>From the mesh's space to the model's (the imported root, with its own transform).</summary>
        public Matrix4x4 ToModel;
        /// <summary>The triangles taken from the submesh; all of them unless a region split it.</summary>
        public int[] Triangles;

        public IEnumerable<Vector3> ModelPositions()
        {
            Vector3[] v = Mesh.vertices;
            var seen = new HashSet<int>();
            foreach (int i in Triangles)
                if (seen.Add(i)) yield return ToModel.MultiplyPoint3x4(v[i]);
        }

        public static List<ModelPiece> Gather(GameObject model)
        {
            var pieces = new List<ModelPiece>();
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (mesh == null || renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    pieces.Add(new ModelPiece
                    {
                        Mesh = mesh,
                        SubMesh = s,
                        Material = s < materials.Length ? materials[s] : null,
                        Node = filter.transform,
                        ToModel = filter.transform.localToWorldMatrix,
                        Triangles = mesh.GetTriangles(s),
                    });
                }
            }
            return pieces;
        }

        /// <summary>
        /// Splits the piece's triangles into the connected parts whose centre lies inside
        /// <paramref name="modelBox"/> and the rest.
        /// </summary>
        /// <remarks>
        /// A single-mesh model (the SIG 716 is one object) still keeps its magazine as its own
        /// connected shell. Connectivity is by welded position, not index, since an importer splits
        /// vertices along every UV seam and hard edge.
        /// </remarks>
        public (ModelPiece inside, ModelPiece outside) SplitByComponents(Bounds modelBox)
        {
            Vector3[] v = Mesh.vertices;
            var weld = new Dictionary<Vector3Int, int>();
            var weldOf = new Dictionary<int, int>();
            const float Quantum = 1e-5f;
            int Weld(int i)
            {
                if (weldOf.TryGetValue(i, out int w)) return w;
                Vector3 p = v[i] / Quantum;
                var key = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (!weld.TryGetValue(key, out w)) { w = weld.Count; weld.Add(key, w); }
                weldOf.Add(i, w);
                return w;
            }

            int triangleCount = Triangles.Length / 3;
            var parent = new List<int>();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[y] = x; }
            var triWelds = new int[Triangles.Length];
            for (int k = 0; k < Triangles.Length; k++)
            {
                triWelds[k] = Weld(Triangles[k]);
                while (parent.Count <= triWelds[k]) parent.Add(parent.Count);
            }
            for (int t = 0; t < triangleCount; t++)
            {
                Union(triWelds[3 * t], triWelds[3 * t + 1]);
                Union(triWelds[3 * t], triWelds[3 * t + 2]);
            }

            var bounds = new Dictionary<int, Bounds>();
            for (int k = 0; k < Triangles.Length; k++)
            {
                int root = Find(triWelds[k]);
                Vector3 p = ToModel.MultiplyPoint3x4(v[Triangles[k]]);
                if (bounds.TryGetValue(root, out Bounds b)) { b.Encapsulate(p); bounds[root] = b; }
                else bounds[root] = new Bounds(p, Vector3.zero);
            }

            var inside = new List<int>();
            var outside = new List<int>();
            for (int t = 0; t < triangleCount; t++)
            {
                int root = Find(triWelds[3 * t]);
                List<int> into = modelBox.Contains(bounds[root].center) ? inside : outside;
                into.Add(Triangles[3 * t]); into.Add(Triangles[3 * t + 1]); into.Add(Triangles[3 * t + 2]);
            }

            return (With(inside.ToArray()), With(outside.ToArray()));
        }

        private ModelPiece With(int[] triangles) => new ModelPiece
        {
            Mesh = Mesh, SubMesh = SubMesh, Material = Material, Node = Node, ToModel = ToModel, Triangles = triangles,
        };
    }

    /// <summary>A signed axis, for saying which way a model's barrel and top face.</summary>
    public enum Axis { PosX, NegX, PosY, NegY, PosZ, NegZ }

    public static class AxisMath
    {
        public static Vector3 Vector(Axis axis)
        {
            switch (axis)
            {
                case Axis.PosX: return Vector3.right;
                case Axis.NegX: return Vector3.left;
                case Axis.PosY: return Vector3.up;
                case Axis.NegY: return Vector3.down;
                case Axis.PosZ: return Vector3.forward;
                default: return Vector3.back;
            }
        }

        /// <summary>
        /// The rotation taking a model whose barrel is <paramref name="modelForward"/> and whose top
        /// is <paramref name="modelUp"/> to a frame where they are <paramref name="targetForward"/>
        /// and <paramref name="targetUp"/>. Always a proper rotation: nothing is mirrored.
        /// </summary>
        public static Quaternion Frame(Vector3 modelForward, Vector3 modelUp, Vector3 targetForward, Vector3 targetUp)
        {
            Quaternion model = Quaternion.LookRotation(modelForward, modelUp);
            Quaternion target = Quaternion.LookRotation(targetForward, targetUp);
            return target * Quaternion.Inverse(model);
        }
    }
}
