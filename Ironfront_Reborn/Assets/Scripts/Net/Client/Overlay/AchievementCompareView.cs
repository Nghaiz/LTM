#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// Two players' achievements side by side (achievements v2, section 6.2): this player on the
    /// left, the other on the right, the achievement in the middle; bars grow out from the middle.
    /// Filtered by metal, by kind and by who holds what, sorted like the achievements page.
    /// </summary>
    /// <remarks>
    /// Code-built inside <see cref="RankingPage"/>, which it replaces while open. The model is
    /// <see cref="AchievementCompare"/>; the hidden rule was kept by the master before any of it
    /// arrived, so a hidden row this player lacks says CLASSIFIED on the other side.
    /// </remarks>
    public sealed class AchievementCompareView
    {
        private const float Width = 1552f;
        private const float HeaderHeight = 120f;
        private const float ListTop = 228f;
        private const float RowHeight = 70f;
        private const float RowPitch = 76f;
        private const float SideWidth = 420f;
        private const float BarWidth = 300f;

        private static readonly Color MineInk = UiStyle.Cyan;
        private static readonly Color TheirInk = UiStyle.Amber;

        private sealed class RowView
        {
            public RectTransform Rect = null!;
            public Image Backing = null!;
            public Image Badge = null!;
            public Text Title = null!;
            public Text Rule = null!;
            public Text MineText = null!;
            public Text TheirText = null!;
            public Image MineTrack = null!;
            public Image MineFill = null!;
            public Image TheirTrack = null!;
            public Image TheirFill = null!;
        }

        private readonly List<RowView> _rows = new List<RowView>();
        private readonly List<(AchievementTier? Tier, Button Button, Image Bar)> _tierButtons = new List<(AchievementTier?, Button, Image)>();
        private readonly List<(CompareOwnership Owner, Button Button, Image Bar)> _ownerButtons = new List<(CompareOwnership, Button, Image)>();
        private readonly List<(AchievementTags? Tag, Button Button, Image Bar)> _tagButtons = new List<(AchievementTags?, Button, Image)>();
        private List<ComparePair> _pairs = new List<ComparePair>();

        private RectTransform _root = null!;
        private RectTransform _actions = null!;
        private RectTransform _body = null!;
        private RectTransform _list = null!;
        private ScrollRect _scroll = null!;
        private Text _myName = null!;
        private Text _theirName = null!;
        private Text _myTotals = null!;
        private Text _theirTotals = null!;
        private Text _myTiers = null!;
        private Text _theirTiers = null!;
        private Image _myBar = null!;
        private Image _theirBar = null!;
        private Text _message = null!;
        private Text _empty = null!;
        private Button _sortButton = null!;
        private AchievementTier? _tier;
        private CompareOwnership _owner = CompareOwnership.All;
        private AchievementTags? _tag;
        private AchievementSort _sort = AchievementSort.Difficulty;

        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        /// <summary>The player compared with, 0 when none.</summary>
        public int TheirId { get; private set; }

        /// <summary>Builds the view inside <paramref name="content"/> and its buttons in <paramref name="actions"/>, hidden.</summary>
        public static AchievementCompareView Build(RectTransform content, RectTransform actions, Action back, Action refresh)
        {
            var view = new AchievementCompareView();
            view.Compose(content, actions, back, refresh);
            view.Hide();
            return view;
        }

        public void Hide()
        {
            TheirId = 0;
            if (_root != null) _root.gameObject.SetActive(false);
            if (_actions != null) _actions.gameObject.SetActive(false);
        }

        /// <summary>The view while the master is asked, or after it could not answer.</summary>
        public void ShowMessage(int theirId, string myName, string theirName, string message)
        {
            TheirId = theirId;
            _myName.text = myName;
            _theirName.text = theirName;
            _body.gameObject.SetActive(false);
            _message.text = message;
            _message.gameObject.SetActive(true);
            Open();
        }

        /// <summary>Draws this player's state beside <paramref name="theirs"/>.</summary>
        public void Show(string myName, AchievementState? mine, ICollection<string> localEarned,
            IReadOnlyDictionary<string, long>? localCareer, PlayerProfile theirs)
        {
            LeaderboardRow row = theirs.Player ?? new LeaderboardRow();
            TheirId = row.PlayerId;
            _pairs = AchievementCompare.Build(mine, localEarned, localCareer, theirs);

            _myName.text = myName;
            _theirName.text = string.IsNullOrEmpty(row.Name) ? "#" + row.PlayerId.ToString(CultureInfo.InvariantCulture) : row.Name;
            DrawTotals(AchievementCompare.MineTotals(_pairs), _myTotals, _myTiers, _myBar);
            DrawTotals(AchievementCompare.TheirTotals(theirs), _theirTotals, _theirTiers, _theirBar);

            _body.gameObject.SetActive(true);
            _message.gameObject.SetActive(false);
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
            Redraw();
            Open();
        }

        private void Open()
        {
            _root.gameObject.SetActive(true);
            _actions.gameObject.SetActive(true);
        }

        private static void DrawTotals(in CompareTotals totals, Text line, Text tiers, Image bar)
        {
            int all = AchievementCatalog.All.Count;
            line.text = totals.Count.ToString(CultureInfo.InvariantCulture) + " / " + all.ToString(CultureInfo.InvariantCulture)
                        + "  ·  " + totals.Points.ToString("N0", CultureInfo.InvariantCulture) + " PTS";
            bar.rectTransform.sizeDelta = new Vector2(BarWidth * (all > 0 ? totals.Count / (float)all : 0f), 0f);

            AchievementTier[] metals =
            {
                AchievementTier.Bronze, AchievementTier.Silver, AchievementTier.Gold, AchievementTier.Platinum, AchievementTier.Mythic,
            };
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < metals.Length; i++)
            {
                if (i > 0) text.Append("   ");
                text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(AchievementArt.TierColour(metals[i]))).Append('>')
                    .Append(AchievementBoard.TierName(metals[i])).Append("</color> ")
                    .Append(totals.PerTier[i].ToString(CultureInfo.InvariantCulture));
            }
            text.Append("   <color=#8DA8BA>HIDDEN</color> ").Append(totals.Hidden.ToString(CultureInfo.InvariantCulture));
            tiers.text = text.ToString();
        }

        // ------------------------------------------------------------------ layout

        private void Compose(RectTransform content, RectTransform actions, Action back, Action refresh)
        {
            _root = Ui.Child(content, "Compare");
            Ui.Stretch(_root);
            BuildHeader();

            _body = Ui.Child(_root, "Compare Body");
            Ui.Stretch(_body);
            BuildFilters();

            RectTransform area = Ui.Child(_body, "Compare List Area");
            Ui.TopLeft(area, new Vector2(0f, ListTop), new Vector2(Width, 672f - ListTop));
            _scroll = Ui.Scroll(area, "Compare List", out RectTransform list);
            Ui.Stretch((RectTransform)_scroll.transform);
            _list = list;
            for (int i = 0; i < AchievementCatalog.All.Count; i++) _rows.Add(BuildRow(list));

            _empty = Ui.Label(_body, "Empty", "NOTHING MATCHES THESE FILTERS", 16, Ui.Weight.Bold, UiStyle.Muted, TextAnchor.MiddleCenter);
            Ui.TopLeft(_empty.rectTransform, new Vector2(0f, ListTop + 60f), new Vector2(Width, 60f));

            _message = Ui.Label(_root, "Message", string.Empty, 18, Ui.Weight.Bold, UiStyle.Muted, TextAnchor.MiddleCenter);
            Ui.TopLeft(_message.rectTransform, new Vector2(0f, 260f), new Vector2(Width, 120f));

            _actions = Ui.Child(actions, "Compare Actions");
            Ui.Stretch(_actions);
            Button backButton = Ui.Button(_actions, "Back", "BACK TO RANKING", UiStyle.Secondary, new Vector2(300f, 52f), "arrow-left");
            Ui.TopLeft((RectTransform)backButton.transform, new Vector2(460f, 0f), new Vector2(300f, 52f));
            backButton.onClick.AddListener(() => back());
            Button refreshButton = Ui.Button(_actions, "Refresh", "REFRESH", UiStyle.Secondary, new Vector2(220f, 52f), "refresh");
            Ui.TopLeft((RectTransform)refreshButton.transform, new Vector2(780f, 0f), new Vector2(220f, 52f));
            refreshButton.onClick.AddListener(() => refresh());
        }

        private void BuildHeader()
        {
            AngularPanel band = Ui.Panel(_root, "Compare Header", UiStyle.WithAlpha(UiStyle.Hex("0B2A44"), 0.92f), 12f,
                UiStyle.WithAlpha(UiStyle.Cyan, 0.55f));
            var rect = (RectTransform)band.transform;
            Ui.TopLeft(rect, Vector2.zero, new Vector2(Width, HeaderHeight));

            BuildSide(rect, left: true, out _myName, out _myTotals, out _myTiers, out _myBar);
            BuildSide(rect, left: false, out _theirName, out _theirTotals, out _theirTiers, out _theirBar);

            Text vs = Ui.Label(rect, "Versus", "VS", 40, Ui.Weight.Black, UiStyle.Orange, TextAnchor.MiddleCenter);
            Ui.TopLeft(vs.rectTransform, new Vector2(Width * 0.5f - 60f, 26f), new Vector2(120f, 56f));
        }

        private static void BuildSide(RectTransform band, bool left, out Text name, out Text totals, out Text tiers, out Image bar)
        {
            Color ink = left ? MineInk : TheirInk;
            TextAnchor anchor = left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
            float x = left ? 24f : Width * 0.5f + 90f;
            float w = Width * 0.5f - 114f;

            Text caption = Ui.Label(band, left ? "You Caption" : "Them Caption", left ? "YOU" : "PLAYER", 12, Ui.Weight.Bold, ink, anchor);
            Ui.TopLeft(caption.rectTransform, new Vector2(x, 8f), new Vector2(w, 18f));
            name = Ui.Label(band, left ? "You Name" : "Them Name", string.Empty, 26, Ui.Weight.Black, UiStyle.Ink, anchor);
            Ui.TopLeft(name.rectTransform, new Vector2(x, 24f), new Vector2(w, 34f));

            Image track = Ui.Fill(band, left ? "You Track" : "Them Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.35f));
            Ui.TopLeft(track.rectTransform, new Vector2(left ? x : x + w - BarWidth, 66f), new Vector2(BarWidth, 8f));
            bar = Ui.Fill(track.transform, "Fill", ink);
            bar.rectTransform.anchorMin = left ? Vector2.zero : new Vector2(1f, 0f);
            bar.rectTransform.anchorMax = left ? new Vector2(0f, 1f) : Vector2.one;
            bar.rectTransform.pivot = left ? new Vector2(0f, 0.5f) : new Vector2(1f, 0.5f);
            bar.rectTransform.anchoredPosition = Vector2.zero;

            totals = Ui.Label(band, left ? "You Totals" : "Them Totals", string.Empty, 14, Ui.Weight.Bold, UiStyle.Ink, anchor);
            Ui.TopLeft(totals.rectTransform, new Vector2(left ? x + BarWidth + 14f : x, 58f), new Vector2(w - BarWidth - 14f, 24f));
            tiers = Ui.Label(band, left ? "You Tiers" : "Them Tiers", string.Empty, 12, Ui.Weight.Bold, UiStyle.Ink, anchor);
            Ui.TopLeft(tiers.rectTransform, new Vector2(x, 88f), new Vector2(w, 20f));
            tiers.supportRichText = true;
        }

        private void BuildFilters()
        {
            float x = 0f;
            (AchievementTier? Tier, string Caption)[] tiers =
            {
                (null, "ALL METALS"), (AchievementTier.Bronze, "BRONZE"), (AchievementTier.Silver, "SILVER"),
                (AchievementTier.Gold, "GOLD"), (AchievementTier.Platinum, "PLATINUM"), (AchievementTier.Mythic, "MYTHIC"),
            };
            foreach ((AchievementTier? tier, string caption) in tiers)
            {
                AchievementTier? chosen = tier;
                (Button button, Image bar) = FilterButton("Compare Metal " + caption, caption, x, 128f, 118f);
                if (tier != null) bar.color = AchievementArt.TierColour(tier.Value);
                button.onClick.AddListener(() => { _tier = chosen; Redraw(); });
                _tierButtons.Add((tier, button, bar));
                x += 124f;
            }

            x += 20f;
            CompareOwnership[] owners =
            {
                CompareOwnership.All, CompareOwnership.Both, CompareOwnership.OnlyMe, CompareOwnership.OnlyThem, CompareOwnership.Neither,
            };
            foreach (CompareOwnership owner in owners)
            {
                CompareOwnership chosen = owner;
                (Button button, Image bar) = FilterButton("Compare Owner " + owner, AchievementCompare.OwnershipName(owner), x, 128f, 136f);
                button.onClick.AddListener(() => { _owner = chosen; Redraw(); });
                _ownerButtons.Add((owner, button, bar));
                x += 142f;
            }

            x = 0f;
            (AchievementTags? Tag, string Caption)[] tags =
            {
                (null, "ALL KINDS"), (AchievementTags.Online, "ONLINE"), (AchievementTags.Practice, "PRACTICE"),
                (AchievementTags.Night, "NIGHT MODE"), (AchievementTags.Hidden, "HIDDEN"),
            };
            foreach ((AchievementTags? tag, string caption) in tags)
            {
                AchievementTags? chosen = tag;
                (Button button, Image bar) = FilterButton("Compare Kind " + caption, caption, x, 176f, 150f);
                button.onClick.AddListener(() => { _tag = chosen; Redraw(); });
                _tagButtons.Add((tag, button, bar));
                x += 158f;
            }

            _sortButton = Ui.Button(_body, "Compare Sort", string.Empty, UiStyle.Secondary, new Vector2(260f, 40f), "sliders");
            Ui.TopLeft((RectTransform)_sortButton.transform, new Vector2(Width - 260f, 176f), new Vector2(260f, 40f));
            _sortButton.onClick.AddListener(() =>
            {
                _sort = (AchievementSort)(((int)_sort + 1) % 4);
                Redraw();
            });
        }

        private (Button, Image) FilterButton(string name, string caption, float x, float y, float width)
        {
            Button button = Ui.Button(_body, name, caption, UiStyle.Command, new Vector2(width, 40f));
            Ui.TopLeft((RectTransform)button.transform, new Vector2(x, y), new Vector2(width, 40f));
            Image bar = Ui.Fill(button.transform, "Active", UiStyle.Orange);
            bar.rectTransform.anchorMin = new Vector2(0f, 0f);
            bar.rectTransform.anchorMax = new Vector2(1f, 0f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0f);
            bar.rectTransform.sizeDelta = new Vector2(-12f, 3f);
            bar.rectTransform.anchoredPosition = new Vector2(0f, 3f);
            return (button, bar);
        }

        private static RowView BuildRow(RectTransform list)
        {
            var row = new RowView();
            row.Backing = Ui.Fill(list, "Compare Row", Color.clear);
            row.Rect = row.Backing.rectTransform;
            row.Rect.sizeDelta = new Vector2(Width - 16f, RowHeight);

            float middle = SideWidth + 16f;
            float middleWidth = Width - 16f - 2f * middle;
            row.Badge = Ui.Art(row.Rect, "Badge", null, Color.white);
            Ui.TopLeft(row.Badge.rectTransform, new Vector2(middle, 9f), new Vector2(52f, 52f));
            row.Title = Ui.Label(row.Rect, "Title", string.Empty, 17, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(row.Title.rectTransform, new Vector2(middle + 62f, 8f), new Vector2(middleWidth - 62f, 24f));
            row.Rule = Ui.Label(row.Rect, "Rule", string.Empty, 12, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.UpperLeft);
            Ui.TopLeft(row.Rule.rectTransform, new Vector2(middle + 62f, 32f), new Vector2(middleWidth - 62f, 34f));

            row.MineText = Ui.Label(row.Rect, "Mine", string.Empty, 15, Ui.Weight.Bold, UiStyle.Ink, TextAnchor.MiddleRight);
            Ui.TopLeft(row.MineText.rectTransform, new Vector2(16f, 10f), new Vector2(SideWidth - 16f, 24f));
            row.MineTrack = Ui.Fill(row.Rect, "Mine Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(row.MineTrack.rectTransform, new Vector2(SideWidth - BarWidth, 44f), new Vector2(BarWidth, 6f));
            row.MineFill = Ui.Fill(row.MineTrack.transform, "Fill", MineInk);
            row.MineFill.rectTransform.anchorMin = new Vector2(1f, 0f);
            row.MineFill.rectTransform.anchorMax = Vector2.one;
            row.MineFill.rectTransform.pivot = new Vector2(1f, 0.5f);
            row.MineFill.rectTransform.anchoredPosition = Vector2.zero;

            float right = Width - 16f - SideWidth;
            row.TheirText = Ui.Label(row.Rect, "Theirs", string.Empty, 15, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(row.TheirText.rectTransform, new Vector2(right, 10f), new Vector2(SideWidth - 16f, 24f));
            row.TheirTrack = Ui.Fill(row.Rect, "Their Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(row.TheirTrack.rectTransform, new Vector2(right, 44f), new Vector2(BarWidth, 6f));
            row.TheirFill = Ui.Fill(row.TheirTrack.transform, "Fill", TheirInk);
            row.TheirFill.rectTransform.anchorMin = Vector2.zero;
            row.TheirFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            row.TheirFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            row.TheirFill.rectTransform.anchoredPosition = Vector2.zero;
            return row;
        }

        // ------------------------------------------------------------------ state

        private void Redraw()
        {
            foreach ((AchievementTier? tier, Button button, Image bar) in _tierButtons) Mark(button, bar, tier == _tier);
            foreach ((CompareOwnership owner, Button button, Image bar) in _ownerButtons) Mark(button, bar, owner == _owner);
            foreach ((AchievementTags? tag, Button button, Image bar) in _tagButtons) Mark(button, bar, tag == _tag);
            Ui.SetCaption(_sortButton, "SORT: " + AchievementBoard.SortName(_sort));

            var shown = new List<ComparePair>(_pairs.Count);
            foreach (ComparePair pair in _pairs)
                if (AchievementCompare.Passes(pair, _tier, _owner, _tag)) shown.Add(pair);
            AchievementCompare.Sort(shown, _sort);

            for (int i = 0; i < _rows.Count; i++)
            {
                RowView row = _rows[i];
                bool visible = i < shown.Count;
                row.Rect.gameObject.SetActive(visible);
                if (!visible) continue;
                Bind(row, shown[i], i);
                Ui.TopLeft(row.Rect, new Vector2(0f, i * RowPitch), new Vector2(Width - 16f, RowHeight));
            }
            _list.sizeDelta = new Vector2(0f, shown.Count * RowPitch);
            _empty.gameObject.SetActive(shown.Count == 0);
        }

        private static void Mark(Button button, Image bar, bool on)
        {
            bar.enabled = on;
            button.interactable = !on;
        }

        private static void Bind(RowView row, in ComparePair pair, int stripe)
        {
            Achievement achievement = pair.Achievement;
            Color metal = AchievementArt.TierColour(achievement.Tier);
            bool me = pair.IHold;
            bool them = pair.TheyHold;

            // A row only one player holds takes that player's colour, faintly.
            row.Backing.color = me && !them ? UiStyle.WithAlpha(MineInk, 0.1f)
                : them && !me ? UiStyle.WithAlpha(TheirInk, 0.1f)
                : stripe % 2 == 0 ? UiStyle.RowEven : UiStyle.RowOdd;

            bool revealed = pair.Mine.IsRevealed;
            row.Badge.sprite = AchievementArt.Badge(achievement, revealed);
            row.Badge.color = me || them || !revealed ? Color.white : AchievementArt.LockedTint;
            row.Title.text = achievement.Title;
            row.Title.color = me || them ? metal : UiStyle.Ink;
            row.Rule.text = AchievementBoard.DescriptionText(pair.Mine);
            row.Rule.fontStyle = revealed ? FontStyle.Normal : FontStyle.Italic;

            BindSide(pair, mine: true, row.MineText, row.MineTrack, row.MineFill, me);
            BindSide(pair, mine: false, row.TheirText, row.TheirTrack, row.TheirFill, them);
        }

        private static void BindSide(in ComparePair pair, bool mine, Text text, Image track, Image fill, bool held)
        {
            string said = AchievementCompare.SideText(pair, mine);
            text.text = held ? "✓  " + said : said;
            text.color = held ? (mine ? MineInk : TheirInk)
                : !mine && pair.Classified ? UiStyle.Faint
                : said == "NOT YET" ? UiStyle.Faint
                : UiStyle.Ink;
            float done = AchievementCompare.SideFraction(pair, mine);
            track.gameObject.SetActive(done >= 0f);
            if (done >= 0f) fill.rectTransform.sizeDelta = new Vector2(BarWidth * done, 0f);
        }
    }
}
