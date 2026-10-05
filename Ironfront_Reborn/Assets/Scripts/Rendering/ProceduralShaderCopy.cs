using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Ironfront.Rendering
{
    /// <summary>
    /// One family of procedural-instancing shader copies: the hidden copies a GPU-driven renderer
    /// draws with, where they live, and the rule that makes one from its original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Copies, never the originals.</b> Declaring <c>#pragma instancing_options procedural</c> on
    /// a shader itself changed how the terrain drew it: Forest Lake's bark and needles went pink
    /// with the terrain still drawing them (A/B screenshots, 2026-10-02). So each original is left as
    /// it is, and the GPU draws use a hidden copy, made by <see cref="Transform"/> and kept in a
    /// <c>Resources</c> folder by the editor's generator, with a material of its own there so a
    /// build keeps its instancing variants.
    /// </para>
    /// <para>
    /// <b>What the rule changes:</b> the name, under <see cref="NamePrefix"/>; the family's include
    /// and procedural instancing line after the first <c>#pragma target</c>;
    /// <c>shader_feature_local</c> to <c>multi_compile_local</c>, because no material in a build
    /// uses the copy with its keywords to keep their variants; and the Amplify graph, which would
    /// regenerate the original.
    /// </para>
    /// <para>
    /// <b>Details take no light but the sun.</b> The terrain draws its details lit by the main
    /// directional light and the ambient alone: a point light, forced per pixel or not, leaves its
    /// grass exactly as dark as it was (only the bare ground under it brightens; Editor A/B,
    /// 2026-10-05). So the detail copies are compiled <c>noforwardadd</c>: Night Mode's pumpkins
    /// and lamps and a rocket's light do not light the grass, as before, and cost no extra pass of
    /// every blade per light.
    /// </para>
    /// </remarks>
    public sealed class ProceduralShaderCopy
    {
        /// <summary>The copies <see cref="InstancedTreeRenderer"/> draws terrain trees with.</summary>
        public static readonly ProceduralShaderCopy Trees = new ProceduralShaderCopy(
            TreeShaderVariants.ResourceFolder, TreeShaderVariants.NamePrefix, "Assets/Scripts/Rendering/TreeInstancing.cginc",
            "TreeInstancingSetup", "Ironfront > Generate instanced tree shaders", "TreeShaderVariants.Transform", false);

        /// <summary>The copies <see cref="InstancedDetailRenderer"/> draws terrain details with.</summary>
        public static readonly ProceduralShaderCopy Details = new ProceduralShaderCopy(
            "InstancedDetails", "Hidden/Ironfront/InstancedDetails/", "Assets/Scripts/Rendering/DetailInstancing.cginc",
            "DetailInstancingSetup", "Ironfront > Generate instanced detail shaders", "ProceduralShaderCopy.Transform", true);

        /// <summary>The <c>Resources</c> folder the copies and their materials live in.</summary>
        public readonly string ResourceFolder;

        /// <summary>What a copy's shader name starts with.</summary>
        public readonly string NamePrefix;

        /// <summary>The include that defines <see cref="SetupFunction"/>.</summary>
        public readonly string Include;

        /// <summary>The procedural instancing setup function each instance runs.</summary>
        public readonly string SetupFunction;

        private readonly string _menu;
        private readonly string _rule;
        private readonly bool _sunOnly;
        private readonly Dictionary<Material, Material> _instanced = new Dictionary<Material, Material>();

        private ProceduralShaderCopy(string resourceFolder, string namePrefix, string include, string setupFunction, string menu, string rule, bool sunOnly)
        {
            _sunOnly = sunOnly;
            ResourceFolder = resourceFolder;
            NamePrefix = namePrefix;
            Include = include;
            SetupFunction = setupFunction;
            _menu = menu;
            _rule = rule;
        }

        /// <summary>The file name, without extension, of the copy of <paramref name="shaderName"/>.</summary>
        public static string FileName(string shaderName) => shaderName.Replace('/', '_').Replace(' ', '_');

        /// <summary>The <c>Resources</c> path of the copy of the shader named <paramref name="shaderName"/>.</summary>
        public string ResourcePath(string shaderName) => ResourceFolder + "/" + FileName(shaderName);

        /// <summary>
        /// The copy of <paramref name="source"/>, the text of the shader named
        /// <paramref name="shaderName"/> at <paramref name="sourcePath"/>.
        /// </summary>
        public string Transform(string source, string shaderName, string sourcePath)
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
            string lines = indent + "#include \"" + Include + "\"\n"
                           + indent + "#pragma instancing_options procedural:" + SetupFunction + "\n";
            text = text.Insert(target.Index + target.Length, lines);
            text = text.Replace("#pragma shader_feature_local ", "#pragma multi_compile_local _ ");
            if (_sunOnly)
            {
                if (!Regex.IsMatch(text, @"^[ \t]*#pragma surface ", RegexOptions.Multiline))
                    throw new System.ArgumentException($"'{sourcePath}' is not a surface shader, so its additive light pass cannot be left out");
                text = Regex.Replace(text, @"^([ \t]*#pragma surface [^\n]*?)[ \t]*$",
                    m => m.Groups[1].Value.Contains(" noforwardadd") ? m.Value : m.Groups[1].Value + " noforwardadd", RegexOptions.Multiline);
            }

            return "// Generated from " + sourcePath + " by " + _menu + ".\n"
                   + "// Do not edit: change the original and generate again (" + _rule + ").\n"
                   + text;
        }

        /// <summary>
        /// The material to draw <paramref name="original"/> with: a copy of it on its shader's
        /// procedural copy, or false when there is no copy.
        /// </summary>
        public bool TryInstanced(Material original, out Material instanced)
        {
            if (original == null || original.shader == null) { instanced = null; return false; }
            if (_instanced.TryGetValue(original, out instanced) && instanced != null) return true;

            var shader = Resources.Load<Shader>(ResourcePath(original.shader.name));
            if (shader == null || !shader.isSupported) { instanced = null; return false; }

            instanced = new Material(original) { name = original.name + " (instanced)", shader = shader, enableInstancing = true };
            _instanced[original] = instanced;
            return true;
        }
    }
}
