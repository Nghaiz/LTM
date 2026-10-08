#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The guide: a tab per subject on the left, its cards on the right (owner's list of
    /// 2026-10-09, item 1). Opened from the main menu, with the How to play key on any other menu
    /// screen and on the deploy screen, and from the in-match Esc menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The words are data</b> (<see cref="HowToPlayContent"/>); this draws them. The CONTROLS tab
    /// is not written at all: it lists <see cref="GameActionCatalog"/> with the player's own keys,
    /// so it can never disagree with Settings, and it redraws when a binding changes.
    /// </para>
    /// <para>
    /// Built by code, like every overlay page, because one copy serves the menu scene and every
    /// map; see <see cref="OverlayHost"/>.
    /// </para>
    /// </remarks>
    public sealed class HowToPlayPage : OverlayPageView
    {
        private const float TabWidth = 236f;
        private const float TabPitch = 66f;
        private const float BodyLeft = 262f;
        private const float BodyWidth = 1290f;
        private const float CardGap = 16f;
        private const float CardHeight = 156f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => OverlayHost.RegisterPage<HowToPlayPage>(OverlayPage.HowToPlay);

        private readonly List<(Button Button, Image Bar)> _tabs = new List<(Button, Image)>();
        private readonly List<GameObject> _bodies = new List<GameObject>();
        private readonly List<(Text Text, string Source)> _keyed = new List<(Text, string)>();
        private readonly List<(GameAction Action, Text Primary, Text Secondary)> _controls =
            new List<(GameAction, Text, Text)>();
        private int _tab;

        public override OverlayPage Page => OverlayPage.HowToPlay;
        public override string Title => "HOW TO PLAY";
        public override string Kicker => "FIELD MANUAL // EVERYTHING A NEW SOLDIER NEEDS";
        public override string Subtitle => "Rules, objectives, controls, vehicles and the tricks veterans use.";
        public override string IconName => "book";

        public override void Build(RectTransform content, RectTransform actions)
        {
            for (int i = 0; i < HowToPlayContent.Tabs.Length; i++)
            {
                HowToPlayContent.Tab tab = HowToPlayContent.Tabs[i];
                BuildTab(content, i, tab);
                _bodies.Add(tab.Id == HowToPlayContent.ControlsTab ? BuildControls(content, tab) : BuildCards(content, tab));
            }

            Button keys = Ui.Button(actions, "OpenControls", "CHANGE KEYS", UiStyle.Secondary, new Vector2(240f, 52f), "keyboard");
            Ui.TopLeft((RectTransform)keys.transform, new Vector2(760f, 0f), new Vector2(240f, 52f));
            keys.onClick.AddListener(() =>
            {
                SettingsPage.RequestCategory(SettingsCategory.Controls);
                GameOverlays.Open(OverlayPage.Settings);
            });

            GameKeys.Changed += Redraw;
            ShowTab(0);
        }

        private void OnDestroy() => GameKeys.Changed -= Redraw;

        public override void OnShown() => Redraw();

        // ------------------------------------------------------------------ layout

        private void BuildTab(RectTransform content, int index, HowToPlayContent.Tab tab)
        {
            string caption = (index + 1).ToString("00", CultureInfo.InvariantCulture) + "  " + tab.Caption;
            Button button = Ui.Button(content, "Tab " + tab.Caption, caption, UiStyle.Secondary, new Vector2(TabWidth, 56f), tab.Icon);
            Ui.TopLeft((RectTransform)button.transform, new Vector2(0f, index * TabPitch), new Vector2(TabWidth, 56f));
            button.transform.Find("Caption").GetComponent<Text>().alignment = TextAnchor.MiddleLeft;

            Image bar = Ui.Fill(button.transform, "Active", UiStyle.Orange);
            bar.rectTransform.anchorMin = new Vector2(0f, 0f);
            bar.rectTransform.anchorMax = new Vector2(0f, 1f);
            bar.rectTransform.pivot = new Vector2(0f, 0.5f);
            bar.rectTransform.sizeDelta = new Vector2(4f, -12f);
            bar.rectTransform.anchoredPosition = new Vector2(2f, 0f);

            int chosen = index;
            button.onClick.AddListener(() => ShowTab(chosen));
            _tabs.Add((button, bar));
        }

        private RectTransform Body(RectTransform content, HowToPlayContent.Tab tab, out float top)
        {
            RectTransform body = Ui.Child(content, tab.Caption + " Body");
            body.anchorMin = new Vector2(0f, 0f);
            body.anchorMax = new Vector2(0f, 1f);
            body.pivot = new Vector2(0f, 1f);
            body.anchoredPosition = new Vector2(BodyLeft, 0f);
            body.sizeDelta = new Vector2(BodyWidth, 0f);

            Text lead = Ui.Label(body, "Lead", HowToPlayContent.Keys(tab.Lead), 17, Ui.Weight.Regular, UiStyle.Ink, TextAnchor.UpperLeft);
            Ui.TopLeft(lead.rectTransform, new Vector2(0f, 4f), new Vector2(BodyWidth, 52f));
            _keyed.Add((lead, tab.Lead));

            Image rule = Ui.Fill(body, "Rule", UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            Ui.TopLeft(rule.rectTransform, new Vector2(0f, 62f), new Vector2(BodyWidth, 1f));
            top = 78f;
            return body;
        }

        private GameObject BuildCards(RectTransform content, HowToPlayContent.Tab tab)
        {
            RectTransform body = Body(content, tab, out float top);
            float width = (BodyWidth - CardGap) * 0.5f;
            int rows = (tab.Cards.Length + 1) / 2;
            float height = Mathf.Min(CardHeight, (672f - top - (rows - 1) * CardGap) / Mathf.Max(1, rows));

            for (int i = 0; i < tab.Cards.Length; i++)
            {
                HowToPlayContent.Card card = tab.Cards[i];
                var position = new Vector2((i % 2) * (width + CardGap), top + (i / 2) * (height + CardGap));
                BuildCard(body, card, position, new Vector2(width, height));
            }
            return body.gameObject;
        }

        private void BuildCard(RectTransform body, HowToPlayContent.Card card, Vector2 position, Vector2 size)
        {
            AngularPanel panel = Ui.Panel(body, card.Title, UiStyle.WithAlpha(UiStyle.Hex("0A1C2C"), 0.82f), 10f,
                UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            var rect = (RectTransform)panel.transform;
            Ui.TopLeft(rect, position, size);

            AngularPanel badge = Ui.Panel(rect, "Badge", UiStyle.WithAlpha(UiStyle.Orange, 0.14f), 8f,
                UiStyle.WithAlpha(UiStyle.Orange, 0.7f));
            Ui.TopLeft((RectTransform)badge.transform, new Vector2(18f, 18f), new Vector2(60f, 60f));
            Image icon = Ui.Icon(badge.transform, "Icon", card.Icon, UiStyle.Amber);
            Ui.Centre(icon.rectTransform, Vector2.zero, new Vector2(38f, 38f));

            Text title = Ui.Label(rect, "Title", card.Title, 17, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(title.rectTransform, new Vector2(96f, 16f), new Vector2(size.x - 116f, 26f));

            Text text = Ui.Label(rect, "Body", HowToPlayContent.Keys(card.Body), 15, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.UpperLeft);
            text.lineSpacing = 1.1f;
            Ui.TopLeft(text.rectTransform, new Vector2(96f, 46f), new Vector2(size.x - 116f, size.y - 56f));
            _keyed.Add((text, card.Body));
        }

        private GameObject BuildControls(RectTransform content, HowToPlayContent.Tab tab)
        {
            RectTransform body = Body(content, tab, out float top);
            const float columnGap = 40f;
            const float rowPitch = 31f;
            float columnWidth = (BodyWidth - columnGap) * 0.5f;

            // Movement and vehicles on the left, combat and the interface on the right, then the
            // two keys nobody rebinds: two columns of about the same height.
            var columns = new[] { new List<GameActionInfo>(), new List<GameActionInfo>() };
            foreach (GameActionInfo info in GameActionCatalog.All)
                columns[info.Group == GameActionGroup.Movement || info.Group == GameActionGroup.Vehicles ? 0 : 1].Add(info);

            for (int c = 0; c < 2; c++)
            {
                float x = c * (columnWidth + columnGap);
                float y = top;
                GameActionGroup? group = null;
                foreach (GameActionInfo info in columns[c])
                {
                    if (group != info.Group)
                    {
                        group = info.Group;
                        Text heading = Ui.Label(body, "Group " + info.Group, GameActionCatalog.GroupName(info.Group), 13,
                            Ui.Weight.Bold, UiStyle.CyanSoft);
                        Ui.TopLeft(heading.rectTransform, new Vector2(x, y + 4f), new Vector2(columnWidth, 22f));
                        y += 28f;
                    }
                    ControlRow(body, info.Name, info.Action, new Vector2(x, y), columnWidth);
                    y += rowPitch;
                }

                if (c == 1)
                {
                    Text heading = Ui.Label(body, "Group Fixed", "ALWAYS", 13, Ui.Weight.Bold, UiStyle.CyanSoft);
                    Ui.TopLeft(heading.rectTransform, new Vector2(x, y + 4f), new Vector2(columnWidth, 22f));
                    y += 28f;
                    FixedRow(body, "Look and aim", "MOUSE", new Vector2(x, y), columnWidth);
                    FixedRow(body, "Menu / back", "ESC", new Vector2(x, y + rowPitch), columnWidth);
                }
            }
            return body.gameObject;
        }

        private void ControlRow(RectTransform body, string name, GameAction action, Vector2 position, float width)
        {
            Image backing = Ui.Fill(body, "Row " + name, UiStyle.RowEven);
            Ui.TopLeft(backing.rectTransform, position, new Vector2(width, 28f));
            Text label = Ui.Label(backing.transform, "Name", name.ToUpperInvariant(), 14, Ui.Weight.Bold, UiStyle.Ink);
            Ui.Stretch(label.rectTransform, 12f, 260f, 0f, 0f);
            Text primary = Cap(backing.transform, "Primary", width - 250f);
            Text secondary = Cap(backing.transform, "Secondary", width - 124f);
            _controls.Add((action, primary, secondary));
        }

        private static void FixedRow(RectTransform body, string name, string key, Vector2 position, float width)
        {
            Image backing = Ui.Fill(body, "Row " + name, UiStyle.RowEven);
            Ui.TopLeft(backing.rectTransform, position, new Vector2(width, 28f));
            Text label = Ui.Label(backing.transform, "Name", name.ToUpperInvariant(), 14, Ui.Weight.Bold, UiStyle.Ink);
            Ui.Stretch(label.rectTransform, 12f, 260f, 0f, 0f);
            Cap(backing.transform, "Primary", width - 250f).text = key;
        }

        private static Text Cap(Transform row, string name, float left)
        {
            AngularPanel cap = Ui.Panel(row, name, UiStyle.WithAlpha(UiStyle.Hex("0E2639"), 0.95f), 4f, UiStyle.Hex("5E89A9"));
            Ui.TopLeft((RectTransform)cap.transform, new Vector2(left, 2f), new Vector2(116f, 24f));
            Text text = Ui.Label(cap.transform, "Key", string.Empty, 13, Ui.Weight.Bold, UiStyle.Ink, TextAnchor.MiddleCenter);
            Ui.Stretch(text.rectTransform, 4f, 4f, 0f, 0f);
            return text;
        }

        // ------------------------------------------------------------------ state

        private void ShowTab(int index)
        {
            _tab = Mathf.Clamp(index, 0, _bodies.Count - 1);
            for (int i = 0; i < _bodies.Count; i++)
            {
                bool on = i == _tab;
                _bodies[i].SetActive(on);
                _tabs[i].Bar.enabled = on;
                // As Settings marks its tab: the chosen one is the disabled face with the bar.
                _tabs[i].Button.interactable = !on;
            }
        }

        /// <summary>Rewrites every key the guide mentions from the player's current bindings.</summary>
        private void Redraw()
        {
            foreach ((Text text, string source) in _keyed) text.text = HowToPlayContent.Keys(source);

            KeyBindingSet keys = GameKeys.Bindings;
            foreach ((GameAction action, Text primary, Text secondary) in _controls)
            {
                KeyCode first = keys.Get(action, 0);
                KeyCode second = keys.Get(action, 1);
                primary.text = first == KeyCode.None ? "—" : GameKeys.KeyName(first);
                primary.color = first == KeyCode.None ? UiStyle.Red : UiStyle.Ink;
                secondary.text = second == KeyCode.None ? "—" : GameKeys.KeyName(second);
                secondary.color = second == KeyCode.None ? UiStyle.Faint : UiStyle.Ink;
            }
        }
    }
}
