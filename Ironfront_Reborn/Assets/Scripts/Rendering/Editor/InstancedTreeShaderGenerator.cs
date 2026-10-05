using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Editor
{
    /// <summary>
    /// Writes the procedural-instancing copy of every shader a terrain tree or a GPU-drawn terrain
    /// detail in the project is drawn with (<see cref="ProceduralShaderCopy"/>), and a material on
    /// each so a build keeps its instancing variants.
    /// </summary>
    /// <remarks>
    /// Run it after changing a tree or detail shader or adding a map whose trees or details use a
    /// new one; <c>InstancedTreeShaderTests</c> and <c>InstancedDetailShaderTests</c> fail until the
    /// copies match their originals. A detail shader with no source in the project (the built-in
    /// <c>Standard</c>) is not generated: its copy in <c>Resources/InstancedDetails</c> is written
    /// by hand, and a detail material with no copy at all keeps its terrain drawing its details.
    /// </remarks>
    public static class InstancedTreeShaderGenerator
    {
        /// <summary>Where the tree copies are written: the <c>Resources</c> folder they load from.</summary>
        public const string Folder = ResourcesRoot + TreeShaderVariants.ResourceFolder;

        private const string ResourcesRoot = "Assets/Scripts/Rendering/Resources/";

        [MenuItem("Ironfront/Generate instanced tree shaders")]
        public static void Generate() => Write(ProceduralShaderCopy.Trees, SourceShaders());

        [MenuItem("Ironfront/Generate instanced detail shaders")]
        public static void GenerateDetails() => Write(ProceduralShaderCopy.Details, DetailSourceShaders());

        /// <summary>Every shader a LODGroup tree prototype of a terrain in the project is drawn with.</summary>
        public static List<Shader> SourceShaders()
        {
            var shaders = new List<Shader>();
            foreach (TerrainData data in TerrainDatas())
                foreach (TreePrototype prototype in data.treePrototypes)
                {
                    LODGroup group = prototype.prefab != null ? prototype.prefab.GetComponent<LODGroup>() : null;
                    if (group == null) continue;
                    foreach (LOD lod in group.GetLODs())
                        foreach (Renderer renderer in lod.renderers)
                            if (renderer != null)
                                Add(shaders, renderer.sharedMaterials);
                }
            return shaders;
        }

        /// <summary>
        /// Every shader with its source in the project that an instanced mesh detail prototype of a
        /// terrain is drawn with.
        /// </summary>
        public static List<Shader> DetailSourceShaders()
        {
            var shaders = new List<Shader>();
            foreach (TerrainData data in TerrainDatas())
                foreach (DetailPrototype prototype in data.detailPrototypes)
                {
                    if (!prototype.usePrototypeMesh || !prototype.useInstancing || prototype.prototype == null) continue;
                    var renderer = prototype.prototype.GetComponent<MeshRenderer>();
                    if (renderer != null) Add(shaders, renderer.sharedMaterials);
                }
            shaders.RemoveAll(shader => !AssetDatabase.GetAssetPath(shader).StartsWith("Assets/", System.StringComparison.Ordinal));
            return shaders;
        }

        /// <summary>The tree copy of <paramref name="shader"/> as it should be on disk.</summary>
        public static string Expected(Shader shader) => Expected(ProceduralShaderCopy.Trees, shader);

        /// <summary>The copy of <paramref name="shader"/> in <paramref name="copies"/> as it should be on disk.</summary>
        public static string Expected(ProceduralShaderCopy copies, Shader shader)
        {
            string source = AssetDatabase.GetAssetPath(shader);
            return copies.Transform(File.ReadAllText(source), shader.name, source);
        }

        public static string CopyPath(Shader shader) => CopyPath(ProceduralShaderCopy.Trees, shader);

        public static string MaterialPath(Shader shader) => MaterialPath(ProceduralShaderCopy.Trees, shader);

        public static string CopyPath(ProceduralShaderCopy copies, Shader shader) => FolderOf(copies) + "/" + ProceduralShaderCopy.FileName(shader.name) + ".shader";

        public static string MaterialPath(ProceduralShaderCopy copies, Shader shader) => FolderOf(copies) + "/" + ProceduralShaderCopy.FileName(shader.name) + ".mat";

        private static string FolderOf(ProceduralShaderCopy copies) => ResourcesRoot + copies.ResourceFolder;

        private static void Write(ProceduralShaderCopy copies, List<Shader> shaders)
        {
            Directory.CreateDirectory(FolderOf(copies));
            foreach (Shader shader in shaders)
            {
                string path = CopyPath(copies, shader);
                File.WriteAllText(path, Expected(copies, shader));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                string materialPath = MaterialPath(copies, shader);
                if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) == null)
                {
                    var keeper = new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)) { enableInstancing = true };
                    AssetDatabase.CreateAsset(keeper, materialPath);
                }
                Debug.Log($"[instancing] procedural copy of '{shader.name}' written to {path}");
            }
            AssetDatabase.SaveAssets();
        }

        private static IEnumerable<TerrainData> TerrainDatas()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:TerrainData"))
                yield return AssetDatabase.LoadAssetAtPath<TerrainData>(AssetDatabase.GUIDToAssetPath(guid));
        }

        private static void Add(List<Shader> shaders, Material[] materials)
        {
            foreach (Material material in materials)
                if (material != null && material.shader != null && !shaders.Contains(material.shader))
                    shaders.Add(material.shader);
        }
    }
}
