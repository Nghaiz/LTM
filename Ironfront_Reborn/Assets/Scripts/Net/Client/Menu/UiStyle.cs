using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The UI pack's design tokens and surface styles, at runtime: the palette, the corner cuts,
    /// and how an operations panel, a field and each kind of button are drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One definition for the authoring tools and the game.</b> The menu screens and the HUD are
    /// authored in the Editor (<c>IronfrontUiKit</c> delegates here), while the overlays a player
    /// opens from the menu and from inside a match -- settings, the guide, achievements, the
    /// ranking -- are built at runtime, because they have to exist in every scene. Both read their
    /// colours and faces from this class, so a button is the same button wherever it is drawn.
    /// </para>
    /// <para>Colours are named for their token in <c>ui-pack/css/tokens.css</c>.</para>
    /// </remarks>
    public static class UiStyle
    {
        /// <summary><c>--steel-100</c>: body text.</summary>
        public static readonly Color Ink = Hex("E7F2FB");

        /// <summary><c>--ink-900</c>: text on the orange primary face.</summary>
        public static readonly Color Ink900 = Hex("07111D");

        /// <summary>Secondary text.</summary>
        public static readonly Color Muted = Hex("8DA8BA");

        /// <summary>Fainter than <see cref="Muted"/>: captions over glass.</summary>
        public static readonly Color Faint = Hex("6F8CA2");

        public static readonly Color Cyan = Hex("35B6FF");
        public static readonly Color CyanSoft = Hex("7ACFFF");
        public static readonly Color Orange = Hex("FF7417");
        public static readonly Color Amber = Hex("FFB23F");
        public static readonly Color Green = Hex("3BDB83");
        public static readonly Color Red = Hex("FF5265");
        public static readonly Color Gold = Hex("FFC843");

        /// <summary><c>--line</c>: the default stroke.</summary>
        public static readonly Color Hairline = new Color(113f / 255f, 179f / 255f, 226f / 255f, 0.42f);

        /// <summary><c>--panel</c>: glass cards.</summary>
        public static readonly Color PanelFill = new Color(5f / 255f, 18f / 255f, 31f / 255f, 0.93f);

        /// <summary>A row of a table, and its alternate.</summary>
        public static readonly Color RowEven = new Color(1f, 1f, 1f, 0.035f);
        public static readonly Color RowOdd = new Color(1f, 1f, 1f, 0.07f);

        // The corners, in reference pixels, exactly as the stylesheet cuts them.
        public const float CutPanel = 20f;
        public const float CutCard = 18f;
        public const float CutMenuButton = 13f;
        public const float CutAction = 10f;

        /// <summary>Buttons at least this tall are <c>.menu-button</c> rows rather than actions.</summary>
        public const float MenuButtonHeight = 62f;

        /// <summary>The kinds of button face: the orange action, the plain one, command blue, danger red.</summary>
        public const string Primary = "primary";
        public const string Secondary = "secondary";
        public const string Command = "command";
        public const string Danger = "danger";

        /// <summary>
        /// <c>panels/operations-panel.svg</c> on a surface: the glass, its stroke, and the orange
        /// bracket on the cut corner at its designed size whatever the panel's.
        /// </summary>
        public static void StyleOperationsPanel(AngularPanel panel)
        {
            panel.color = new Color(6f / 255f, 20f / 255f, 33f / 255f, 0.9f);
            panel.Configure(CutPanel, AngularEdge.All, 1f, new Color(107f / 255f, 164f / 255f, 206f / 255f, 0.65f));

            Transform existing = panel.transform.Find("Accent");
            GameObject accentObject = existing != null
                ? existing.gameObject
                : new GameObject("Accent", typeof(RectTransform), typeof(AngularAccent));
            accentObject.transform.SetParent(panel.transform, worldPositionStays: false);

            RectTransform rect = accentObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = AngularAccent.DesignSize;

            AngularAccent accent = accentObject.GetComponent<AngularAccent>();
            accent.color = Orange;
            accent.raycastTarget = false;
        }

        /// <summary>
        /// <c>inputs/field.svg</c> on a surface: an uncut frame with a 1px border and the 3px cyan bar
        /// over its left edge. Finds the bar a previous call made rather than adding a second.
        /// </summary>
        public static void StyleFieldFace(AngularPanel face)
        {
            face.color = WithAlpha(Hex("071523"), 0.92f);
            face.Configure(0f, AngularEdge.All, 1f, Hex("5E89A9"));

            Transform existing = face.transform.Find("Accent");
            GameObject barObject = existing != null
                ? existing.gameObject
                : new GameObject("Accent", typeof(RectTransform), typeof(AngularPanel));
            barObject.transform.SetParent(face.transform, worldPositionStays: false);
            barObject.transform.SetAsFirstSibling();

            RectTransform barRect = barObject.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0f, 0f);
            barRect.anchorMax = new Vector2(0f, 1f);
            barRect.pivot = new Vector2(0f, 0.5f);
            barRect.anchoredPosition = Vector2.zero;
            barRect.sizeDelta = new Vector2(3f, 0f);

            AngularPanel bar = barObject.GetComponent<AngularPanel>();
            bar.color = Hex("39AEF5");
            bar.Configure(0f, AngularEdge.None, 0f, Color.clear);
            bar.raycastTarget = false;
        }

        /// <summary>The face for a button of <paramref name="kind"/>: primary, secondary, command or danger.</summary>
        public static void StyleButtonFace(AngularPanel face, string kind, float height)
        {
            float cut = height >= MenuButtonHeight ? CutMenuButton : CutAction;
            switch (kind)
            {
                case Primary:
                    face.color = Hex("FF9D27");
                    face.SetGradient(Hex("F15A0A"), 90f);
                    face.Configure(cut, AngularEdge.All, 1f, Hex("FFC066"));
                    break;
                case Command:
                    face.color = WithAlpha(Hex("04172B"), 0.92f);
                    face.Configure(cut, AngularEdge.All, 1f, Hex("2E8FD6"));
                    break;
                case Danger:
                    face.color = WithAlpha(Hex("170A10"), 0.92f);
                    face.Configure(cut, AngularEdge.All, 1f, Hex("B8475A"));
                    break;
                default:
                    face.color = WithAlpha(Hex("0A1927"), 0.92f);
                    face.Configure(cut, AngularEdge.All, 1f, Hex("7FB5DB"));
                    break;
            }
            face.raycastTarget = true;
        }

        /// <summary>The tint states every button of a kind shares.</summary>
        public static ColorBlock ButtonColours(ColorBlock colours, string kind)
        {
            colours.normalColor = Color.white;
            colours.highlightedColor = kind == Primary ? Hex("FFD9AE") : CyanSoft;
            colours.selectedColor = colours.highlightedColor;
            colours.pressedColor = kind == Primary ? Hex("E95D0D") : Hex("176F9F");
            colours.disabledColor = new Color(0.35f, 0.4f, 0.45f, 0.45f);
            colours.colorMultiplier = 1f;
            return colours;
        }

        /// <summary>The caption size by the button's class: tall menu rows, ordinary actions, compact ones.</summary>
        public static int CaptionSize(float height)
            => height >= MenuButtonHeight ? Mathf.Clamp(Mathf.RoundToInt(height * 0.32f), 20, 25)
                : height >= 46f ? 16 : 14;

        /// <summary>The caption colour on a kind's face.</summary>
        public static Color CaptionInk(string kind) => kind == Primary ? Ink900 : Ink;

        public static Color Hex(string rgb)
            => ColorUtility.TryParseHtmlString("#" + rgb, out Color colour) ? colour : Color.white;

        public static Color WithAlpha(Color colour, float alpha)
        {
            colour.a = alpha;
            return colour;
        }
    }
}
