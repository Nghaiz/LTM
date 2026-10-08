using Ironfront.Net.Unity.Client.Overlay;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Writes <c>Resources/IronfrontUi/UiSkin.asset</c>: the fonts and painted backgrounds the
    /// runtime overlays (settings, the guide, achievements, the ranking) draw with.
    /// </summary>
    /// <remarks>
    /// Re-runnable; it only reassigns references. The icons and badges beside it are rendered by
    /// <c>tools/ui/make_icons.py</c> and imported by <see cref="IronfrontUiImport"/>.
    /// </remarks>
    public static class BuildUiSkin
    {
        private const string AssetPath = "Assets/Resources/IronfrontUi/UiSkin.asset";

        [MenuItem("Ironfront/Net/Build UI skin")]
        public static void Run()
        {
            UiSkin skin = AssetDatabase.LoadAssetAtPath<UiSkin>(AssetPath);
            if (skin == null)
            {
                skin = ScriptableObject.CreateInstance<UiSkin>();
                AssetDatabase.CreateAsset(skin, AssetPath);
            }

            skin.Assign(
                Font("Roboto-Regular"), Font("Roboto-Bold"), Font("Roboto-Black"),
                IronfrontRebornUiAssetCatalog.Sprite("backgrounds/main-menu.png"),
                IronfrontRebornUiAssetCatalog.Sprite("backgrounds/multiplayer.png"),
                IronfrontRebornUiAssetCatalog.Sprite("branding/ironfront-reborn-logo.png"));
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();
            Debug.Log("[ui-skin] wrote " + AssetPath);
        }

        private static Font Font(string name)
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Font/" + name + ".ttf");
            if (font == null) throw new System.InvalidOperationException("Assets/Font/" + name + ".ttf is missing.");
            return font;
        }
    }

    /// <summary>
    /// Imports every PNG under <c>Assets/Resources/IronfrontUi</c> as a sharp UI sprite, so an icon
    /// or badge re-rendered by <c>make_icons.py</c> needs no hand setting.
    /// </summary>
    public sealed class IronfrontUiImport : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/IronfrontUi/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder) || !assetPath.EndsWith(".png")) return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 512;
            // Glyphs are line art and stay uncompressed; the badges are painted and compress well.
            IronfrontUiKit.SharpenUiTexture(importer, assetPath.Contains("/Achievements/")
                ? TextureImporterCompression.CompressedHQ
                : TextureImporterCompression.Uncompressed);
        }
    }
}
