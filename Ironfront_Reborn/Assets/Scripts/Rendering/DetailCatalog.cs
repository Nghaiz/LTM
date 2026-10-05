using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Rendering
{
    /// <summary>
    /// A terrain's detail prototypes, read once, each ready to draw the way
    /// <see cref="InstancedDetailRenderer"/> draws it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only what draws the same this way is taken.</b> Every prototype must be an instanced mesh
    /// detail (<c>usePrototypeMesh</c> and <c>useInstancing</c>) whose prefab root is untransformed
    /// and carries the mesh and its renderer, not aligned to the ground (the terrain tilts those by
    /// its normal, which <c>DetailInstanceTransform</c> does not carry), and every material must
    /// have a procedural copy of its shader (<see cref="ProceduralShaderCopy.Details"/>) declaring
    /// every keyword the material uses: Forest Lake's grass, ferns, blueberries and flowers on the
    /// generated copy of <c>M_Foliage_Wind</c>, its rocks on the hand-written copy of
    /// <c>Standard</c>. Anything else and <see cref="TryBuild"/> refuses the whole terrain, which
    /// then keeps drawing its details itself: its detail distance is the terrain's, not a
    /// prototype's, so it cannot keep some and hand over the rest. Island and Dustbowl (texture
    /// grass) are refused this way.
    /// </para>
    /// <para>
    /// Drawing a prototype without a copy on its own material instead was tried and dropped: that
    /// material's additive pass lights it with every point light near it, which the terrain never
    /// does (<see cref="ProceduralShaderCopy"/>), at a draw call a light.
    /// </para>
    /// </remarks>
    internal sealed class DetailCatalog
    {
        /// <summary>Prototypes a terrain may have.</summary>
        internal const int MaxPrototypes = 32;

        /// <summary>How far above its authored range Unity's noise takes a detail's scale (measured 1.04x).</summary>
        internal const float ScaleOvershoot = 1.25f;

        internal sealed class Part
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
        }

        internal sealed class Prototype
        {
            public int Layer;
            public string Name;
            public Part[] Parts;
            public ShadowCastingMode ShadowCasting;
            public bool ReceiveShadows;
            public int GameObjectLayer;
            public LightProbeUsage LightProbes;
            public ReflectionProbeUsage ReflectionProbes;
            public uint RenderingLayerMask;

            /// <summary>The radius, about the pivot, that holds the mesh at a scale of one.</summary>
            public float Radius;

            /// <summary>The height of the mesh's top at a height scale of one.</summary>
            public float Height;

            /// <summary>The largest scale the prototype's ranges allow, with Unity's noise overshoot.</summary>
            public float MaxScale;

            public bool ScalesWithDensity;

            public bool Casts => ShadowCasting != ShadowCastingMode.Off;
        }

        internal Prototype[] Prototypes;

        /// <summary>The farthest any detail reaches past its patch's square, in metres.</summary>
        internal float Reach;

        /// <summary>The catalog of <paramref name="terrain"/>'s details, or null and why not.</summary>
        internal static DetailCatalog TryBuild(Terrain terrain, out string reason)
        {
            TerrainData data = terrain != null ? terrain.terrainData : null;
            if (data == null) { reason = "no terrain data"; return null; }

            DetailPrototype[] sources = data.detailPrototypes;
            if (sources.Length == 0) { reason = "no detail prototypes"; return null; }
            if (sources.Length > MaxPrototypes) { reason = $"{sources.Length} detail prototypes, more than {MaxPrototypes}"; return null; }

            var catalog = new DetailCatalog { Prototypes = new Prototype[sources.Length] };
            for (int i = 0; i < sources.Length; i++)
            {
                Prototype prototype = TryReadPrototype(sources[i], i, out reason);
                if (prototype == null) { reason = $"detail prototype {i}: {reason}"; return null; }
                catalog.Prototypes[i] = prototype;
                catalog.Reach = Mathf.Max(catalog.Reach, prototype.Radius * prototype.MaxScale);
            }

            reason = null;
            return catalog;
        }

        internal static Prototype TryReadPrototype(DetailPrototype source, int layer, out string reason)
        {
            if (!source.usePrototypeMesh) { reason = "a texture detail"; return null; }
            if (!source.useInstancing) { reason = "a mesh detail drawn without instancing"; return null; }
            if (source.alignToGround > 0f) { reason = "aligned to the ground"; return null; }

            GameObject prefab = source.prototype;
            if (prefab == null) { reason = "no prefab"; return null; }
            Transform root = prefab.transform;
            if (root.localPosition != Vector3.zero || root.localRotation != Quaternion.identity || root.localScale != Vector3.one)
            {
                reason = $"'{prefab.name}' has a transformed root";
                return null;
            }

            var renderer = prefab.GetComponent<MeshRenderer>();
            MeshFilter filter = prefab.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (renderer == null || mesh == null) { reason = $"'{prefab.name}' has no mesh and mesh renderer on its root"; return null; }

            Part[] parts = TryReadParts(mesh, renderer.sharedMaterials, out reason);
            if (parts == null) { reason = $"'{prefab.name}': {reason}"; return null; }

            Bounds bounds = mesh.bounds;
            reason = null;
            return new Prototype
            {
                Layer = layer,
                Name = prefab.name,
                Parts = parts,
                ShadowCasting = renderer.shadowCastingMode,
                ReceiveShadows = renderer.receiveShadows,
                GameObjectLayer = prefab.layer,
                LightProbes = renderer.lightProbeUsage,
                ReflectionProbes = renderer.reflectionProbeUsage,
                RenderingLayerMask = renderer.renderingLayerMask,
                Radius = bounds.center.magnitude + bounds.extents.magnitude,
                Height = Mathf.Max(0f, bounds.max.y),
                MaxScale = Mathf.Max(source.maxWidth, source.maxHeight) * ScaleOvershoot,
                ScalesWithDensity = source.useDensityScaling,
            };
        }

        /// <summary>One part a submesh, each on its material's procedural copy; null when one has none.</summary>
        private static Part[] TryReadParts(Mesh mesh, Material[] materials, out string reason)
        {
            var parts = new List<Part>();
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                Material original = materials.Length > 0 ? materials[Mathf.Min(submesh, materials.Length - 1)] : null;
                if (original == null || original.shader == null)
                {
                    reason = $"submesh {submesh} has no material";
                    return null;
                }
                if (!ProceduralShaderCopy.Details.TryInstanced(original, out Material copy))
                {
                    reason = $"material '{original.name}' has no procedural copy of '{original.shader.name}'";
                    return null;
                }
                if (!Declares(copy.shader, original))
                {
                    reason = $"material '{original.name}' uses a keyword the copy of '{original.shader.name}' does not declare";
                    return null;
                }
                parts.Add(new Part { Mesh = mesh, Submesh = submesh, Material = copy });
            }
            reason = null;
            return parts.ToArray();
        }

        /// <summary>
        /// Whether <paramref name="copy"/> declares every keyword <paramref name="original"/> has
        /// switched on: a copy written by hand (<c>InstancedDetails/Standard</c>) carries only the
        /// features the maps use, and a material using another would draw without it.
        /// </summary>
        internal static bool Declares(Shader copy, Material original)
        {
            foreach (LocalKeyword keyword in original.enabledKeywords)
                if (!copy.keywordSpace.FindKeyword(keyword.name).isValid) return false;
            return true;
        }
    }
}
