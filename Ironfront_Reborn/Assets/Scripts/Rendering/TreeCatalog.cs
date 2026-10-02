using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Rendering
{
    /// <summary>
    /// A terrain's trees, read once and kept by cell, with every LOD of every prototype ready to
    /// draw instanced.
    /// </summary>
    /// <remarks>
    /// <b>Only what draws the same instanced is taken.</b> Every prototype must be a prefab with an
    /// untransformed root and a LODGroup of two or more LODs, each LOD mesh renderers whose
    /// materials instance; anything else and <see cref="TryBuild"/> refuses the whole terrain, which
    /// then keeps drawing its trees itself. An instance is placed as the terrain places one: at its
    /// normalized position over the terrain, turned about Y by its rotation, scaled by its width and
    /// height scales.
    /// </remarks>
    internal sealed class TreeCatalog
    {
        /// <summary>The side of the square cells trees are kept, culled and shadowed in.</summary>
        internal const float CellMetres = 128f;

        internal sealed class Part
        {
            public Mesh Mesh;
            public int Submesh;
            public Matrix4x4 Local;
            public bool IdentityLocal;
            public RenderParams Shadowed;
            public RenderParams Unshadowed;
        }

        internal sealed class Prototype
        {
            public float[] Thresholds;
            public float Size;
            public Vector3 Reference;
            public Part[][] Lods;

            /// <summary>Per frame: how far, squared, each LOD lasts for a scale of one.</summary>
            public float[] SquaredDistances;
        }

        internal struct Cell
        {
            public Bounds Bounds;
            public int Start;
            public int Count;
        }

        internal Prototype[] Prototypes;
        internal Cell[] Cells;
        internal Matrix4x4[] Matrices;
        internal Vector3[] References;
        internal float[] Sizes;
        internal float[] SquaredScales;
        internal int[] PrototypeOf;

        /// <summary>The largest LOD count of any prototype.</summary>
        internal int MaxLods;

        internal int Count => References.Length;

        /// <summary>The catalog of <paramref name="terrain"/>'s trees, or null and why not.</summary>
        internal static TreeCatalog TryBuild(Terrain terrain, out string reason)
        {
            TerrainData data = terrain != null ? terrain.terrainData : null;
            if (data == null) { reason = "no terrain data"; return null; }

            TreePrototype[] sources = data.treePrototypes;
            TreeInstance[] trees = data.treeInstances;
            if (sources.Length == 0 || trees.Length == 0) { reason = "no trees"; return null; }

            var catalog = new TreeCatalog { Prototypes = new Prototype[sources.Length] };
            for (int i = 0; i < sources.Length; i++)
            {
                catalog.Prototypes[i] = TryReadPrototype(sources[i].prefab, out reason);
                if (catalog.Prototypes[i] == null) { reason = $"prototype {i}: {reason}"; return null; }
                catalog.MaxLods = Mathf.Max(catalog.MaxLods, catalog.Prototypes[i].Lods.Length);
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
            if (lods == null || lods.Length < 2) { reason = $"'{prefab.name}' has no LODGroup of two or more LODs"; return null; }

            var prototype = new Prototype
            {
                Thresholds = new float[lods.Length],
                Size = group.size,
                Reference = group.localReferencePoint,
                Lods = new Part[lods.Length][],
                SquaredDistances = new float[lods.Length],
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

                Material[] materials = meshRenderer.sharedMaterials;
                Matrix4x4 local = root.worldToLocalMatrix * meshRenderer.transform.localToWorldMatrix;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = materials.Length > 0 ? materials[Mathf.Min(submesh, materials.Length - 1)] : null;
                    if (material == null || !material.enableInstancing) { reason = "a material that does not instance"; return null; }

                    var shadowed = new RenderParams(material)
                    {
                        layer = meshRenderer.gameObject.layer,
                        renderingLayerMask = meshRenderer.renderingLayerMask,
                        shadowCastingMode = meshRenderer.shadowCastingMode,
                        receiveShadows = meshRenderer.receiveShadows,
                        lightProbeUsage = meshRenderer.lightProbeUsage,
                        reflectionProbeUsage = meshRenderer.reflectionProbeUsage,
                        motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
                    };
                    RenderParams unshadowed = shadowed;
                    unshadowed.shadowCastingMode = ShadowCastingMode.Off;
                    parts.Add(new Part
                    {
                        Mesh = mesh,
                        Submesh = submesh,
                        Local = local,
                        IdentityLocal = local == Matrix4x4.identity,
                        Shadowed = shadowed,
                        Unshadowed = unshadowed,
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
            var cellOf = new int[trees.Length];
            var counts = new int[columns * rows];
            for (int i = 0; i < trees.Length; i++)
            {
                int column = Mathf.Clamp((int)(trees[i].position.x * columns), 0, columns - 1);
                int row = Mathf.Clamp((int)(trees[i].position.z * rows), 0, rows - 1);
                cellOf[i] = row * columns + column;
                counts[cellOf[i]]++;
            }

            Cells = new Cell[counts.Length];
            for (int c = 0, start = 0; c < counts.Length; start += counts[c], c++)
                Cells[c].Start = start;

            Matrices = new Matrix4x4[trees.Length];
            References = new Vector3[trees.Length];
            Sizes = new float[trees.Length];
            SquaredScales = new float[trees.Length];
            PrototypeOf = new int[trees.Length];
            for (int i = 0; i < trees.Length; i++)
            {
                TreeInstance tree = trees[i];
                Prototype prototype = Prototypes[tree.prototypeIndex];
                Matrix4x4 placement = Matrix4x4.TRS(
                    origin + Vector3.Scale(tree.position, size),
                    Quaternion.AngleAxis(tree.rotation * Mathf.Rad2Deg, Vector3.up),
                    new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
                float scale = Mathf.Max(tree.widthScale, tree.heightScale);

                ref Cell cell = ref Cells[cellOf[i]];
                int slot = cell.Start + cell.Count;
                Vector3 reference = placement.MultiplyPoint3x4(prototype.Reference);
                float treeSize = prototype.Size * scale;
                var bounds = new Bounds(reference, new Vector3(treeSize, treeSize, treeSize));
                if (cell.Count == 0) cell.Bounds = bounds; else cell.Bounds.Encapsulate(bounds);
                cell.Count++;

                Matrices[slot] = placement;
                References[slot] = reference;
                Sizes[slot] = treeSize;
                SquaredScales[slot] = scale * scale;
                PrototypeOf[slot] = tree.prototypeIndex;
            }
        }
    }
}
