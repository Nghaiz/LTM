using UnityEngine;

namespace Ironfront.Rendering
{
    /// <summary>
    /// The procedural-instancing copies of the terrain trees' shaders that
    /// <see cref="InstancedTreeRenderer"/> draws with: <see cref="ProceduralShaderCopy.Trees"/>,
    /// which holds the rule and says why the originals are never changed.
    /// </summary>
    public static class TreeShaderVariants
    {
        /// <summary>The <c>Resources</c> folder the copies and their materials live in.</summary>
        public const string ResourceFolder = "InstancedTrees";

        /// <summary>What a copy's shader name starts with.</summary>
        public const string NamePrefix = "Hidden/Ironfront/InstancedTrees/";

        /// <summary>The <c>Resources</c> path of the copy of the shader named <paramref name="shaderName"/>.</summary>
        public static string ResourcePath(string shaderName) => ProceduralShaderCopy.Trees.ResourcePath(shaderName);

        /// <summary>The file name, without extension, of the copy of <paramref name="shaderName"/>.</summary>
        public static string FileName(string shaderName) => ProceduralShaderCopy.FileName(shaderName);

        /// <summary>
        /// The copy of <paramref name="source"/>, the text of the shader named
        /// <paramref name="shaderName"/> at <paramref name="sourcePath"/>.
        /// </summary>
        public static string Transform(string source, string shaderName, string sourcePath) =>
            ProceduralShaderCopy.Trees.Transform(source, shaderName, sourcePath);

        /// <summary>
        /// The material <see cref="InstancedTreeRenderer"/> draws <paramref name="original"/> with:
        /// a copy of it on its shader's instanced copy, or false when there is no copy.
        /// </summary>
        public static bool TryInstanced(Material original, out Material instanced) =>
            ProceduralShaderCopy.Trees.TryInstanced(original, out instanced);
    }
}
