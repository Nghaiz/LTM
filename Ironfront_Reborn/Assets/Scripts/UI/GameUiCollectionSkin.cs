using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Unity.Ui
{
    /// <summary>Shared styling primitives for authored menus and lightweight runtime UI.</summary>
    public static class GameUiCollectionSkin
    {
        public static readonly Color Surface = new Color32(8, 18, 28, 242);
        public static readonly Color TextPrimary = new Color32(236, 246, 247, 255);
        public static readonly Color TextMuted = new Color32(146, 174, 181, 255);
        public static readonly Color Cyan = new Color32(44, 222, 232, 255);
        public static readonly Color Yellow = new Color32(255, 200, 48, 255);

        public static void StylePanel(Image panel)
        {
            if (panel == null)
                return;

            panel.sprite = GameUiCollectionResources.Load().PanelCyan;
            panel.type = Image.Type.Simple;
            panel.color = Color.white;
            panel.raycastTarget = false;
        }

        public static void StyleButton(Button button, bool primary)
        {
            if (button == null)
                return;

            Image target = button.targetGraphic as Image ?? button.GetComponent<Image>();
            if (target != null)
            {
                target.sprite = primary
                    ? GameUiCollectionResources.Load().ButtonYellow
                    : GameUiCollectionResources.Load().ButtonCyan;
                target.type = Image.Type.Simple;
                target.color = Color.white;
                button.targetGraphic = target;
            }

            Color accent = primary ? Yellow : Cyan;
            button.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = Color.Lerp(Color.white, accent, 0.22f),
                pressedColor = Color.Lerp(Color.white, Surface, 0.34f),
                selectedColor = Color.Lerp(Color.white, accent, 0.38f),
                disabledColor = new Color(0.42f, 0.47f, 0.49f, 0.55f),
                colorMultiplier = 1f,
                fadeDuration = 0.08f
            };

            Text caption = button.GetComponentInChildren<Text>(true);
            if (caption != null)
            {
                caption.color = primary ? Surface : TextPrimary;
                caption.fontStyle = FontStyle.Bold;
                caption.raycastTarget = false;
            }
        }

        public static void StyleField(Selectable selectable)
        {
            if (selectable == null)
                return;

            Image target = selectable.targetGraphic as Image ?? selectable.GetComponent<Image>();
            if (target != null)
            {
                target.sprite = GameUiCollectionResources.Load().ButtonBorderCyan;
                target.type = Image.Type.Simple;
                target.color = Color.white;
                selectable.targetGraphic = target;
            }

            selectable.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(0.82f, 1f, 1f, 1f),
                pressedColor = new Color(0.62f, 0.9f, 0.92f, 1f),
                selectedColor = new Color(0.72f, 1f, 1f, 1f),
                disabledColor = new Color(0.4f, 0.45f, 0.47f, 0.52f),
                colorMultiplier = 1f,
                fadeDuration = 0.08f
            };
        }

        public static void StyleText(Text text, bool heading = false)
        {
            if (text == null)
                return;

            text.color = heading ? Cyan : TextPrimary;
            text.fontStyle = heading ? FontStyle.Bold : FontStyle.Normal;
            text.raycastTarget = false;
        }

        public static void StyleCompactControl(Selectable selectable)
        {
            if (selectable == null)
                return;

            ColorBlock colors = selectable.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.72f, 1f, 1f, 1f);
            colors.pressedColor = new Color(0.5f, 0.84f, 0.88f, 1f);
            colors.selectedColor = new Color(0.62f, 1f, 1f, 1f);
            colors.disabledColor = new Color(0.4f, 0.45f, 0.47f, 0.52f);
            colors.fadeDuration = 0.08f;
            selectable.colors = colors;
        }
    }
}
