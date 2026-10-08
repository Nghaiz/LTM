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
    /// The fifty achievements (owner's list of 2026-10-09, item 4): a badge, a name and one line
    /// each, the share of players who hold it, and the player's own progress -- commonest first,
    /// as a store's global achievement list reads. Opened from the main menu and the Esc menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The model is <see cref="AchievementBoard"/>;</b> this draws it. What the player holds
    /// comes from <see cref="AchievementLedger"/>: the master's answer for the signed-in account
    /// and the practice achievements this machine saw. Offline it still lists all fifty, with the
    /// practice ones earned here, and says the shares need a sign-in.
    /// </para>
    /// <para>
    /// <b>Hidden ones</b> are drawn as "???" behind a sealed badge until earned, as their
    /// <see cref="Achievement.Hidden"/> asks; their share still shows, so a player can see a
    /// secret is common without learning what it is.
    /// </para>
    /// </remarks>
    public sealed class AchievementsPage : OverlayPageView
    {
        private const float ContentWidth = 1552f;
        private const float ListTop = 152f;
        private const float ListHeight = 520f;
        private const float CardGap = 20f;
        private const float CardHeight = 112f;
        private const float CardPitch = 124f;
        private const float FilterHeight = 40f;

        private enum EarnedFilter { All, Earned, Locked }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => OverlayHost.RegisterPage<AchievementsPage>(OverlayPage.Achievements);

        private sealed class Card
        {
            public RectTransform Rect = null!;
            public AngularPanel Face = null!;
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
        }

        private readonly List<Card> _cards = new List<Card>();
        private readonly List<(EarnedFilter Filter, Button Button, Image Bar)> _showButtons = new List<(EarnedFilter, Button, Image)>();
        private readonly List<(AchievementCategory? Category, Button Button, Image Bar)> _categoryButtons =
            new List<(AchievementCategory?, Button, Image)>();
        private readonly List<(AchievementTier Tier, Text Count)> _tierCounts = new List<(AchievementTier, Text)>();

        private RectTransform? _list;
        private ScrollRect? _scroll;
        private Text? _count;
        private Image? _bar;
        private Text? _percent;
        private Text? _status;
        private Button? _refresh;
        private EarnedFilter _show = EarnedFilter.All;
        private AchievementCategory? _category;
        private bool _loading;
        private string _error = string.Empty;
        private bool _forTool;
        private Ironfront.MasterClient.AchievementState? _toolState;
        private ICollection<string>? _toolLocal;

        public override OverlayPage Page => OverlayPage.Achievements;
        public override string Title => "ACHIEVEMENTS";
        public override string Kicker => "SERVICE RECORD // HONORS AND FEATS";
        public override string Subtitle => "Fifty to earn online and in practice. The rarest are the ones fewest players hold.";
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

        public override void OnShown()
        {
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
            Redraw();
            _ = LoadAsync();
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

            _count = Ui.Label(rect, "Count", string.Empty, 46, Ui.Weight.Black, UiStyle.Amber);
            Ui.TopLeft(_count.rectTransform, new Vector2(84f, 2f), new Vector2(260f, 60f));
            _count.horizontalOverflow = HorizontalWrapMode.Overflow;
            Text caption = Ui.Label(rect, "Caption", "ACHIEVEMENTS EARNED", 12, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(caption.rectTransform, new Vector2(86f, 60f), new Vector2(260f, 18f));

            Image track = Ui.Fill(rect, "Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.35f));
            Ui.TopLeft(track.rectTransform, new Vector2(340f, 40f), new Vector2(380f, 8f));
            _bar = Ui.Fill(track.transform, "Fill", UiStyle.Amber);
            _bar.rectTransform.anchorMin = Vector2.zero;
            _bar.rectTransform.anchorMax = new Vector2(0f, 1f);
            _bar.rectTransform.pivot = new Vector2(0f, 0.5f);
            _bar.rectTransform.anchoredPosition = Vector2.zero;
            _percent = Ui.Label(rect, "Percent", string.Empty, 18, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(_percent.rectTransform, new Vector2(734f, 30f), new Vector2(90f, 28f));
            Text complete = Ui.Label(rect, "Complete", "COMPLETE", 11, Ui.Weight.Bold, UiStyle.Faint);
            Ui.TopLeft(complete.rectTransform, new Vector2(340f, 54f), new Vector2(200f, 18f));

            Image rule = Ui.Fill(rect, "Rule", UiStyle.WithAlpha(UiStyle.Hairline, 0.45f));
            Ui.TopLeft(rule.rectTransform, new Vector2(846f, 16f), new Vector2(1f, 56f));

            AchievementTier[] tiers = { AchievementTier.Bronze, AchievementTier.Silver, AchievementTier.Gold, AchievementTier.Platinum };
            for (int i = 0; i < tiers.Length; i++)
            {
                AchievementTier tier = tiers[i];
                Color metal = AchievementArt.TierColour(tier);
                float x = 876f + i * 168f;
                Image medal = Ui.Icon(rect, "Medal " + tier, "medal", metal);
                Ui.TopLeft(medal.rectTransform, new Vector2(x, 24f), new Vector2(38f, 38f));
                Text name = Ui.Label(rect, "Tier " + tier, AchievementBoard.TierName(tier), 12, Ui.Weight.Bold, metal);
                Ui.TopLeft(name.rectTransform, new Vector2(x + 46f, 18f), new Vector2(116f, 18f));
                Text count = Ui.Label(rect, "Tier Count " + tier, string.Empty, 22, Ui.Weight.Black, UiStyle.Ink);
                Ui.TopLeft(count.rectTransform, new Vector2(x + 46f, 36f), new Vector2(116f, 32f));
                _tierCounts.Add((tier, count));
            }
        }

        private void BuildFilters(RectTransform content)
        {
            float x = 0f;
            (EarnedFilter Filter, string Caption)[] shows = { (EarnedFilter.All, "ALL"), (EarnedFilter.Earned, "EARNED"), (EarnedFilter.Locked, "LOCKED") };
            foreach ((EarnedFilter show, string caption) in shows)
            {
                EarnedFilter chosen = show;
                (Button button, Image bar) = FilterButton(content, "Filter " + caption, caption, x, 112f);
                button.onClick.AddListener(() =>
                {
                    _show = chosen;
                    Redraw();
                });
                _showButtons.Add((show, button, bar));
                x += 120f;
            }

            x += 24f;
            (AchievementCategory? Category, string Caption)[] categories =
            {
                (null, "ALL TYPES"),
                (AchievementCategory.Multiplayer, AchievementCatalog.CategoryName(AchievementCategory.Multiplayer)),
                (AchievementCategory.Combat, AchievementCatalog.CategoryName(AchievementCategory.Combat)),
                (AchievementCategory.Vehicles, AchievementCatalog.CategoryName(AchievementCategory.Vehicles)),
                (AchievementCategory.Honor, AchievementCatalog.CategoryName(AchievementCategory.Honor)),
                (AchievementCategory.Hard, AchievementCatalog.CategoryName(AchievementCategory.Hard)),
                (AchievementCategory.Secret, AchievementCatalog.CategoryName(AchievementCategory.Secret)),
                (AchievementCategory.Practice, AchievementCatalog.CategoryName(AchievementCategory.Practice)),
            };
            foreach ((AchievementCategory? category, string caption) in categories)
            {
                AchievementCategory? chosen = category;
                (Button button, Image bar) = FilterButton(content, "Type " + caption, caption, x, 128f);
                button.onClick.AddListener(() =>
                {
                    _category = chosen;
                    Redraw();
                });
                _categoryButtons.Add((category, button, bar));
                x += 136f;
            }
        }

        private static (Button, Image) FilterButton(RectTransform content, string name, string caption, float x, float width)
        {
            Button button = Ui.Button(content, name, caption, UiStyle.Secondary, new Vector2(width, FilterHeight));
            Ui.TopLeft((RectTransform)button.transform, new Vector2(x, 100f), new Vector2(width, FilterHeight));
            Image bar = Ui.Fill(button.transform, "Active", UiStyle.Orange);
            bar.rectTransform.anchorMin = new Vector2(0f, 0f);
            bar.rectTransform.anchorMax = new Vector2(1f, 0f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0f);
            bar.rectTransform.sizeDelta = new Vector2(-12f, 3f);
            bar.rectTransform.anchoredPosition = new Vector2(0f, 3f);
            return (button, bar);
        }

        private static Card BuildCard(RectTransform list, float width)
        {
            var card = new Card();
            card.Face = Ui.Panel(list, "Card", Color.white, 10f, Color.white, AngularEdge.All, 1.5f);
            card.Rect = (RectTransform)card.Face.transform;
            card.Rect.sizeDelta = new Vector2(width, CardHeight);

            card.Accent = Ui.Fill(card.Rect, "Accent", Color.white);
            Ui.TopLeft(card.Accent.rectTransform, new Vector2(3f, 14f), new Vector2(3f, CardHeight - 28f));

            card.Badge = Ui.Art(card.Rect, "Badge", null, Color.white);
            Ui.TopLeft(card.Badge.rectTransform, new Vector2(16f, 14f), new Vector2(84f, 84f));
            card.Lock = Ui.Icon(card.Rect, "Lock", "lock", UiStyle.Ink);
            Ui.TopLeft(card.Lock.rectTransform, new Vector2(78f, 74f), new Vector2(24f, 24f));

            card.Title = Ui.Label(card.Rect, "Title", string.Empty, 19, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(card.Title.rectTransform, new Vector2(116f, 12f), new Vector2(450f, 28f));
            card.Tag = Ui.Label(card.Rect, "Tag", string.Empty, 12, Ui.Weight.Bold, UiStyle.Faint);
            Ui.TopLeft(card.Tag.rectTransform, new Vector2(116f, 39f), new Vector2(450f, 18f));
            card.Blurb = Ui.Label(card.Rect, "Blurb", string.Empty, 14, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.UpperLeft);
            Ui.TopLeft(card.Blurb.rectTransform, new Vector2(116f, 59f), new Vector2(450f, 28f));

            card.Track = Ui.Fill(card.Rect, "Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(card.Track.rectTransform, new Vector2(116f, 94f), new Vector2(300f, 5f));
            card.Fill = Ui.Fill(card.Track.transform, "Fill", UiStyle.CyanSoft);
            card.Fill.rectTransform.anchorMin = Vector2.zero;
            card.Fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            card.Fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            card.Fill.rectTransform.anchoredPosition = Vector2.zero;
            card.Count = Ui.Label(card.Rect, "Count", string.Empty, 12, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(card.Count.rectTransform, new Vector2(426f, 86f), new Vector2(150f, 20f));

            card.Earned = Ui.Label(card.Rect, "Earned", string.Empty, 12, Ui.Weight.Bold, UiStyle.Green);
            Ui.TopLeft(card.Earned.rectTransform, new Vector2(116f, 86f), new Vector2(300f, 20f));

            Image divider = Ui.Fill(card.Rect, "Divider", UiStyle.WithAlpha(UiStyle.Hairline, 0.4f));
            Ui.TopLeft(divider.rectTransform, new Vector2(width - 186f, 16f), new Vector2(1f, CardHeight - 32f));

            card.Share = Ui.Label(card.Rect, "Share", string.Empty, 28, Ui.Weight.Black, UiStyle.Ink, TextAnchor.MiddleRight);
            Ui.TopLeft(card.Share.rectTransform, new Vector2(width - 176f, 18f), new Vector2(156f, 38f));
            card.Rarity = Ui.Label(card.Rect, "Rarity", string.Empty, 11, Ui.Weight.Bold, UiStyle.Faint, TextAnchor.MiddleRight);
            Ui.TopLeft(card.Rarity.rectTransform, new Vector2(width - 176f, 56f), new Vector2(156f, 18f));
            Image shareTrack = Ui.Fill(card.Rect, "Share Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(shareTrack.rectTransform, new Vector2(width - 156f, 84f), new Vector2(136f, 4f));
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

            List<AchievementEntry> entries = _forTool
                ? AchievementBoard.Build(_toolState, _toolLocal ?? new HashSet<string>())
                : AchievementBoard.Build(AchievementLedger.CurrentState, AchievementLedger.LocalEarned);
            int earned = AchievementBoard.EarnedCount(entries);

            if (_count != null)
                _count.text = earned.ToString(CultureInfo.InvariantCulture) + "<size=24><color=#8DA8BA> / "
                              + entries.Count.ToString(CultureInfo.InvariantCulture) + "</color></size>";
            float done = entries.Count > 0 ? earned / (float)entries.Count : 0f;
            if (_bar != null) _bar.rectTransform.sizeDelta = new Vector2(380f * done, 0f);
            if (_percent != null) _percent.text = Mathf.RoundToInt(done * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            foreach ((AchievementTier tier, Text count) in _tierCounts)
            {
                int have = 0, all = 0;
                foreach (AchievementEntry entry in entries)
                {
                    if (entry.Achievement.Tier != tier) continue;
                    all++;
                    if (entry.Earned) have++;
                }
                count.text = have.ToString(CultureInfo.InvariantCulture) + "<size=15><color=#8DA8BA> / "
                             + all.ToString(CultureInfo.InvariantCulture) + "</color></size>";
            }

            foreach ((EarnedFilter show, Button button, Image bar) in _showButtons)
            {
                bool on = show == _show;
                bar.enabled = on;
                button.interactable = !on;
            }
            foreach ((AchievementCategory? category, Button button, Image bar) in _categoryButtons)
            {
                bool on = category == _category;
                bar.enabled = on;
                button.interactable = !on;
            }

            float width = (ContentWidth - CardGap) * 0.5f;
            int shown = 0;
            for (int i = 0; i < _cards.Count; i++)
            {
                Card card = _cards[i];
                bool visible = i < entries.Count && Passes(entries[i]);
                card.Rect.gameObject.SetActive(visible);
                if (!visible) continue;

                Bind(card, entries[i]);
                Ui.TopLeft(card.Rect, new Vector2((shown % 2) * (width + CardGap), (shown / 2) * CardPitch),
                    new Vector2(width, CardHeight));
                shown++;
            }
            _list.sizeDelta = new Vector2(0f, ((shown + 1) / 2) * CardPitch);

            if (_status != null) _status.text = StatusText(entries);
            if (_refresh != null) _refresh.interactable = !_loading && AchievementLedger.SignedIn;
        }

        private bool Passes(in AchievementEntry entry)
        {
            if (_show == EarnedFilter.Earned && !entry.Earned) return false;
            if (_show == EarnedFilter.Locked && entry.Earned) return false;
            return _category == null || entry.Achievement.Category == _category;
        }

        private string StatusText(List<AchievementEntry> entries)
        {
            if (_loading) return "Asking the master server…";
            if (_error.Length > 0) return _error;
            if (_forTool ? _toolState == null : !AchievementLedger.SignedIn)
                return "Offline: practice achievements only. Sign in to multiplayer to track your career and see how rare each one is.";

            long players = entries.Count > 0 ? entries[0].Players : 0;
            return players > 0
                ? "Share of " + players.ToString("N0", CultureInfo.InvariantCulture) + " players with a career, commonest first."
                : "No one has a career yet. Play an online match to start yours.";
        }

        private static void Bind(Card card, in AchievementEntry entry)
        {
            Achievement achievement = entry.Achievement;
            Color metal = AchievementArt.TierColour(achievement.Tier);
            bool earned = entry.Earned;
            bool revealed = entry.IsRevealed;

            card.Face.color = earned ? new Color(0.04f, 0.12f, 0.19f, 0.94f) : new Color(0.025f, 0.07f, 0.11f, 0.86f);
            card.Face.Configure(10f, AngularEdge.All, 1.5f,
                earned ? UiStyle.WithAlpha(metal, 0.75f) : UiStyle.WithAlpha(UiStyle.Hairline, 0.32f));
            card.Accent.color = metal;
            card.Accent.enabled = earned;

            card.Badge.sprite = AchievementArt.Badge(achievement, revealed);
            card.Badge.color = earned ? Color.white : revealed ? AchievementArt.LockedTint : new Color(0.62f, 0.68f, 0.76f, 1f);
            card.Lock.enabled = !earned;

            card.Title.text = revealed ? achievement.Title : "???";
            card.Title.color = earned ? UiStyle.Ink : UiStyle.WithAlpha(UiStyle.Ink, 0.72f);
            card.Tag.text = revealed
                ? AchievementCatalog.CategoryName(achievement.Category) + "  ·  " + AchievementBoard.TierName(achievement.Tier)
                : AchievementCatalog.CategoryName(AchievementCategory.Secret) + "  ·  HIDDEN";
            card.Tag.color = earned ? metal : UiStyle.Faint;
            card.Blurb.text = revealed ? achievement.Description : "A hidden achievement. Keep playing to reveal it.";

            bool progress = entry.ShowsProgress;
            card.Track.gameObject.SetActive(progress);
            card.Count.gameObject.SetActive(progress);
            if (progress)
            {
                card.Fill.rectTransform.sizeDelta = new Vector2(300f * AchievementBoard.ProgressFraction(entry), 0f);
                card.Count.text = AchievementBoard.ProgressText(entry);
            }
            card.Earned.text = AchievementBoard.EarnedText(entry);

            card.Share.text = AchievementBoard.ShareText(entry);
            card.Share.color = earned ? metal : UiStyle.Ink;
            card.Rarity.text = AchievementBoard.RarityText(entry);
            card.ShareFill.transform.parent.gameObject.SetActive(entry.HasShare);
            card.ShareFill.rectTransform.sizeDelta = new Vector2(136f * (float)entry.Share, 0f);
            card.ShareFill.color = earned ? metal : UiStyle.WithAlpha(UiStyle.CyanSoft, 0.8f);
        }
    }
}
