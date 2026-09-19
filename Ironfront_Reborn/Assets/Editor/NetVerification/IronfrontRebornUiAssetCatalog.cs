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
        /// Puts every raster menu asset into the form UGUI can draw, and reports what it found.
        /// </summary>
        /// <remarks>
        /// <para>
        /// PNG menu art is imported without mipmaps or lossy compression. SVG files remain in the
        /// repository only as source masters and are deliberately ignored by the runtime catalogue.
        /// </para>
        /// </remarks>
        internal static string ConfigureImporters()
        {
            var report = new StringBuilder();
            int textures = 0;
            int sourceMasters = 0;

            foreach (string path in PackAssetPaths())
            {
                AssetImporter importer = AssetImporter.GetAtPath(path);

                if (importer is TextureImporter texture)
                {
                    texture.textureType = TextureImporterType.Sprite;
                    texture.spriteImportMode = SpriteImportMode.Single;
                    texture.filterMode = FilterMode.Bilinear;
                    // Mipmaps ON, which is the opposite of the usual advice for UI art.
                    //
                    // The usual advice assumes a sprite drawn at its own size, where mip 0 is used
                    // and the chain is dead weight. This pack is not that: the icons are 96px and
                    // the menu draws them at 22 to 28, so every one of them is minified three to
                    // four times over. With no mip chain that minification is a bilinear average of
                    // four texels -- the mushy, shimmery look the icons had, and worse the smaller
                    // the window, because the canvas scaler shrinks the draw size while the
                    // texture stays 96px.
                    texture.mipmapEnabled = true;
                    texture.alphaIsTransparency = true;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.npotScale = TextureImporterNPOTScale.None;
                    texture.textureCompression = TextureImporterCompression.Uncompressed;
                    var settings = new TextureImporterSettings();
                    texture.ReadTextureSettings(settings);
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    texture.SetTextureSettings(settings);
                    AssetDatabase.WriteImportSettingsIfDirty(path);
                    textures++;
                    continue;
                }
                if (importer != null) sourceMasters++;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            report.Append(textures).Append(" uncompressed PNG texture(s), ")
                .Append(sourceMasters).Append(" non-raster source master(s) ignored");
            return report.ToString();
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
