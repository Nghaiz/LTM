using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Rendering
{
    /// <summary>
    /// A terrain's trees, read once, with every LOD of every prototype ready to draw by procedural
    /// instancing (<c>TreeInstancing.cginc</c>).
    /// </summary>
    /// <remarks>
    /// <b>Only what draws the same that way is taken.</b> Every prototype must be a prefab with an
    /// untransformed root and a LODGroup of two to <see cref="MaxLods"/> LODs, each LOD mesh
    /// renderers placed at the root, whose shaders have an instanced copy
    /// (<see cref="TreeShaderVariants"/>); anything else and <see cref="TryBuild"/> refuses the whole
    /// terrain, which then keeps drawing its trees itself. An instance is placed as the terrain
    /// places one: at its normalized position over the terrain, turned about Y by its rotation,
    /// scaled by its width and height scales.
    /// </remarks>
    internal sealed class TreeCatalog
    {
        /// <summary>LODs a prototype may have; <c>TreeCulling.compute</c>'s <c>MAX_LODS</c>.</summary>
        internal const int MaxLods = 8;

        /// <summary>Prototypes a terrain may have: one bit each in a cell's mask.</summary>
        internal const int MaxPrototypes = 32;

        /// <summary>The side of the square cells used to bound where each prototype stands.</summary>
        internal const float CellMetres = 128f;

        internal sealed class Part
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public ShadowCastingMode ShadowCasting;
            public bool ReceiveShadows;
            public int Layer;
            public LightProbeUsage LightProbes;
        }

        internal sealed class Prototype
        {
            public float[] Thresholds;
            public float Size;
            public Vector3 Reference;
            public Part[][] Lods;
            public int Count;
            public float LargestSquaredScale;
        }

        internal struct Cell
        {
            public Bounds Bounds;
            public uint Prototypes;
        }

        internal Prototype[] Prototypes;
        internal Cell[] Cells;
        internal Matrix4x4[] ObjectToWorld;
        internal Vector3[] References;
        internal float[] Radii;
        internal float[] SquaredScales;
        internal uint[] PrototypeOf;

        internal int Count => References.Length;

        /// <summary>The catalog of <paramref name="terrain"/>'s trees, or null and why not.</summary>
        internal static TreeCatalog TryBuild(Terrain terrain, out string reason)
        {
            TerrainData data = terrain != null ? terrain.terrainData : null;
            if (data == null) { reason = "no terrain data"; return null; }

            TreePrototype[] sources = data.treePrototypes;
            TreeInstance[] trees = data.treeInstances;
            if (sources.Length == 0 || trees.Length == 0) { reason = "no trees"; return null; }
            if (sources.Length > MaxPrototypes) { reason = $"{sources.Length} prototypes, more than {MaxPrototypes}"; return null; }

            var catalog = new TreeCatalog { Prototypes = new Prototype[sources.Length] };
            for (int i = 0; i < sources.Length; i++)
            {
                catalog.Prototypes[i] = TryReadPrototype(sources[i].prefab, out reason);
                if (catalog.Prototypes[i] == null) { reason = $"prototype {i}: {reason}"; return null; }
            }

            catalog.Place(terrain.GetPosition(), data.size, trees);
            reason = null;
            return catalog;
        }

        private static Prototype TryReadPrototype(GameObject prefab, out string reason)
        {
            if (prefab == null) { reason = "no prefab"; return null; }

            Transform root = prefab.transform;
            if (root.localPosition != Vector3.zero || root.localRotation != Quaternion.identity || root.localScale != Vector3.one)
            {
                reason = $"'{prefab.name}' has a transformed root";
                return null;
            }

            LODGroup group = prefab.GetComponent<LODGroup>();
            LOD[] lods = group != null ? group.GetLODs() : null;
            if (lods == null || lods.Length < 2 || lods.Length > MaxLods)
            {
                reason = $"'{prefab.name}' has no LODGroup of 2 to {MaxLods} LODs";
                return null;
            }

            var prototype = new Prototype
            {
                Thresholds = new float[lods.Length],
                Size = group.size,
                Reference = group.localReferencePoint,
                Lods = new Part[lods.Length][],
            };
            for (int lod = 0; lod < lods.Length; lod++)
            {
                prototype.Thresholds[lod] = lods[lod].screenRelativeTransitionHeight;
                prototype.Lods[lod] = TryReadParts(root, lods[lod].renderers, out reason);
                if (prototype.Lods[lod] == null) { reason = $"'{prefab.name}' LOD {lod}: {reason}"; return null; }
            }

            reason = null;
            return prototype;
        }

        private static Part[] TryReadParts(Transform root, Renderer[] renderers, out string reason)
        {
            var parts = new List<Part>();
            foreach (Renderer renderer in renderers)
            {
                var meshRenderer = renderer as MeshRenderer;
                MeshFilter filter = meshRenderer != null ? meshRenderer.GetComponent<MeshFilter>() : null;
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) { reason = "a renderer that is not a mesh renderer"; return null; }
                if (root.worldToLocalMatrix * meshRenderer.transform.localToWorldMatrix != Matrix4x4.identity)
                {
                    reason = "a renderer not placed at the prefab's root";
                    return null;
                }

                Material[] materials = meshRenderer.sharedMaterials;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material original = materials.Length > 0 ? materials[Mathf.Min(submesh, materials.Length - 1)] : null;
                    if (!TreeShaderVariants.TryInstanced(original, out Material material))
                    {
                        reason = $"material '{(original != null ? original.name : "none")}' has no instanced copy of its shader";
                        return null;
                    }

                    parts.Add(new Part
                    {
                        Mesh = mesh,
                        Submesh = submesh,
                        Material = material,
                        ShadowCasting = meshRenderer.shadowCastingMode,
                        ReceiveShadows = meshRenderer.receiveShadows,
                        Layer = meshRenderer.gameObject.layer,
                        LightProbes = meshRenderer.lightProbeUsage,
                    });
                }
            }

            reason = null;
            return parts.ToArray();
        }

        private void Place(Vector3 origin, Vector3 size, TreeInstance[] trees)
        {
            int columns = Mathf.Max(1, Mathf.CeilToInt(size.x / CellMetres));
            int rows = Mathf.Max(1, Mathf.CeilToInt(size.z / CellMetres));
            Cells = new Cell[columns * rows];
            var started = new bool[Cells.Length];

            ObjectToWorld = new Matrix4x4[trees.Length];
            References = new Vector3[trees.Length];
            Radii = new float[trees.Length];
            SquaredScales = new float[trees.Length];
            PrototypeOf = new uint[trees.Length];
            for (int i = 0; i < trees.Length; i++)
            {
                TreeInstance tree = trees[i];
                Prototype prototype = Prototypes[tree.prototypeIndex];
                Matrix4x4 placement = Matrix4x4.TRS(
                    origin + Vector3.Scale(tree.position, size),
                    Quaternion.AngleAxis(tree.rotation * Mathf.Rad2Deg, Vector3.up),
                    new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
                float scale = Mathf.Max(tree.widthScale, tree.heightScale);
                Vector3 reference = placement.MultiplyPoint3x4(prototype.Reference);
                float treeSize = prototype.Size * scale;

                ObjectToWorld[i] = placement;
                References[i] = reference;
                Radii[i] = treeSize * 0.5f;
                SquaredScales[i] = scale * scale;
                PrototypeOf[i] = (uint)tree.prototypeIndex;
                prototype.Count++;
                prototype.LargestSquaredScale = Mathf.Max(prototype.LargestSquaredScale, scale * scale);

                int column = Mathf.Clamp((int)(tree.position.x * columns), 0, columns - 1);
                int row = Mathf.Clamp((int)(tree.position.z * rows), 0, rows - 1);
                ref Cell cell = ref Cells[row * columns + column];
                var bounds = new Bounds(reference, new Vector3(treeSize, treeSize, treeSize));
                if (started[row * columns + column]) cell.Bounds.Encapsulate(bounds); else cell.Bounds = bounds;
                started[row * columns + column] = true;
                cell.Prototypes |= 1u << tree.prototypeIndex;
            }
        }
    }
}
