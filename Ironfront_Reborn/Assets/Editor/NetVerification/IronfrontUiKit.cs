using Ironfront.Net.Unity.Client.Menu;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// The UI pack's design tokens and surfaces, for every tool that authors UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One definition, because two drifted.</b> The menu was rebuilt on the pack's palette,
    /// Roboto and angular geometry while the in-match readout kept the built-in Arial on flat
    /// grey and the legacy HUD its own greys, so the game changed design language at the moment a
    /// match loaded. <c>BuildMenuCanvas</c>, <c>BuildMatchHud</c> and <c>RestyleIngameUi</c> now take
    /// their colours, type and surfaces from here, and a button is the same button on every screen.
    /// </para>
    /// <para>
    /// Colours are named for their token in <c>ui-pack/css/tokens.css</c>, not for the surface
    /// that happens to use them.
    /// </para>
    /// </remarks>
    internal static class IronfrontUiKit
    {
        /// <summary><c>--steel-100</c>: body text.</summary>
        internal static readonly Color Ink = UiStyle.Ink;

        /// <summary><c>--ink-900</c>: text on the orange primary face.</summary>
        internal static readonly Color Ink900 = UiStyle.Ink900;

        /// <summary><c>.section-heading p</c>: secondary text.</summary>
        internal static readonly Color Muted = UiStyle.Muted;

        internal static readonly Color Cyan = UiStyle.Cyan;
        internal static readonly Color CyanSoft = UiStyle.CyanSoft;
        internal static readonly Color Orange = UiStyle.Orange;
        internal static readonly Color Green = UiStyle.Green;
        internal static readonly Color Red = UiStyle.Red;

        /// <summary><c>--line</c>: the default stroke.</summary>
        internal static readonly Color Line = UiStyle.Hairline;

        /// <summary><c>--panel</c>: glass cards.</summary>
        internal static readonly Color Surface = UiStyle.PanelFill;

        // The corners, in reference pixels, exactly as the stylesheet cuts them.
        internal const float CutPanel = UiStyle.CutPanel;
        internal const float CutCard = UiStyle.CutCard;
        internal const float CutMenuButton = UiStyle.CutMenuButton;
        internal const float CutAction = UiStyle.CutAction;

        /// <summary>Buttons at least this tall are <c>.menu-button</c> rows rather than actions.</summary>
        internal const float MenuButtonHeight = UiStyle.MenuButtonHeight;

        /// <summary>
        /// The bundled UI font used by the HTML prototype's fallback stack.
        /// </summary>
        /// <remarks>
        /// The explicit project path keeps glyph metrics stable between Editor and player builds.
        /// A built-in fallback remains so a missing asset produces readable diagnostics instead
        /// of an entirely blank screen.
        /// </remarks>
        internal static Font RegularFont()
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Font/Roboto-Regular.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        internal static Font BoldFont()
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Font/Roboto-Bold.ttf");
            return font != null ? font : RegularFont();
        }

        /// <summary>
        /// A cut-cornered surface, for CSS-only shapes: cards, rules, tracks, bars.
        /// </summary>
        internal static AngularPanel Angular(GameObject parent, string name, Vector2 position,
            Vector2 size, float cut, Color fill, AngularEdge edge = AngularEdge.All,
            float edgeWidth = 1f, Color? edgeColour = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(AngularPanel));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            AngularPanel panel = go.GetComponent<AngularPanel>();
            panel.color = fill;
            panel.Configure(cut, edge, edgeWidth, edgeColour ?? Line);
            panel.raycastTarget = false;
            return panel;
        }

        /// <summary>
        /// <c>panels/operations-panel.svg</c> on an existing surface: the glass, its stroke, and
        /// the orange bracket on the cut corner at its designed size whatever the panel's.
        /// </summary>
        internal static void StyleOperationsPanel(AngularPanel panel)
            => UiStyle.StyleOperationsPanel(panel);

        /// <summary>
        /// <c>inputs/field.svg</c> on an existing surface: an uncut frame with a 1px border, and
        /// the 3px cyan bar over its left edge.
        /// </summary>
        /// <remarks>
        /// The menu's settings rows and the pause menu's OPTIONS rows, so the two read as one
        /// family. It finds the bar a previous call made rather than adding a second, for the
        /// restyles that run over their own output.
        /// </remarks>
        internal static void StyleFieldFace(AngularPanel face)
            => UiStyle.StyleFieldFace(face);

        /// <summary>
        /// The button face for an action variant: <c>primary.svg</c>, <c>secondary.svg</c>, and
        /// the two semantic states the pack has no master for.
        /// </summary>
        /// <remarks>
        /// Command and danger used to multiply a colour into the secondary PNG, which darkened its
        /// stroke into the fill. Here each has its own fill and stroke, in the palette's blue and
        /// red, so the state reads from the edge the way the other two variants do.
        /// </remarks>
        internal static void StyleButtonFace(AngularPanel face, string kind, float height)
            => UiStyle.StyleButtonFace(face, kind, height);

        /// <summary>The caption of the action that puts a player into the world.</summary>
        internal const string DeployCaption = "DEPLOY  ➜";

        /// <summary><c>.deploy-action</c>'s ink: darker than any text on the menu's orange.</summary>
        internal static readonly Color DeployInk = Hex("1A2020");

        /// <summary>
        /// <c>buttons/deploy.svg</c>, from the HUD pack: the one orange action that puts a player
        /// into the world, on the loadout screen and on the death screen alike.
        /// </summary>
        /// <remarks>
        /// The master is a vertical gradient from <c>#FFAE48</c> to <c>#E66A16</c> under a 2px
        /// <c>#FFC172</c> stroke, cut at the top-left and the bottom-right — the two corners
        /// <see cref="AngularPanel"/> cuts. Its tint states are the primary button's.
        /// </remarks>
        internal static void StyleDeployFace(AngularPanel face)
        {
            face.color = Hex("FFAE48");
            face.SetGradient(Hex("E66A16"), 180f);
            face.Configure(15f, AngularEdge.All, 2f, Hex("FFC172"));
            face.raycastTarget = true;
        }

        /// <summary>
        /// <c>panels/loadout-card.svg</c>: an equipment card's glass, stroke and cut.
        /// </summary>
        internal static void StyleCard(AngularPanel face)
        {
            face.color = new Color(7f / 255f, 22f / 255f, 35f / 255f, 0.9f);
            face.Configure(14f, AngularEdge.All, 1f, new Color(102f / 255f, 134f / 255f, 162f / 255f, 0.6f));
            face.raycastTarget = true;
        }

        /// <summary>The tint states every button of a variant shares.</summary>
        internal static ColorBlock ButtonColours(ColorBlock colours, string kind)
            => UiStyle.ButtonColours(colours, kind);

        /// <summary>
        /// The caption size, by the button's class rather than its exact height: the tall menu
        /// rows, the ordinary actions, and the compact table/prompt buttons.
        /// </summary>
        /// <remarks>
        /// Scaling with the height gave a screen's side-by-side controls three different type
        /// sizes when their heights differed by a few pixels.
        /// </remarks>
        internal static int CaptionSize(float height) => UiStyle.CaptionSize(height);

        /// <summary>The caption colour on a variant's face.</summary>
        internal static Color CaptionInk(string kind) => UiStyle.CaptionInk(kind);

        /// <summary>
        /// Imports a UI texture so it stays sharp where a canvas draws it smaller than its pixels.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trilinear, over a Kaiser-filtered mip chain biased half a level sharp. Bilinear snaps to
        /// one mip: art drawn between two levels' sizes comes from the coarser one and is magnified
        /// back up, which is the smear the menu's wordmark and the HUD's icons showed. Kaiser keeps
        /// edges in the downsampled levels that a box filter averages away, and the bias leans
        /// every lookup toward the finer of the two levels it blends.
        /// </para>
        /// <para>
        /// Line art wants <see cref="TextureImporterCompression.Uncompressed"/>: block compression
        /// rings and smears its hard edges. Smooth art — paintings, gradients, soft masks — keeps
        /// whatever compression suits its size.
        /// </para>
        /// </remarks>
        /// <returns>Whether a setting changed, so the caller reimports only what it touched.</returns>
        internal static bool SharpenUiTexture(TextureImporter importer, TextureImporterCompression compression)
        {
            bool changed = !importer.mipmapEnabled
                || importer.filterMode != FilterMode.Trilinear
                || importer.mipmapFilter != TextureImporterMipFilter.KaiserFilter
                || !Mathf.Approximately(importer.mipMapBias, -0.5f)
                || importer.textureCompression != compression;

            importer.mipmapEnabled = true;
            importer.filterMode = FilterMode.Trilinear;
            importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
            importer.mipMapBias = -0.5f;
            importer.textureCompression = compression;
            return changed;
        }

        internal static Color Hex(string rgb)
            => UiStyle.Hex(rgb);

        internal static Color WithAlpha(Color colour, float alpha)
            => UiStyle.WithAlpha(colour, alpha);
    }
}
