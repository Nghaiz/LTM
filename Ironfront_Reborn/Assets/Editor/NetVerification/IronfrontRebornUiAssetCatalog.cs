using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>Exact Unity paths and importer rules for the supplied UI pack.</summary>
    internal static class IronfrontRebornUiAssetCatalog
    {
        internal const string Root = "Assets/UI/IronfrontRebornPack/";

        private static readonly Dictionary<string, Vector4> Borders = new Dictionary<string, Vector4>
        {
            { "panels/", new Vector4(18f, 18f, 18f, 18f) },
            { "buttons/", new Vector4(12f, 12f, 12f, 12f) },
            { "fields/", new Vector4(8f, 8f, 8f, 8f) },
            { "tabs/", new Vector4(12f, 12f, 12f, 12f) },
        };

        internal static void ConfigureImporters()
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { Root.TrimEnd('/') });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.filterMode = FilterMode.Bilinear;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                Vector4 border = Vector4.zero;
                foreach (KeyValuePair<string, Vector4> rule in Borders)
                    if (path.IndexOf("/" + rule.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        border = rule.Value;
                        break;
                    }
                importer.spriteBorder = border;

                AssetDatabase.WriteImportSettingsIfDirty(path);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        internal static Sprite Sprite(string relativePath)
        {
            string path = Root + relativePath;
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
                throw new InvalidOperationException("Required UI-pack sprite is missing: " + path);
            return sprite;
        }
    }
}
