#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// Builds the runtime overlays' pieces -- rects, text, icons, the pack's surfaces and buttons --
    /// in the UI pack's style (<see cref="UiStyle"/>, <see cref="UiSkin"/>).
    /// </summary>
    /// <remarks>
    /// Built in code at runtime, like the corner radar and the night-vision readout, because these
    /// screens must exist in the menu scene and in every map without being authored into each.
    /// Every position is in 1920x1080 reference pixels; the overlay's CanvasScaler does the rest.
    /// </remarks>
    public static class Ui
    {
        public enum Weight { Regular, Bold, Black }

        public static RectTransform Child(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            return rect;
        }

        /// <summary>Centred on its parent's centre at <paramref name="position"/>, <paramref name="size"/> big.</summary>
        public static RectTransform Centre(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        /// <summary>Top-left corner at <paramref name="topLeft"/> (y down from the parent's top edge).</summary>
        public static RectTransform TopLeft(RectTransform rect, Vector2 topLeft, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
            rect.sizeDelta = size;
            return rect;
        }

        /// <summary>Fills the parent, inset by the four margins.</summary>
        public static RectTransform Stretch(RectTransform rect, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
            return rect;
        }

        public static Font FontOf(Weight weight)
            => weight == Weight.Black ? UiSkin.Current.Black
                : weight == Weight.Bold ? UiSkin.Current.Bold
                : UiSkin.Current.Regular;

        public static Text Label(Transform parent, string name, string text, int size, Weight weight,
            Color colour, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            RectTransform rect = Child(parent, name);
            Text label = rect.gameObject.AddComponent<Text>();
            label.font = FontOf(weight);
            label.fontSize = size;
            label.color = colour;
            label.alignment = anchor;
            label.text = text;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.supportRichText = true;
            return label;
        }

        public static Image Art(Transform parent, string name, Sprite? sprite, Color colour)
        {
            RectTransform rect = Child(parent, name);
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A white glyph from the icon set, tinted <paramref name="colour"/>.</summary>
        public static Image Icon(Transform parent, string name, string icon, Color colour)
            => Art(parent, name, UiSkin.Icon(icon), colour);

        /// <summary>A flat, uncut rectangle: rules, row backings, bars.</summary>
        public static Image Fill(Transform parent, string name, Color colour)
        {
            Image image = Art(parent, name, null, colour);
            image.preserveAspect = false;
            return image;
        }

        /// <summary>A cut-cornered glass surface.</summary>
        public static AngularPanel Panel(Transform parent, string name, Color fill, float cut,
            Color? edge = null, AngularEdge edges = AngularEdge.All, float edgeWidth = 1f)
        {
            RectTransform rect = Child(parent, name);
            AngularPanel panel = rect.gameObject.AddComponent<AngularPanel>();
            panel.color = fill;
            panel.Configure(cut, edges, edgeWidth, edge ?? UiStyle.Hairline);
            panel.raycastTarget = false;
            return panel;
        }

        /// <summary>
        /// A button of <paramref name="kind"/> (<see cref="UiStyle.Primary"/> and the rest), with an
        /// optional leading icon. The caption is the button's child "Caption".
        /// </summary>
        public static Button Button(Transform parent, string name, string caption, string kind, Vector2 size,
            string? icon = null)
        {
            AngularPanel face = Panel(parent, name, Color.white, UiStyle.CutAction);
            ((RectTransform)face.transform).sizeDelta = size;
            UiStyle.StyleButtonFace(face, kind, size.y);

            Button button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.colors = UiStyle.ButtonColours(button.colors, kind);

            float textLeft = 14f;
            if (icon != null)
            {
                float glyph = Mathf.Min(size.y * 0.42f, 26f);
                Image mark = Icon(face.transform, "Icon", icon, UiStyle.CaptionInk(kind));
                Centre(mark.rectTransform, new Vector2(-size.x * 0.5f + 18f + glyph * 0.5f, 0f), new Vector2(glyph, glyph));
                textLeft = 26f + glyph;
            }

            Text text = Label(face.transform, "Caption", caption, UiStyle.CaptionSize(size.y), Weight.Bold,
                UiStyle.CaptionInk(kind), TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, textLeft, 14f, 0f, 0f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return button;
        }

        /// <summary>Sets a button's caption, where <see cref="Button(Transform,string,string,string,Vector2,string)"/> put it.</summary>
        public static void SetCaption(Button button, string caption)
        {
            Transform text = button.transform.Find("Caption");
            if (text != null) text.GetComponent<Text>().text = caption;
        }

        /// <summary>A small rounded key cap with a key's name in it: "ESC", "TAB", "LMB".</summary>
        public static RectTransform KeyCap(Transform parent, string name, string key, int size = 13)
        {
            AngularPanel cap = Panel(parent, name, UiStyle.WithAlpha(UiStyle.Hex("0E2639"), 0.95f), 4f,
                UiStyle.Hex("5E89A9"));
            Text text = Label(cap.transform, "Key", key, size, Weight.Bold, UiStyle.Ink, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 6f, 6f, 0f, 0f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rect = (RectTransform)cap.transform;
            rect.sizeDelta = new Vector2(Mathf.Max(30f, 14f + key.Length * (size * 0.62f)), size + 12f);
            return rect;
        }

        /// <summary>
        /// A vertical scroll area: a viewport that clips, and a content rect laid out top-down by the
        /// caller. The mouse wheel scrolls it.
        /// </summary>
        public static ScrollRect Scroll(Transform parent, string name, out RectTransform content)
        {
            RectTransform viewport = Child(parent, name);
            Image hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();

            content = Child(viewport, "Content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            ScrollRect scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.08f;
            scroll.verticalScrollbar = SlimScrollbar(parent, name);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return scroll;
        }

        /// <summary>
        /// A thin bar just outside the right edge of <paramref name="parent"/>, so a list that runs
        /// past the bottom says so; it hides itself when everything fits.
        /// </summary>
        private static Scrollbar SlimScrollbar(Transform parent, string name)
        {
            RectTransform bar = Child(parent, name + " Scrollbar");
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.anchoredPosition = new Vector2(10f, 0f);
            bar.sizeDelta = new Vector2(5f, 0f);
            Image track = bar.gameObject.AddComponent<Image>();
            track.color = UiStyle.WithAlpha(UiStyle.Hairline, 0.25f);

            RectTransform area = Child(bar, "Sliding Area");
            Stretch(area);
            Image handle = Fill(area, "Handle", UiStyle.WithAlpha(UiStyle.CyanSoft, 0.8f));
            Stretch(handle.rectTransform);

            Scrollbar scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            // A scrollbar's value is where the list stands, and 0 is its far end: a list opens at its top.
            scrollbar.value = 1f;
            scrollbar.handleRect = handle.rectTransform;
            scrollbar.targetGraphic = handle;
            scrollbar.colors = UiStyle.ButtonColours(scrollbar.colors, UiStyle.Secondary);
            Navigation none = scrollbar.navigation;
            none.mode = Navigation.Mode.None;
            scrollbar.navigation = none;
            return scrollbar;
        }
    }
}
