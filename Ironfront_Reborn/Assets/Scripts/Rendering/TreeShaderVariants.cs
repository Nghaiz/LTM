using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Ironfront.Rendering
{
    /// <summary>
    /// The procedural-instancing copies of the terrain trees' shaders that
    /// <see cref="InstancedTreeRenderer"/> draws with, and the rule that makes one from its original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Copies, never the originals.</b> Declaring <c>#pragma instancing_options procedural</c> on
    /// a tree shader itself changed how the terrain drew it: Forest Lake's bark and needles went pink
    /// with the terrain still drawing them (A/B screenshots, 2026-10-02). So each original is left as
    /// it is, and the GPU draws use a hidden copy, made by <see cref="Transform"/> and kept in
    /// <c>Resources/InstancedTrees</c> by the editor's generator, with a material of its own there so
    /// a build keeps its instancing variants.
    /// </para>
    /// <para>
    /// <b>What the rule changes:</b> the name, under <see cref="NamePrefix"/>; the procedural
    /// instancing lines after the first <c>#pragma target</c>; <c>shader_feature_local</c> to
    /// <c>multi_compile_local</c>, because no material in a build uses the copy with its keywords
    /// to keep their variants; and the Amplify graph, which would regenerate the original.
    /// </para>
    /// </remarks>
    public static class TreeShaderVariants
    {
        /// <summary>The <c>Resources</c> folder the copies and their materials live in.</summary>
        public const string ResourceFolder = "InstancedTrees";

        /// <summary>What a copy's shader name starts with.</summary>
        public const string NamePrefix = "Hidden/Ironfront/InstancedTrees/";

        private static readonly Dictionary<Material, Material> Instanced = new Dictionary<Material, Material>();

        /// <summary>The <c>Resources</c> path of the copy of the shader named <paramref name="shaderName"/>.</summary>
        public static string ResourcePath(string shaderName) => ResourceFolder + "/" + FileName(shaderName);

        /// <summary>The file name, without extension, of the copy of <paramref name="shaderName"/>.</summary>
        public static string FileName(string shaderName) => shaderName.Replace('/', '_').Replace(' ', '_');

        /// <summary>
        /// The copy of <paramref name="source"/>, the text of the shader named
        /// <paramref name="shaderName"/> at <paramref name="sourcePath"/>.
        /// </summary>
        public static string Transform(string source, string shaderName, string sourcePath)
        {
            string text = source.Replace("\r\n", "\n");
            text = Regex.Replace(text, @"/\*ASEBEGIN.*?ASEEND\*/\n?", string.Empty, RegexOptions.Singleline);
            text = Regex.Replace(text, @"^// Made with Amplify Shader Editor.*\n(// Available at.*\n)?", string.Empty, RegexOptions.Multiline);

            string quoted = "Shader \"" + shaderName + "\"";
            int name = text.IndexOf(quoted, System.StringComparison.Ordinal);
            if (name < 0) throw new System.ArgumentException($"'{sourcePath}' does not declare {quoted}");
            text = text.Substring(0, name) + "Shader \"" + NamePrefix + shaderName + "\"" + text.Substring(name + quoted.Length);

            Match target = Regex.Match(text, @"^([ \t]*)#pragma target [^\n]*\n", RegexOptions.Multiline);
            if (!target.Success) throw new System.ArgumentException($"'{sourcePath}' has no #pragma target");
            string indent = target.Groups[1].Value;
            string lines = indent + "#include \"Assets/Scripts/Rendering/TreeInstancing.cginc\"\n"
                           + indent + "#pragma instancing_options procedural:TreeInstancingSetup\n";
            text = text.Insert(target.Index + target.Length, lines);
            text = text.Replace("#pragma shader_feature_local ", "#pragma multi_compile_local _ ");

            return "// Generated from " + sourcePath + " by Ironfront > Generate instanced tree shaders.\n"
                   + "// Do not edit: change the original and generate again (TreeShaderVariants.Transform).\n"
                   + text;
        }

        /// <summary>
        /// The material <see cref="InstancedTreeRenderer"/> draws <paramref name="original"/> with:
        /// a copy of it on its shader's instanced copy, or false when there is no copy.
        /// </summary>
        public static bool TryInstanced(Material original, out Material instanced)
        {
            if (original == null || original.shader == null) { instanced = null; return false; }
            if (Instanced.TryGetValue(original, out instanced) && instanced != null) return true;

            var shader = Resources.Load<Shader>(ResourcePath(original.shader.name));
            if (shader == null || !shader.isSupported) { instanced = null; return false; }

            instanced = new Material(original) { name = original.name + " (instanced)", shader = shader, enableInstancing = true };
            Instanced[original] = instanced;
            return true;
        }
    }
}
