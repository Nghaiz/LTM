using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>Exact Unity paths and importer rules for the supplied UI pack.</summary>
    internal static class IronfrontRebornUiAssetCatalog
    {
        internal const string Root = "Assets/UI/IronfrontReborn/";

        /// <summary>
        /// Puts every pack asset into the form the menu can draw, and reports what it found.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The return value is a line for the builder's log rather than a status: the SVG half of
        /// this pack is imported by Unity's scripted SVG importer, whose settings this tool does
        /// not own and whose enum it must therefore DISCOVER rather than assume. Reporting the
        /// option names it saw is what makes a wrong guess visible in one run instead of costing a
        /// rebuild-and-look cycle.
        /// </para>
        /// <para>
        /// The failure this exists for: the SVGs import as <c>VectorImage</c> assets, and
        /// <see cref="UnityEngine.UI.Image"/> draws <see cref="Sprite"/> and nothing else. Asking
        /// for one returned null, the old caller painted a flat rectangle, and eight screens
        /// shipped with no iconography.
        /// </para>
        /// </remarks>
        internal static string ConfigureImporters()
        {
            var report = new StringBuilder();
            int textures = 0;
            int vectors = 0;
            int converted = 0;

            // Every asset, not `t:Texture2D`.
            //
            // The SVG half of this pack -- all fourteen icons, both badges, the wordmark, the
            // panels, the buttons, the field and the corner -- is imported by Unity's built-in
            // SCRIPTED SVG importer. A scripted import produces no Texture2D at its main asset
            // path, so `FindAssets("t:Texture2D")` never returned one of them and the
            // `is TextureImporter` test never ran for one of them.
            foreach (string path in PackAssetPaths())
            {
                AssetImporter importer = AssetImporter.GetAtPath(path);

                if (importer is TextureImporter texture)
                {
                    texture.textureType = TextureImporterType.Sprite;
                    texture.spriteImportMode = SpriteImportMode.Single;
                    texture.filterMode = FilterMode.Bilinear;
                    texture.mipmapEnabled = false;
                    texture.alphaIsTransparency = true;
                    AssetDatabase.WriteImportSettingsIfDirty(path);
                    textures++;
                    continue;
                }

                if (importer == null) continue;
                vectors++;

                string outcome = ConfigureSvg(importer, path);
                if (outcome != null)
                {
                    converted++;
                    if (report.Length == 0) report.Append("svg importer: ").Append(outcome);
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            report.Append(report.Length == 0 ? string.Empty : "; ")
                .Append(textures).Append(" texture, ").Append(vectors).Append(" scripted (")
                .Append(converted).Append(" switched to a sprite mode)");
            return report.ToString();
        }

        /// <summary>
        /// Switches a scripted SVG importer to a sprite-producing mode, by looking up the mode
        /// rather than hard-coding it.
        /// </summary>
        /// <returns>A description of what was decided, or null when there was nothing to decide.</returns>
        private static string ConfigureSvg(AssetImporter importer, string path)
        {
            var serialized = new SerializedObject(importer);

            // `m_SvgType`, not `svgType`: the .meta file writes the short name because that is what
            // the YAML drops the `m_` prefix from, while the serialized object keeps it. Both are
            // tried so a Unity version that renames either one still works.
            SerializedProperty type = serialized.FindProperty("m_SvgType")
                ?? serialized.FindProperty("svgType");

            if (type == null)
                throw new InvalidOperationException(
                    "The SVG importer at " + path + " has no 'm_SvgType' property, so this tool " +
                    "cannot put it into a sprite mode. Its properties are: " + PropertyNames(serialized));

            string[] modes = type.enumDisplayNames;
            int sprite = Array.FindIndex(modes,
                mode => mode.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0);

            if (sprite < 0)
                throw new InvalidOperationException(
                    "The SVG importer at " + path + " offers no sprite mode. Available modes: " +
                    string.Join(", ", modes) + ".");

            if (type.enumValueIndex == sprite) return null;

            type.enumValueIndex = sprite;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.WriteImportSettingsIfDirty(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return "svgType -> '" + modes[sprite] + "' of {" + string.Join(", ", modes) + "}";
        }

        private static string PropertyNames(SerializedObject serialized)
        {
            var names = new List<string>();
            SerializedProperty iterator = serialized.GetIterator();
            while (iterator.NextVisible(true)) names.Add(iterator.name);
            return string.Join(", ", names);
        }

        /// <summary>Every asset under the pack root, of any importer type.</summary>
        private static IEnumerable<string> PackAssetPaths()
        {
            string root = Root.TrimEnd('/');
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(path)) yield return path;
            }
        }

        /// <summary>
        /// The sprite at <paramref name="relativePath"/>, or a thrown exception naming the path.
        /// </summary>
        /// <remarks>
        /// <b>There is deliberately no <c>TrySprite</c> companion.</b> There used to be, and the
        /// scene builder used it for every panel, button, field, tab, toggle and signal indicator
        /// in all eight screens: a path the pack did not contain returned null, the caller painted
        /// a flat rectangle, and the build reported success. The result was a menu with no
        /// iconography and no angular surfaces anywhere, and no signal that anything was missing.
        /// A sprite the UI asks for is a sprite the UI needs, so the failure belongs at the lookup
        /// rather than in a colour chosen to hide it.
        /// </remarks>
        internal static Sprite Sprite(string relativePath)
        {
            string path = Root + relativePath;

            // `LoadAssetAtPath<Sprite>` asks for the MAIN asset at this path, and the SVG files do
            // not necessarily have a Sprite as their main asset: Unity's scripted SVG importer
            // decides that itself, and the .svg.meta files here carry `svgType: 3` with the sprite
            // data in a `spriteData` block. Searching every asset the path produces is
            // importer-agnostic -- it finds a Sprite whether the importer made it the main asset or
            // a sub-asset, and it needs to know nothing about what `svgType` means.
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                foreach (UnityEngine.Object candidate in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (candidate is Sprite found)
                    {
                        sprite = found;
                        break;
                    }
                }
            }

            if (sprite == null)
            {
                // The message names what the path DID produce, not only what was wanted. The
                // previous revision returned null here and the caller painted a flat rectangle, so
                // eight screens shipped with no artwork and no diagnostic; a replacement that
                // merely threw "sprite is missing" would still leave the next person guessing
                // whether the file was absent or merely imported as something else.
                throw new InvalidOperationException(
                    "Required UI-pack sprite is missing from " + path + ". The path produced: " +
                    Describe(path) + ".");
            }

            return sprite;
        }

        /// <summary>What <paramref name="path"/> actually holds, for an error message.</summary>
        private static string Describe(string path)
        {
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            if (all.Length == 0)
                return "nothing at all (the file is missing, or its importer produced no assets)";

            var names = new List<string>(all.Length);
            foreach (UnityEngine.Object item in all)
                names.Add(item == null
                    ? "<null>"
                    : item.GetType().Name + " named '" + item.name + "'");
            return string.Join(", ", names);
        }
    }
}
