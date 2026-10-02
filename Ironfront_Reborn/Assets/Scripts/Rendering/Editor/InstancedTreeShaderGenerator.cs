using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Editor
{
    /// <summary>
    /// Writes the procedural-instancing copy of every shader a terrain tree in the project is drawn
    /// with (<see cref="TreeShaderVariants"/>), and a material on each so a build keeps its
    /// instancing variants.
    /// </summary>
    /// <remarks>
    /// Run it after changing a tree shader or adding a map whose trees use a new one;
    /// <c>InstancedTreeShaderTests</c> fails until the copies match their originals.
    /// </remarks>
    public static class InstancedTreeShaderGenerator
    {
        /// <summary>Where the copies are written: the <c>Resources</c> folder they load from.</summary>
        public const string Folder = "Assets/Scripts/Rendering/Resources/" + TreeShaderVariants.ResourceFolder;

        [MenuItem("Ironfront/Generate instanced tree shaders")]
        public static void Generate()
        {
            Directory.CreateDirectory(Folder);
            foreach (Shader shader in SourceShaders())
            {
                string path = CopyPath(shader);
                File.WriteAllText(path, Expected(shader));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                string materialPath = MaterialPath(shader);
                if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) == null)
                {
                    var keeper = new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)) { enableInstancing = true };
                    AssetDatabase.CreateAsset(keeper, materialPath);
                }
                Debug.Log($"[trees] instanced copy of '{shader.name}' written to {path}");
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>Every shader a LODGroup tree prototype of a terrain in the project is drawn with.</summary>
        public static List<Shader> SourceShaders()
        {
            var shaders = new List<Shader>();
            foreach (string guid in AssetDatabase.FindAssets("t:TerrainData"))
            {
                var data = AssetDatabase.LoadAssetAtPath<TerrainData>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (TreePrototype prototype in data.treePrototypes)
                {
                    LODGroup group = prototype.prefab != null ? prototype.prefab.GetComponent<LODGroup>() : null;
                    if (group == null) continue;
                    foreach (LOD lod in group.GetLODs())
                        foreach (Renderer renderer in lod.renderers)
                            if (renderer != null)
                                foreach (Material material in renderer.sharedMaterials)
                                    if (material != null && material.shader != null && !shaders.Contains(material.shader))
                                        shaders.Add(material.shader);
                }
            }
            return shaders;
        }

        /// <summary>The copy of <paramref name="shader"/> as it should be on disk.</summary>
        public static string Expected(Shader shader)
        {
            string source = AssetDatabase.GetAssetPath(shader);
            return TreeShaderVariants.Transform(File.ReadAllText(source), shader.name, source);
        }

        public static string CopyPath(Shader shader) => Folder + "/" + TreeShaderVariants.FileName(shader.name) + ".shader";

        public static string MaterialPath(Shader shader) => Folder + "/" + TreeShaderVariants.FileName(shader.name) + ".mat";
    }
}
