#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Applies the Ironfront visual system and keyboard wiring to the generated menu hierarchy.
    /// It is intentionally idempotent so an authored scene and an older scene receive the same UI.
    /// </summary>
    public static class MenuRuntimeTheme
    {
        private const string MarkerName = "Ironfront Runtime Theme";
        private const string CardName = "Theme Content Card";
        private const string RailName = "Theme Brand Rail";

        private static readonly Color Canvas = new Color(0.018f, 0.027f, 0.039f, 0.985f);
        private static readonly Color Surface = new Color(0.042f, 0.061f, 0.079f, 0.98f);
        private static readonly Color SurfaceRaised = new Color(0.067f, 0.094f, 0.118f, 0.99f);
        private static readonly Color Field = new Color(0.035f, 0.052f, 0.069f, 1f);
        private static readonly Color Ink = new Color(0.91f, 0.94f, 0.96f, 1f);
        private static readonly Color MutedInk = new Color(0.53f, 0.61f, 0.67f, 1f);
        private static readonly Color Amber = new Color(0.95f, 0.58f, 0.18f, 1f);
        private static readonly Color AmberBright = new Color(1f, 0.68f, 0.27f, 1f);
        private static readonly Color Cyan = new Color(0.28f, 0.72f, 0.78f, 1f);
        private static readonly Color Danger = new Color(0.94f, 0.34f, 0.31f, 1f);

        private static readonly HashSet<string> WidePanels = new HashSet<string>(StringComparer.Ordinal)
        {
            "RoomBrowser",
            "RoomLobby"
        };

        private static readonly HashSet<string> PrimaryButtons = new HashSet<string>(StringComparer.Ordinal)
        {
            "Multiplayer",
            "LogIn",
            "Create",
            "BrowseRooms",
            "CreateRoom",
            "Join",
            "Ready",
            "Send"
        };

        public static void Apply(GameObject root)
        {
            if (root == null || root.transform.Find(MarkerName) != null) return;

            var marker = new GameObject(MarkerName, typeof(RectTransform));
            marker.transform.SetParent(root.transform, worldPositionStays: false);
            marker.SetActive(false);

            foreach (Transform child in root.transform.Cast<Transform>().ToArray())
            {
                if (child.gameObject == marker || child.name == "PracticeBackBar") continue;
                ThemePanel(child.gameObject, WidePanels.Contains(child.name));
            }

            ThemePracticeBar(root.transform.Find("PracticeBackBar"));
        }

        private static void ThemePanel(GameObject panel, bool wide)
        {
            Image panelImage = panel.GetComponent<Image>() ?? panel.AddComponent<Image>();
            panelImage.color = Canvas;
            panelImage.raycastTarget = false;

            if (panel.GetComponent<CanvasGroup>() == null) panel.AddComponent<CanvasGroup>();
            if (panel.GetComponent<MenuScreenTransition>() == null)
                panel.AddComponent<MenuScreenTransition>();

            RectTransform card = BuildCard(panel, wide);
            BuildBrand(panel, wide);
            StyleText(card.gameObject);
            StyleControls(card.gameObject);
            AddFieldLabels(card.gameObject);
            WireNavigation(panel);

            Transform prompt = card.Find("PasswordPrompt");
            if (prompt != null)
            {
                StylePrompt(prompt.gameObject);
                WirePasswordPrompt(prompt.gameObject);
            }
        }

        private static RectTransform BuildCard(GameObject panel, bool wide)
        {
            Transform existing = panel.transform.Find(CardName);
            if (existing != null) return (RectTransform)existing;

            Transform[] content = panel.transform.Cast<Transform>().ToArray();
            var cardObject = new GameObject(CardName, typeof(RectTransform), typeof(Image), typeof(Outline));
            cardObject.transform.SetParent(panel.transform, worldPositionStays: false);
            cardObject.transform.SetAsFirstSibling();

            RectTransform card = cardObject.GetComponent<RectTransform>();
            Centre(card, wide ? Vector2.zero : new Vector2(310f, 0f),
                wide ? new Vector2(1740f, 940f) : new Vector2(980f, 900f));

            Image surface = cardObject.GetComponent<Image>();
            surface.color = Surface;
            surface.raycastTarget = false;

            Outline outline = cardObject.GetComponent<Outline>();
            outline.effectColor = new Color(Cyan.r, Cyan.g, Cyan.b, 0.22f);
            outline.effectDistance = new Vector2(1f, -1f);

            foreach (Transform item in content)
                item.SetParent(card, worldPositionStays: false);

            return card;
        }

        private static void BuildBrand(GameObject panel, bool wide)
        {
            var railObject = new GameObject(RailName, typeof(RectTransform), typeof(Image));
            railObject.transform.SetParent(panel.transform, worldPositionStays: false);
            railObject.transform.SetAsFirstSibling();

            RectTransform rail = railObject.GetComponent<RectTransform>();
            Image image = railObject.GetComponent<Image>();
            image.color = SurfaceRaised;
            image.raycastTarget = false;

            if (wide)
            {
                rail.anchorMin = new Vector2(0f, 1f);
                rail.anchorMax = Vector2.one;
                rail.pivot = new Vector2(0.5f, 1f);
                rail.anchoredPosition = Vector2.zero;
                rail.sizeDelta = new Vector2(0f, 72f);
                ThemeLabel(railObject, "Brand", "IRONFRONT REBORN", 26,
                    new Vector2(-670f, -36f), new Vector2(360f, 50f), TextAnchor.MiddleLeft, Ink);
                ThemeLabel(railObject, "Team", "TEAM 10 LTM  /  MULTIPLAYER OPERATIONS", 18,
                    new Vector2(520f, -36f), new Vector2(600f, 44f), TextAnchor.MiddleRight, MutedInk);
            }
            else
            {
                rail.anchorMin = Vector2.zero;
                rail.anchorMax = new Vector2(0f, 1f);
                rail.pivot = new Vector2(0f, 0.5f);
                rail.anchoredPosition = Vector2.zero;
                rail.sizeDelta = new Vector2(520f, 0f);

                ThemeLabel(railObject, "Kicker", "TEAM 10 LTM", 20,
                    new Vector2(68f, 126f), new Vector2(360f, 42f), TextAnchor.MiddleLeft, Cyan);
                ThemeLabel(railObject, "Brand", "IRONFRONT\nREBORN", 64,
                    new Vector2(68f, 0f), new Vector2(390f, 170f), TextAnchor.MiddleLeft, Ink);
                ThemeLabel(railObject, "Descriptor", "ONLINE TACTICAL WARFARE", 18,
                    new Vector2(68f, -118f), new Vector2(390f, 40f), TextAnchor.MiddleLeft, MutedInk);

                GameObject accent = new GameObject("Amber Accent", typeof(RectTransform), typeof(Image));
                accent.transform.SetParent(railObject.transform, worldPositionStays: false);
                Centre(accent.GetComponent<RectTransform>(), new Vector2(-238f, 0f), new Vector2(8f, 250f));
                accent.GetComponent<Image>().color = Amber;
                accent.GetComponent<Image>().raycastTarget = false;
            }
        }

        private static void StyleText(GameObject card)
        {
            foreach (Text text in card.GetComponentsInChildren<Text>(includeInactive: true))
            {
                text.font = DefaultFont();

                if (text.name == "Heading")
                {
                    if (text.text == "IRONFRONT") text.text = "IRONFRONT REBORN";
                    text.color = Ink;
                    text.fontStyle = FontStyle.Bold;
                    text.alignment = TextAnchor.MiddleLeft;
                }
                else if (text.name == "Error")
                {
                    text.color = Danger;
                }
                else if (text.name.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    text.color = MutedInk;
                }
                else if (text.name != "Caption")
                {
                    text.color = Ink;
                }
            }
        }

        private static void StyleControls(GameObject card)
        {
            foreach (Button button in card.GetComponentsInChildren<Button>(includeInactive: true))
                StyleButton(button, PrimaryButtons.Contains(button.name));

            foreach (InputField input in card.GetComponentsInChildren<InputField>(includeInactive: true))
            {
                Image image = input.GetComponent<Image>();
                if (image != null) image.color = Field;
                input.colors = SelectableColors(Field, new Color(0.06f, 0.10f, 0.13f, 1f), Cyan);
                if (input.textComponent != null) input.textComponent.color = Ink;
            }

            foreach (Dropdown dropdown in card.GetComponentsInChildren<Dropdown>(includeInactive: true))
            {
                Image image = dropdown.GetComponent<Image>();
                if (image != null) image.color = Field;
                dropdown.colors = SelectableColors(Field, SurfaceRaised, Cyan);
            }

            foreach (Toggle toggle in card.GetComponentsInChildren<Toggle>(includeInactive: true))
                toggle.colors = SelectableColors(Field, SurfaceRaised, Amber);
        }

        private static void StyleButton(Button button, bool primary)
        {
            Color normal = primary ? Amber : SurfaceRaised;
            Color highlighted = primary ? AmberBright : new Color(0.10f, 0.15f, 0.18f, 1f);
            Color selected = primary ? new Color(1f, 0.76f, 0.40f, 1f) : Cyan;
            button.colors = SelectableColors(normal, highlighted, selected);

            if (button.targetGraphic is Image image) image.color = normal;

            Text caption = button.GetComponentInChildren<Text>(includeInactive: true);
            if (caption != null)
            {
                caption.color = primary ? new Color(0.08f, 0.065f, 0.04f, 1f) : Ink;
                caption.fontStyle = FontStyle.Bold;
            }
        }

        private static ColorBlock SelectableColors(Color normal, Color highlighted, Color selected)
        {
            ColorBlock colors = ColorBlock.defaultColorBlock;
            colors.normalColor = normal;
            colors.highlightedColor = highlighted;
            colors.pressedColor = new Color(
                highlighted.r * 0.76f,
                highlighted.g * 0.76f,
                highlighted.b * 0.76f,
                1f);
            colors.selectedColor = selected;
            colors.disabledColor = new Color(0.16f, 0.18f, 0.20f, 0.62f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            return colors;
        }

        private static void AddFieldLabels(GameObject card)
        {
            foreach (InputField field in card.GetComponentsInChildren<InputField>(includeInactive: true))
            {
                if (field.transform.parent == null
                    || field.transform.parent.Find("Theme Label " + field.name) != null)
                    continue;

                RectTransform source = field.GetComponent<RectTransform>();
                Vector2 position = source.anchoredPosition + new Vector2(0f, source.sizeDelta.y * 0.72f);
                Text label = ThemeLabel(
                    field.transform.parent.gameObject,
                    "Theme Label " + field.name,
                    FieldLabel(field.name),
                    17,
                    position,
                    new Vector2(source.sizeDelta.x, 24f),
                    TextAnchor.MiddleLeft,
                    MutedInk);
                label.fontStyle = FontStyle.Bold;
            }
        }

        private static string FieldLabel(string name)
        {
            switch (name)
            {
                case "ConfirmPassword": return "CONFIRM PASSWORD";
                case "DisplayName": return "DISPLAY NAME  /  OPTIONAL";
                case "MaxPlayers": return "MAX PLAYERS";
                case "BotCount": return "BOTS";
                case "ChatInput": return "SQUAD CHAT";
                default: return name.Replace("Field", string.Empty).ToUpperInvariant();
            }
        }

        private static void StylePrompt(GameObject prompt)
        {
            Image image = prompt.GetComponent<Image>() ?? prompt.AddComponent<Image>();
            image.color = new Color(0.025f, 0.038f, 0.05f, 0.995f);
            if (prompt.GetComponent<Outline>() == null)
            {
                Outline outline = prompt.AddComponent<Outline>();
                outline.effectColor = new Color(Amber.r, Amber.g, Amber.b, 0.45f);
            }
        }

        private static void WireNavigation(GameObject panel)
        {
            switch (panel.name)
            {
                case "Title":
                    Configure(panel, new[] { "Multiplayer", "Practice" }, "Multiplayer", null);
                    break;
                case "Login":
                    Configure(panel, new[] { "Username", "Password", "LogIn", "CreateAccount" }, "LogIn", null);
                    break;
                case "Register":
                    Configure(panel, new[] { "Username", "Password", "ConfirmPassword", "DisplayName", "Create", "Back" }, "Create", "Back");
                    break;
                case "Lobby":
                    Configure(panel, new[] { "BrowseRooms" }, "BrowseRooms", null);
                    break;
                case "RoomBrowser":
                    var roomOrder = Enumerable.Range(0, 8).Select(index => "Row" + index)
                        .Concat(new[] { "Refresh", "CreateRoom" }).ToArray();
                    Configure(panel, roomOrder, "Refresh", null);
                    break;
                case "CreateRoom":
                    Configure(panel, new[] { "Name", "Map", "MaxPlayers", "BotCount", "Private", "Password", "Create", "Back" }, "Create", "Back");
                    break;
                case "RoomLobby":
                    Configure(panel, new[] { "SwitchSide", "Ready", "Leave", "ChatInput", "Send" }, "Send", "Leave");
                    break;
            }
        }

        private static void WirePasswordPrompt(GameObject prompt) =>
            Configure(prompt, new[] { "Password", "Join", "Cancel" }, "Join", "Cancel");

        private static void Configure(
            GameObject host,
            IEnumerable<string> order,
            string? primaryName,
            string? cancelName)
        {
            Selectable[] controls = order
                .Select(name => Find<Selectable>(host, name))
                .Where(control => control != null)
                .Cast<Selectable>()
                .ToArray();

            Button? primary = primaryName != null ? Find<Button>(host, primaryName) : null;
            Button? cancel = cancelName != null ? Find<Button>(host, cancelName) : null;
            MenuKeyboardNavigator navigator = host.GetComponent<MenuKeyboardNavigator>()
                                               ?? host.AddComponent<MenuKeyboardNavigator>();
            navigator.Configure(controls, primary, cancel);
        }

        private static T? Find<T>(GameObject root, string name) where T : Component =>
            root.GetComponentsInChildren<T>(includeInactive: true)
                .FirstOrDefault(component => component.gameObject.name == name);

        private static void ThemePracticeBar(Transform? bar)
        {
            if (bar == null) return;
            foreach (Button button in bar.GetComponentsInChildren<Button>(includeInactive: true))
                StyleButton(button, primary: false);
        }

        private static Text ThemeLabel(
            GameObject parent,
            string name,
            string value,
            int fontSize,
            Vector2 position,
            Vector2 size,
            TextAnchor alignment,
            Color color)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(Text));
            host.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(host.GetComponent<RectTransform>(), position, size);

            Text text = host.GetComponent<Text>();
            text.text = value;
            text.font = DefaultFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            return text;
        }

        private static void Centre(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static Font DefaultFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }
    }
}
