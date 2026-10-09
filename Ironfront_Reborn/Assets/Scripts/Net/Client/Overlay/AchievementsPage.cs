#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The eighty achievements (achievements v2, <c>docs/achievements.md</c> section 6.1): one list,
    /// no families, filtered by metal, by state and by kind, and sorted by difficulty, rarity, what
    /// was earned last or what is nearest. A card opens its details: the rule, the parts done, the
    /// share of players, who earned a Mythic first. Opened from the main menu and the Esc menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The model is <see cref="AchievementBoard"/>;</b> this draws it. What the player holds
    /// comes from <see cref="AchievementLedger"/> (the master's answer and the practice
    /// achievements this machine saw), and practice progress from <see cref="PracticeFeats"/>.
    /// </para>
    /// <para>
    /// <b>Hidden ones</b> show their name, the black silhouette of their badge and a hint, never
    /// their rule, until earned.
    /// </para>
    /// </remarks>
    public sealed class AchievementsPage : OverlayPageView
    {
        private const float ContentWidth = 1552f;
        private const float ListTop = 200f;
        private const float ListHeight = 472f;
        private const float CardGap = 20f;
        private const float CardHeight = 122f;
        private const float CardPitch = 134f;
        private const int Embers = 6;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => OverlayHost.RegisterPage<AchievementsPage>(OverlayPage.Achievements);

        private sealed class Card
        {
            public RectTransform Rect = null!;
            public AngularPanel Face = null!;
            public Button Button = null!;
            public Image Accent = null!;
            public Image Badge = null!;
            public Image Lock = null!;
            public Text Title = null!;
            public Text Tag = null!;
            public Text Blurb = null!;
            public Image Track = null!;
            public Image Fill = null!;
            public Text Count = null!;
            public Text Earned = null!;
            public Text Share = null!;
            public Text Rarity = null!;
            public Image ShareFill = null!;
            public Image[] Embers = null!;
            public bool Mythic;
            public int EntryIndex = -1;
        }

        private readonly List<Card> _cards = new List<Card>();
        private readonly List<(AchievementTier? Tier, Button Button, Image Bar)> _tierButtons = new List<(AchievementTier?, Button, Image)>();
        private readonly List<(AchievementStatus Status, Button Button, Image Bar)> _statusButtons = new List<(AchievementStatus, Button, Image)>();
        private readonly List<(AchievementTags? Tag, Button Button, Image Bar)> _tagButtons = new List<(AchievementTags?, Button, Image)>();
        private readonly List<(AchievementTier Tier, Text Count)> _tierCounts = new List<(AchievementTier, Text)>();
        private List<AchievementEntry> _entries = new List<AchievementEntry>();

        private RectTransform? _list;
        private ScrollRect? _scroll;
        private Text? _count;
        private Text? _points;
        private Image? _bar;
        private Text? _percent;
        private Text? _status;
        private Button? _refresh;
        private Button? _sortButton;
        private AchievementTier? _tier;
        private AchievementStatus _show = AchievementStatus.All;
        private AchievementTags? _tag;
        private AchievementSort _sort = AchievementSort.Difficulty;
        private bool _loading;
        private string _error = string.Empty;
        private bool _forTool;
        private Ironfront.MasterClient.AchievementState? _toolState;
        private ICollection<string>? _toolLocal;
        private AchievementDetailPanel? _detail;

        public override OverlayPage Page => OverlayPage.Achievements;
        public override string Title => "ACHIEVEMENTS";
        public override string Kicker => "SERVICE RECORD // HONORS AND FEATS";
        public override string Subtitle => "Eighty to earn online and in practice, Bronze to Mythic. A round counts when you play at least 5 minutes of it.";
        public override string IconName => "medal";

        public override void Build(RectTransform content, RectTransform actions)
        {
            BuildSummary(content);
            BuildFilters(content);

            RectTransform area = Ui.Child(content, "List Area");
            Ui.TopLeft(area, new Vector2(0f, ListTop), new Vector2(ContentWidth, ListHeight));
            _scroll = Ui.Scroll(area, "List", out RectTransform list);
            Ui.Stretch((RectTransform)_scroll.transform);
            _list = list;
            float width = (ContentWidth - CardGap) * 0.5f;
            for (int i = 0; i < AchievementCatalog.All.Count; i++) _cards.Add(BuildCard(list, width));

            _detail = AchievementDetailPanel.Build(content, new Vector2(0f, ListTop), new Vector2(ContentWidth, ListHeight));

            _status = Ui.Label(actions, "Status", string.Empty, 14, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.MiddleRight);
            Ui.TopLeft(_status.rectTransform, new Vector2(0f, 0f), new Vector2(740f, 52f));
            _refresh = Ui.Button(actions, "Refresh", "REFRESH", UiStyle.Secondary, new Vector2(240f, 52f), "refresh");
            Ui.TopLeft((RectTransform)_refresh.transform, new Vector2(760f, 0f), new Vector2(240f, 52f));
            _refresh.onClick.AddListener(() => _ = LoadAsync());

            AchievementLedger.Changed += Redraw;
            Redraw();
        }

        private void OnDestroy() => AchievementLedger.Changed -= Redraw;

        /// <summary>Draws <paramref name="state"/> and <paramref name="local"/> with no master or ledger. For Editor tools.</summary>
        public void ShowForTool(Ironfront.MasterClient.AchievementState? state, ICollection<string> local)
        {
            _forTool = true;
            _toolState = state;
            _toolLocal = local;
            Redraw();
        }

        /// <summary>Opens the details of the achievement <paramref name="id"/>. For Editor tools.</summary>
        public void ShowDetailForTool(string id)
        {
            int index = _entries.FindIndex(e => e.Achievement.Id == id);
            if (index >= 0) _detail?.Show(_entries[index]);
        }

        public override void OnShown()
        {
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
            _detail?.Hide();
            Redraw();
            _ = LoadAsync();
        }

        public override bool HandleEscape()
        {
            if (_detail == null || !_detail.IsOpen) return false;
            _detail.Hide();
            return true;
        }

        private void Update()
        {
            // The Mythic cards' embers: a few sparks rising slowly through the frame.
            float t = Time.unscaledTime;
            for (int i = 0; i < _cards.Count; i++)
            {
                Card card = _cards[i];
                if (!card.Mythic || !card.Rect.gameObject.activeInHierarchy) continue;
                for (int e = 0; e < card.Embers.Length; e++)
                {
                    float phase = (t * (0.18f + 0.04f * e) + e * 0.37f) % 1f;
                    RectTransform rect = card.Embers[e].rectTransform;
                    float x = 20f + ((e * 97f) % (card.Rect.sizeDelta.x - 40f)) + Mathf.Sin(t * 1.3f + e) * 6f;
                    rect.anchoredPosition = new Vector2(x, -CardHeight + 6f + phase * (CardHeight - 12f));
                    Color c = card.Embers[e].color;
                    c.a = Mathf.Sin(phase * Mathf.PI) * 0.75f;
                    card.Embers[e].color = c;
                }
            }
        }

        // ------------------------------------------------------------------ layout

        private void BuildSummary(RectTransform content)
        {
            AngularPanel strip = Ui.Panel(content, "Summary", UiStyle.WithAlpha(UiStyle.Hex("061827"), 0.86f), 12f,
                UiStyle.WithAlpha(UiStyle.Hairline, 0.55f));
            var rect = (RectTransform)strip.transform;
            Ui.TopLeft(rect, Vector2.zero, new Vector2(ContentWidth, 88f));

            Image trophy = Ui.Icon(rect, "Trophy", "trophy", UiStyle.Amber);
            Ui.TopLeft(trophy.rectTransform, new Vector2(24f, 22f), new Vector2(44f, 44f));

            _count = Ui.Label(rect, "Count", string.Empty, 44, Ui.Weight.Black, UiStyle.Amber);
            Ui.TopLeft(_count.rectTransform, new Vector2(82f, 4f), new Vector2(250f, 56f));
            _count.horizontalOverflow = HorizontalWrapMode.Overflow;
            Text caption = Ui.Label(rect, "Caption", "ACHIEVEMENTS EARNED", 12, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(caption.rectTransform, new Vector2(84f, 60f), new Vector2(250f, 18f));

            Image track = Ui.Fill(rect, "Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.35f));
            Ui.TopLeft(track.rectTransform, new Vector2(300f, 30f), new Vector2(300f, 8f));
            _bar = Ui.Fill(track.transform, "Fill", UiStyle.Amber);
            _bar.rectTransform.anchorMin = Vector2.zero;
            _bar.rectTransform.anchorMax = new Vector2(0f, 1f);
            _bar.rectTransform.pivot = new Vector2(0f, 0.5f);
            _bar.rectTransform.anchoredPosition = Vector2.zero;
            _percent = Ui.Label(rect, "Percent", string.Empty, 16, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(_percent.rectTransform, new Vector2(610f, 20f), new Vector2(70f, 28f));
            _points = Ui.Label(rect, "Points", string.Empty, 15, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(_points.rectTransform, new Vector2(300f, 46f), new Vector2(380f, 24f));

            Image rule = Ui.Fill(rect, "Rule", UiStyle.WithAlpha(UiStyle.Hairline, 0.45f));
            Ui.TopLeft(rule.rectTransform, new Vector2(700f, 16f), new Vector2(1f, 56f));

            AchievementTier[] tiers =
            {
                AchievementTier.Bronze, AchievementTier.Silver, AchievementTier.Gold, AchievementTier.Platinum, AchievementTier.Mythic,
            };
            for (int i = 0; i < tiers.Length; i++)
            {
                AchievementTier tier = tiers[i];
                Color metal = AchievementArt.TierColour(tier);
                float x = 724f + i * 164f;
                Image medal = Ui.Icon(rect, "Medal " + tier, "medal", metal);
                Ui.TopLeft(medal.rectTransform, new Vector2(x, 24f), new Vector2(38f, 38f));
                Text name = Ui.Label(rect, "Tier " + tier, AchievementBoard.TierName(tier), 12, Ui.Weight.Bold, metal);
                Ui.TopLeft(name.rectTransform, new Vector2(x + 46f, 18f), new Vector2(112f, 18f));
                Text count = Ui.Label(rect, "Tier Count " + tier, string.Empty, 22, Ui.Weight.Black, UiStyle.Ink);
                Ui.TopLeft(count.rectTransform, new Vector2(x + 46f, 36f), new Vector2(112f, 32f));
                _tierCounts.Add((tier, count));
            }
        }

        private void BuildFilters(RectTransform content)
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
                (Button button, Image bar) = FilterButton(content, "Metal " + caption, caption, x, 100f, 132f);
                if (tier != null) bar.color = AchievementArt.TierColour(tier.Value);
                button.onClick.AddListener(() => { _tier = chosen; Redraw(); });
                _tierButtons.Add((tier, button, bar));
                x += 140f;
            }

            _sortButton = Ui.Button(content, "Sort", string.Empty, UiStyle.Secondary, new Vector2(260f, 40f), "sliders");
            Ui.TopLeft((RectTransform)_sortButton.transform, new Vector2(ContentWidth - 260f, 100f), new Vector2(260f, 40f));
            _sortButton.onClick.AddListener(() =>
            {
                _sort = (AchievementSort)(((int)_sort + 1) % 4);
                Redraw();
            });

            x = 0f;
            (AchievementStatus Status, string Caption)[] states =
            {
                (AchievementStatus.All, "ALL"), (AchievementStatus.Unlocked, "UNLOCKED"),
                (AchievementStatus.Locked, "LOCKED"), (AchievementStatus.InProgress, "IN PROGRESS"),
            };
            foreach ((AchievementStatus status, string caption) in states)
            {
                AchievementStatus chosen = status;
                (Button button, Image bar) = FilterButton(content, "State " + caption, caption, x, 148f, 150f);
                button.onClick.AddListener(() => { _show = chosen; Redraw(); });
                _statusButtons.Add((status, button, bar));
                x += 158f;
            }

            x += 24f;
            (AchievementTags? Tag, string Caption)[] tags =
            {
                (null, "ALL KINDS"), (AchievementTags.Online, "ONLINE"), (AchievementTags.Practice, "PRACTICE"),
                (AchievementTags.Night, "NIGHT MODE"), (AchievementTags.Hidden, "HIDDEN"),
            };
            foreach ((AchievementTags? tag, string caption) in tags)
            {
                AchievementTags? chosen = tag;
                (Button button, Image bar) = FilterButton(content, "Kind " + caption, caption, x, 148f, 150f);
                button.onClick.AddListener(() => { _tag = chosen; Redraw(); });
                _tagButtons.Add((tag, button, bar));
                x += 158f;
            }
        }

        private static (Button, Image) FilterButton(RectTransform content, string name, string caption, float x, float y, float width)
        {
            Button button = Ui.Button(content, name, caption, UiStyle.Command, new Vector2(width, 40f));
            Ui.TopLeft((RectTransform)button.transform, new Vector2(x, y), new Vector2(width, 40f));
            Image bar = Ui.Fill(button.transform, "Active", UiStyle.Orange);
            bar.rectTransform.anchorMin = new Vector2(0f, 0f);
            bar.rectTransform.anchorMax = new Vector2(1f, 0f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0f);
            bar.rectTransform.sizeDelta = new Vector2(-12f, 3f);
            bar.rectTransform.anchoredPosition = new Vector2(0f, 3f);
            return (button, bar);
        }

        private Card BuildCard(RectTransform list, float width)
        {
            var card = new Card();
            card.Face = Ui.Panel(list, "Card", Color.white, 10f, Color.white, AngularEdge.All, 1.5f);
            card.Face.raycastTarget = true;
            card.Rect = (RectTransform)card.Face.transform;
            card.Rect.sizeDelta = new Vector2(width, CardHeight);
            card.Button = card.Face.gameObject.AddComponent<Button>();
            card.Button.targetGraphic = card.Face;
            card.Button.transition = Selectable.Transition.None;
            Card bound = card;
            card.Button.onClick.AddListener(() =>
            {
                if (bound.EntryIndex >= 0 && bound.EntryIndex < _entries.Count) _detail?.Show(_entries[bound.EntryIndex]);
            });

            card.Embers = new Image[Embers];
            for (int e = 0; e < Embers; e++)
            {
                card.Embers[e] = Ui.Fill(card.Rect, "Ember", new Color(1f, 0.42f, 0.2f, 0f));
                RectTransform rect = card.Embers[e].rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(3f, 3f);
                card.Embers[e].raycastTarget = false;
            }

            card.Accent = Ui.Fill(card.Rect, "Accent", Color.white);
            Ui.TopLeft(card.Accent.rectTransform, new Vector2(3f, 14f), new Vector2(3f, CardHeight - 28f));

            card.Badge = Ui.Art(card.Rect, "Badge", null, Color.white);
            Ui.TopLeft(card.Badge.rectTransform, new Vector2(14f, 13f), new Vector2(96f, 96f));
            card.Lock = Ui.Icon(card.Rect, "Lock", "lock", UiStyle.Ink);
            Ui.TopLeft(card.Lock.rectTransform, new Vector2(86f, 84f), new Vector2(24f, 24f));

            float text = 124f;
            float textWidth = width - text - 196f;
            card.Title = Ui.Label(card.Rect, "Title", string.Empty, 19, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(card.Title.rectTransform, new Vector2(text, 10f), new Vector2(textWidth, 26f));
            card.Tag = Ui.Label(card.Rect, "Tag", string.Empty, 11, Ui.Weight.Bold, UiStyle.Faint);
            Ui.TopLeft(card.Tag.rectTransform, new Vector2(text, 36f), new Vector2(textWidth, 18f));
            card.Blurb = Ui.Label(card.Rect, "Blurb", string.Empty, 13, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.UpperLeft);
            Ui.TopLeft(card.Blurb.rectTransform, new Vector2(text, 55f), new Vector2(textWidth, 36f));

            card.Track = Ui.Fill(card.Rect, "Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(card.Track.rectTransform, new Vector2(text, 102f), new Vector2(260f, 5f));
            card.Fill = Ui.Fill(card.Track.transform, "Fill", UiStyle.CyanSoft);
            card.Fill.rectTransform.anchorMin = Vector2.zero;
            card.Fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            card.Fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            card.Fill.rectTransform.anchoredPosition = Vector2.zero;
            card.Count = Ui.Label(card.Rect, "Count", string.Empty, 12, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(card.Count.rectTransform, new Vector2(text + 270f, 94f), new Vector2(200f, 20f));

            card.Earned = Ui.Label(card.Rect, "Earned", string.Empty, 12, Ui.Weight.Bold, UiStyle.Green);
            Ui.TopLeft(card.Earned.rectTransform, new Vector2(text, 94f), new Vector2(textWidth, 20f));

            Image divider = Ui.Fill(card.Rect, "Divider", UiStyle.WithAlpha(UiStyle.Hairline, 0.4f));
            Ui.TopLeft(divider.rectTransform, new Vector2(width - 186f, 16f), new Vector2(1f, CardHeight - 32f));

            card.Share = Ui.Label(card.Rect, "Share", string.Empty, 28, Ui.Weight.Black, UiStyle.Ink, TextAnchor.MiddleRight);
            Ui.TopLeft(card.Share.rectTransform, new Vector2(width - 176f, 20f), new Vector2(156f, 38f));
            card.Rarity = Ui.Label(card.Rect, "Rarity", string.Empty, 11, Ui.Weight.Bold, UiStyle.Faint, TextAnchor.MiddleRight);
            Ui.TopLeft(card.Rarity.rectTransform, new Vector2(width - 176f, 58f), new Vector2(156f, 18f));
            Image shareTrack = Ui.Fill(card.Rect, "Share Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(shareTrack.rectTransform, new Vector2(width - 156f, 88f), new Vector2(136f, 4f));
            card.ShareFill = Ui.Fill(shareTrack.transform, "Fill", UiStyle.Amber);
            card.ShareFill.rectTransform.anchorMin = Vector2.zero;
            card.ShareFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            card.ShareFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            card.ShareFill.rectTransform.anchoredPosition = Vector2.zero;
            return card;
        }

        // ------------------------------------------------------------------ state

        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (_loading) return;
            if (!AchievementLedger.SignedIn)
            {
                _error = string.Empty;
                Redraw();
                return;
            }

            _loading = true;
            Redraw();
            (bool ok, string error) = await AchievementLedger.RefreshAsync();
            _loading = false;
            if (this == null) return;
            _error = ok ? string.Empty : error;
            Redraw();
        }

        private void Redraw()
        {
            if (_list == null) return;

            _entries = _forTool
                ? AchievementBoard.Build(_toolState, _toolLocal ?? new HashSet<string>())
                : AchievementBoard.Build(AchievementLedger.CurrentState, AchievementLedger.LocalEarned, PracticeFeats.Progress());
            List<AchievementEntry> all = _entries;
            int earned = AchievementBoard.EarnedCount(all);

            if (_count != null)
                _count.text = earned.ToString(CultureInfo.InvariantCulture) + "<size=24><color=#8DA8BA> / "
                              + all.Count.ToString(CultureInfo.InvariantCulture) + "</color></size>";
            float done = all.Count > 0 ? earned / (float)all.Count : 0f;
            if (_bar != null) _bar.rectTransform.sizeDelta = new Vector2(300f * done, 0f);
            if (_percent != null) _percent.text = Mathf.RoundToInt(done * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            if (_points != null)
                _points.text = AchievementBoard.EarnedPoints(all).ToString("N0", CultureInfo.InvariantCulture)
                               + "<color=#8DA8BA> / " + AchievementCatalog.TotalPoints.ToString("N0", CultureInfo.InvariantCulture)
                               + " POINTS</color>";
            foreach ((AchievementTier tier, Text count) in _tierCounts)
            {
                int have = 0, total = 0;
                foreach (AchievementEntry entry in all)
                {
                    if (entry.Achievement.Tier != tier) continue;
                    total++;
                    if (entry.Earned) have++;
                }
                count.text = have.ToString(CultureInfo.InvariantCulture) + "<size=15><color=#8DA8BA> / "
                             + total.ToString(CultureInfo.InvariantCulture) + "</color></size>";
            }

            foreach ((AchievementTier? tier, Button button, Image bar) in _tierButtons) Mark(button, bar, tier == _tier);
            foreach ((AchievementStatus status, Button button, Image bar) in _statusButtons) Mark(button, bar, status == _show);
            foreach ((AchievementTags? tag, Button button, Image bar) in _tagButtons) Mark(button, bar, tag == _tag);
            if (_sortButton != null) Ui.SetCaption(_sortButton, "SORT: " + AchievementBoard.SortName(_sort));

            var ordered = new List<AchievementEntry>(all.Count);
            foreach (AchievementEntry entry in all)
                if (AchievementBoard.Passes(entry, _tier, _show, _tag)) ordered.Add(entry);
            AchievementBoard.Sort(ordered, _sort);

            float width = (ContentWidth - CardGap) * 0.5f;
            for (int i = 0; i < _cards.Count; i++)
            {
                Card card = _cards[i];
                bool visible = i < ordered.Count;
                card.Rect.gameObject.SetActive(visible);
                if (!visible)
                {
                    card.EntryIndex = -1;
                    continue;
                }

                card.EntryIndex = ordered[i].Order;
                Bind(card, ordered[i]);
                Ui.TopLeft(card.Rect, new Vector2((i % 2) * (width + CardGap), (i / 2) * CardPitch),
                    new Vector2(width, CardHeight));
            }
            _list.sizeDelta = new Vector2(0f, ((ordered.Count + 1) / 2) * CardPitch);

            if (_status != null) _status.text = StatusText(all);
            if (_refresh != null) _refresh.interactable = !_loading && AchievementLedger.SignedIn;
        }

        private static void Mark(Button button, Image bar, bool on)
        {
            bar.enabled = on;
            button.interactable = !on;
        }

        private string StatusText(List<AchievementEntry> entries)
        {
            if (_loading) return "Asking the master server…";
            if (_error.Length > 0) return _error;
            if (_forTool ? _toolState == null : !AchievementLedger.SignedIn)
                return "Offline: practice achievements only. Sign in to multiplayer to track your career and see how rare each one is.";

            long players = entries.Count > 0 ? entries[0].Players : 0;
            return players > 0
                ? "Shares are out of " + players.ToString("N0", CultureInfo.InvariantCulture) + " players with a career."
                : "No one has a career yet. Play an online match to start yours.";
        }

        private static void Bind(Card card, in AchievementEntry entry)
        {
            Achievement achievement = entry.Achievement;
            Color metal = AchievementArt.TierColour(achievement.Tier);
            bool earned = entry.Earned;
            bool revealed = entry.IsRevealed;
            card.Mythic = achievement.Tier == AchievementTier.Mythic;

            Color face = card.Mythic
                ? (earned ? new Color(0.13f, 0.03f, 0.04f, 0.96f) : new Color(0.08f, 0.03f, 0.04f, 0.92f))
                : (earned ? new Color(0.04f, 0.12f, 0.19f, 0.94f) : new Color(0.025f, 0.07f, 0.11f, 0.86f));
            card.Face.color = face;
            card.Face.Configure(10f, AngularEdge.All, card.Mythic ? 2f : 1.5f,
                card.Mythic ? UiStyle.WithAlpha(metal, earned ? 0.9f : 0.55f)
                : earned ? UiStyle.WithAlpha(metal, 0.75f) : UiStyle.WithAlpha(UiStyle.Hairline, 0.32f));
            card.Accent.color = metal;
            card.Accent.enabled = earned;
            foreach (Image ember in card.Embers) ember.enabled = card.Mythic;

            card.Badge.sprite = AchievementArt.Badge(achievement, revealed);
            card.Badge.color = earned || !revealed ? Color.white : AchievementArt.LockedTint;
            card.Lock.enabled = !earned;

            card.Title.text = achievement.Title;
            card.Title.color = earned ? UiStyle.Ink : UiStyle.WithAlpha(UiStyle.Ink, 0.72f);
            card.Tag.text = AchievementDetailPanel.TagLine(achievement);
            card.Tag.color = earned ? metal : UiStyle.Faint;
            card.Blurb.text = AchievementBoard.DescriptionText(entry);
            card.Blurb.fontStyle = revealed ? FontStyle.Normal : FontStyle.Italic;

            bool progress = entry.ShowsProgress;
            bool best = achievement.Progress == AchievementProgress.Best;
            card.Track.gameObject.SetActive(progress && !best);
            card.Count.gameObject.SetActive(progress);
            if (progress)
            {
                card.Fill.rectTransform.sizeDelta = new Vector2(260f * AchievementBoard.ProgressFraction(entry), 0f);
                card.Count.text = AchievementBoard.ProgressText(entry);
                Ui.TopLeft(card.Count.rectTransform, new Vector2(best ? 124f : 394f, 94f), new Vector2(200f, 20f));
            }
            card.Earned.text = AchievementBoard.EarnedText(entry);

            card.Share.text = AchievementBoard.ShareText(entry);
            card.Share.color = earned ? metal : UiStyle.Ink;
            card.Rarity.text = AchievementBoard.RarityText(entry);
            card.Rarity.color = AchievementDetailPanel.RarityColour(entry);
            card.ShareFill.transform.parent.gameObject.SetActive(entry.HasShare);
            card.ShareFill.rectTransform.sizeDelta = new Vector2(136f * (float)entry.Share, 0f);
            card.ShareFill.color = earned ? metal : UiStyle.WithAlpha(UiStyle.CyanSoft, 0.8f);
        }
    }
}
