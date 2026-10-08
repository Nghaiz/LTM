#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Ironfront.MasterClient;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client.Hud;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The global ranking (owner's list of 2026-10-09, item 4): the best hundred careers, twenty
    /// to a page, drawn like the Tab board -- the same column marks and colours, gold, silver and
    /// bronze for the top three, the player's own row in gold. Opened from the main menu and the
    /// Esc menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The master ranks; this draws.</b> It sends the top hundred in order (points, then kills,
    /// then fewest deaths) and the requester's own row, which is pinned under the table whenever
    /// the page on screen does not already show it -- a player at #2,345 still sees where they stand.
    /// </para>
    /// <para>
    /// Paged with the arrows in the footer, the Left/Right and Page Up/Down keys and the mouse
    /// wheel. Asked for again each time it opens, at most every <see cref="FreshSeconds"/>.
    /// </para>
    /// </remarks>
    public sealed class RankingPage : OverlayPageView
    {
        public const int RowsPerPage = 20;
        public const int MaxRows = 100;

        private const float FreshSeconds = 15f;
        private const float ContentWidth = 1552f;
        private const float BandHeight = 64f;
        private const float HeadsTop = 76f;
        private const float RowsTop = 110f;
        private const float RowHeight = 23f;
        private const float RowPitch = 24f;
        private const float RowInset = 14f;

        // Column right edges (from the table's right), widths; the name and rank sit from the left.
        private static readonly (string Caption, string Icon, Color Ink, float Right, float Width)[] Columns =
        {
            ("SCORE", "star", HudStyle.ScoreHeadInk, 14f, 110f),
            ("WIN %", "percent", HudStyle.RatioHeadInk, 150f, 84f),
            ("MATCHES", "flag", HudStyle.BoardMuted, 254f, 100f),
            ("WINS", "trophy", HudStyle.AliveInk, 374f, 80f),
            ("BEST", "crown", HudStyle.GoldInk, 474f, 80f),
            ("HEADS", "headshot", HudStyle.HeadshotInk, 574f, 90f),
            ("K/D", "ratio", HudStyle.RatioHeadInk, 684f, 84f),
            ("DEATHS", "skull", HudStyle.DeadInk, 788f, 96f),
            ("KILLS", "crosshair", HudStyle.KillsInk, 904f, 96f),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => OverlayHost.RegisterPage<RankingPage>(OverlayPage.Ranking);

        private sealed class RankingRow
        {
            public RectTransform Rect = null!;
            public Image Backing = null!;
            public Image Accent = null!;
            public Image Medal = null!;
            public Text Rank = null!;
            public Text Name = null!;
            public readonly Text[] Cells = new Text[Columns.Length];
        }

        private readonly List<RankingRow> _rows = new List<RankingRow>();
        private RankingRow? _pinned;
        private Image? _pinnedRule;
        private Text? _players;
        private Text? _you;
        private Text? _youCaption;
        private Text? _message;
        private Text? _pageText;
        private Button? _previous;
        private Button? _next;
        private Button? _refresh;
        private Leaderboard? _board;
        private int _boardPlayer;
        private float _loadedAt = float.NegativeInfinity;
        private bool _loading;
        private string _error = string.Empty;
        private int _page;
        private bool _shown;
        private bool _forTool;
        private int _toolPlayer;

        public override OverlayPage Page => OverlayPage.Ranking;
        public override string Title => "GLOBAL RANKING";
        public override string Kicker => "WORLD STANDINGS // TOP 100 SOLDIERS";
        public override string Subtitle => "Every online round counts. Ranked by points, then kills, then fewest deaths.";
        public override string IconName => "podium";

        public override void Build(RectTransform content, RectTransform actions)
        {
            BuildBand(content);

            AngularPanel table = Ui.Panel(content, "Table", HudStyle.Pane, 12f, UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            var rect = (RectTransform)table.transform;
            Ui.TopLeft(rect, new Vector2(0f, HeadsTop - 8f), new Vector2(ContentWidth, 672f - HeadsTop + 8f));

            BuildHeads(content);
            for (int i = 0; i < RowsPerPage; i++)
                _rows.Add(BuildRow(content, "RankingRow " + (i + 1).ToString(CultureInfo.InvariantCulture), RowsTop + i * RowPitch));

            Image rule = Ui.Fill(content, "Pinned Rule", UiStyle.WithAlpha(UiStyle.Gold, 0.35f));
            _pinnedRule = rule;
            Ui.TopLeft(rule.rectTransform, new Vector2(RowInset, RowsTop + RowsPerPage * RowPitch + 6f),
                new Vector2(ContentWidth - 2f * RowInset, 1f));
            _pinned = BuildRow(content, "You", RowsTop + RowsPerPage * RowPitch + 14f);

            _message = Ui.Label(content, "Message", string.Empty, 18, Ui.Weight.Bold, UiStyle.Muted, TextAnchor.MiddleCenter);
            Ui.TopLeft(_message.rectTransform, new Vector2(0f, RowsTop + 120f), new Vector2(ContentWidth, 120f));

            _previous = Ui.Button(actions, "Previous", "PREV", UiStyle.Secondary, new Vector2(150f, 52f), "arrow-left");
            Ui.TopLeft((RectTransform)_previous.transform, new Vector2(330f, 0f), new Vector2(150f, 52f));
            _previous.onClick.AddListener(() => TurnPage(-1));
            _pageText = Ui.Label(actions, "Page", string.Empty, 16, Ui.Weight.Bold, UiStyle.Ink, TextAnchor.MiddleCenter);
            Ui.TopLeft(_pageText.rectTransform, new Vector2(486f, 0f), new Vector2(118f, 52f));
            _next = Ui.Button(actions, "Next", "NEXT", UiStyle.Secondary, new Vector2(150f, 52f), "arrow-right");
            Ui.TopLeft((RectTransform)_next.transform, new Vector2(610f, 0f), new Vector2(150f, 52f));
            _next.onClick.AddListener(() => TurnPage(1));
            _refresh = Ui.Button(actions, "Refresh", "REFRESH", UiStyle.Secondary, new Vector2(220f, 52f), "refresh");
            Ui.TopLeft((RectTransform)_refresh.transform, new Vector2(780f, 0f), new Vector2(220f, 52f));
            _refresh.onClick.AddListener(() => _ = LoadAsync(force: true));

            Redraw();
        }

        public override void OnShown()
        {
            _shown = true;
            _page = 0;
            Redraw();
            _ = LoadAsync(force: false);
        }

        public override void OnHidden() => _shown = false;

        /// <summary>Draws <paramref name="board"/> as <paramref name="player"/> would see it, with no master. For Editor tools.</summary>
        public void ShowForTool(Leaderboard board, int player, int page)
        {
            _forTool = true;
            _toolPlayer = player;
            _board = board;
            _boardPlayer = player;
            _page = page;
            Redraw();
        }

        private void Update()
        {
            if (!_shown) return;
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.PageDown)) TurnPage(1);
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.PageUp)) TurnPage(-1);
            float wheel = Input.mouseScrollDelta.y;
            if (wheel < -0.01f) TurnPage(1);
            else if (wheel > 0.01f) TurnPage(-1);
        }

        // ------------------------------------------------------------------ layout

        private void BuildBand(RectTransform content)
        {
            AngularPanel band = Ui.Panel(content, "Band", UiStyle.WithAlpha(UiStyle.Hex("0B2A44"), 0.92f), 12f,
                UiStyle.WithAlpha(UiStyle.Cyan, 0.55f));
            var rect = (RectTransform)band.transform;
            Ui.TopLeft(rect, Vector2.zero, new Vector2(ContentWidth, BandHeight));

            Image podium = Ui.Icon(rect, "Podium", "podium", UiStyle.Amber);
            Ui.TopLeft(podium.rectTransform, new Vector2(20f, 12f), new Vector2(40f, 40f));
            Text heading = Ui.Label(rect, "Heading", "TOP 100", 30, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(heading.rectTransform, new Vector2(74f, 4f), new Vector2(220f, 40f));
            _players = Ui.Label(rect, "Players", string.Empty, 14, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(_players.rectTransform, new Vector2(76f, 40f), new Vector2(600f, 20f));

            _youCaption = Ui.Label(rect, "Your Rank Caption", "YOUR RANK", 12, Ui.Weight.Bold, UiStyle.CyanSoft, TextAnchor.MiddleRight);
            Ui.TopLeft(_youCaption.rectTransform, new Vector2(ContentWidth - 520f, 8f), new Vector2(500f, 18f));
            _you = Ui.Label(rect, "Your Rank", string.Empty, 26, Ui.Weight.Black, UiStyle.Gold, TextAnchor.MiddleRight);
            Ui.TopLeft(_you.rectTransform, new Vector2(ContentWidth - 720f, 24f), new Vector2(700f, 36f));
        }

        private static void BuildHeads(RectTransform content)
        {
            RectTransform heads = Ui.Child(content, "Columns");
            Ui.TopLeft(heads, new Vector2(RowInset, HeadsTop), new Vector2(ContentWidth - 2f * RowInset, 28f));

            HeadAtLeft(heads, "#", "medal", 10f, 46f);
            HeadAtLeft(heads, "PLAYER", "person", 66f, 300f);
            foreach ((string caption, string icon, Color ink, float right, float width) in Columns)
            {
                RectTransform cell = Ui.Child(heads, caption);
                cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(1f, 0.5f);
                cell.anchoredPosition = new Vector2(-right, 0f);
                cell.sizeDelta = new Vector2(width, 28f);
                Image glyph = Ui.Icon(cell, "Icon", icon, ink);
                glyph.rectTransform.anchorMin = glyph.rectTransform.anchorMax = glyph.rectTransform.pivot = new Vector2(1f, 0.5f);
                glyph.rectTransform.anchoredPosition = Vector2.zero;
                glyph.rectTransform.sizeDelta = new Vector2(18f, 18f);
                Text head = Ui.Label(cell, "Caption", caption, 12, Ui.Weight.Bold, ink, TextAnchor.MiddleRight);
                Ui.Stretch(head.rectTransform, 0f, 24f, 0f, 0f);
            }

            Image rule = Ui.Fill(content, "Column Rule", new Color(1f, 1f, 1f, 0.16f));
            Ui.TopLeft(rule.rectTransform, new Vector2(RowInset, HeadsTop + 30f), new Vector2(ContentWidth - 2f * RowInset, 1f));
        }

        private static void HeadAtLeft(RectTransform heads, string caption, string icon, float x, float width)
        {
            RectTransform cell = Ui.Child(heads, caption);
            cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(0f, 0.5f);
            cell.anchoredPosition = new Vector2(x, 0f);
            cell.sizeDelta = new Vector2(width, 28f);
            Image glyph = Ui.Icon(cell, "Icon", icon, HudStyle.BoardMuted);
            glyph.rectTransform.anchorMin = glyph.rectTransform.anchorMax = glyph.rectTransform.pivot = new Vector2(0f, 0.5f);
            glyph.rectTransform.anchoredPosition = Vector2.zero;
            glyph.rectTransform.sizeDelta = new Vector2(18f, 18f);
            if (caption == "#") return;
            Text head = Ui.Label(cell, "Caption", caption, 12, Ui.Weight.Bold, HudStyle.BoardMuted);
            Ui.Stretch(head.rectTransform, 24f, 0f, 0f, 0f);
        }

        private static RankingRow BuildRow(RectTransform content, string name, float y)
        {
            var row = new RankingRow();
            row.Backing = Ui.Fill(content, name, Color.clear);
            row.Rect = row.Backing.rectTransform;
            Ui.TopLeft(row.Rect, new Vector2(RowInset, y), new Vector2(ContentWidth - 2f * RowInset, RowHeight));

            row.Accent = Ui.Fill(row.Rect, "Accent", HudStyle.Gold);
            Ui.TopLeft(row.Accent.rectTransform, Vector2.zero, new Vector2(3f, RowHeight));

            row.Medal = Ui.Icon(row.Rect, "Medal", "medal", Color.white);
            Ui.TopLeft(row.Medal.rectTransform, new Vector2(12f, 1f), new Vector2(21f, 21f));
            row.Rank = Ui.Label(row.Rect, "Rank", string.Empty, 14, Ui.Weight.Bold, HudStyle.BoardFaint, TextAnchor.MiddleCenter);
            Ui.TopLeft(row.Rank.rectTransform, new Vector2(4f, 0f), new Vector2(60f, RowHeight));
            row.Rank.horizontalOverflow = HorizontalWrapMode.Overflow;

            row.Name = Ui.Label(row.Rect, "Name", string.Empty, 15, Ui.Weight.Bold, HudStyle.Ink);
            Ui.TopLeft(row.Name.rectTransform, new Vector2(76f, 0f), new Vector2(520f, RowHeight));
            row.Name.horizontalOverflow = HorizontalWrapMode.Overflow;

            for (int c = 0; c < Columns.Length; c++)
            {
                var (caption, _, _, right, width) = Columns[c];
                Text cell = Ui.Label(row.Rect, caption, string.Empty, 15, Ui.Weight.Bold, HudStyle.Ink, TextAnchor.MiddleRight);
                cell.rectTransform.anchorMin = cell.rectTransform.anchorMax = cell.rectTransform.pivot = new Vector2(1f, 0.5f);
                cell.rectTransform.anchoredPosition = new Vector2(-right, 0f);
                cell.rectTransform.sizeDelta = new Vector2(width, RowHeight);
                row.Cells[c] = cell;
            }
            return row;
        }

        // ------------------------------------------------------------------ state

        private async System.Threading.Tasks.Task LoadAsync(bool force)
        {
            MasterSession? session = AchievementLedger.Session;
            if (_loading) return;
            if (session == null || !session.IsLoggedIn)
            {
                _error = string.Empty;
                Redraw();
                return;
            }
            if (!force && _board != null && _boardPlayer == session.PlayerId
                && Time.realtimeSinceStartup - _loadedAt < FreshSeconds)
                return;

            _loading = true;
            Redraw();
            int player = session.PlayerId;
            Leaderboard? board = await session.GetLeaderboardAsync();
            _loading = false;
            if (this == null) return;

            if (board != null)
            {
                _board = board;
                _boardPlayer = player;
                _loadedAt = Time.realtimeSinceStartup;
                _error = string.Empty;
            }
            else
            {
                _error = session.CareerError;
            }
            Redraw();
        }

        private int PageCount
        {
            get
            {
                int rows = _board?.Rows?.Length ?? 0;
                return Mathf.Clamp((Mathf.Min(rows, MaxRows) + RowsPerPage - 1) / RowsPerPage, 1, MaxRows / RowsPerPage);
            }
        }

        private void TurnPage(int by)
        {
            int page = Mathf.Clamp(_page + by, 0, PageCount - 1);
            if (page == _page) return;
            _page = page;
            Redraw();
        }

        private void Redraw()
        {
            if (_message == null) return;

            MasterSession? session = AchievementLedger.Session;
            bool signedIn = _forTool || (session != null && session.IsLoggedIn);
            int me = _forTool ? _toolPlayer : session != null ? session.PlayerId : 0;
            Leaderboard? board = signedIn && _board != null && _boardPlayer == me ? _board : null;
            LeaderboardRow[] rows = board?.Rows ?? System.Array.Empty<LeaderboardRow>();
            LeaderboardRow? you = board?.You;

            _page = Mathf.Clamp(_page, 0, PageCount - 1);
            int first = _page * RowsPerPage;
            bool youOnPage = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                int index = first + i;
                if (index < rows.Length && index < MaxRows)
                {
                    LeaderboardRow line = rows[index];
                    bool mine = line.PlayerId == me && me > 0;
                    youOnPage |= mine;
                    Bind(_rows[i], line, i, mine);
                }
                else
                {
                    Clear(_rows[i]);
                }
            }

            bool pin = you != null && !youOnPage;
            if (_pinned != null)
            {
                if (pin) Bind(_pinned, you!, 0, mine: true);
                else Clear(_pinned);
            }
            if (_pinnedRule != null) _pinnedRule.enabled = pin;

            if (_players != null)
                _players.text = board == null ? string.Empty
                    : board.Players.ToString("N0", CultureInfo.InvariantCulture) + " SOLDIERS WITH A CAREER";
            if (_you != null)
            {
                _you.text = you != null ? "#" + you.Rank.ToString("N0", CultureInfo.InvariantCulture)
                    : board != null ? "PLAY AN ONLINE MATCH TO BE RANKED" : string.Empty;
                _you.fontSize = you != null ? 30 : 16;
            }
            if (_youCaption != null) _youCaption.enabled = you != null;

            _message.text = !signedIn ? "SIGN IN TO MULTIPLAYER TO SEE THE GLOBAL RANKING"
                : _loading && board == null ? "LOADING THE RANKING…"
                : _error.Length > 0 && board == null ? _error.ToUpperInvariant()
                : board != null && rows.Length == 0 ? "NO ONE IS RANKED YET. PLAY AN ONLINE MATCH TO BE THE FIRST."
                : string.Empty;

            if (_pageText != null) _pageText.text = "PAGE " + (_page + 1).ToString(CultureInfo.InvariantCulture) + " / "
                                                     + PageCount.ToString(CultureInfo.InvariantCulture);
            if (_previous != null) _previous.interactable = _page > 0;
            if (_next != null) _next.interactable = _page < PageCount - 1;
            if (_refresh != null) _refresh.interactable = signedIn && !_loading;
        }

        private static void Clear(RankingRow row) => row.Rect.gameObject.SetActive(false);

        private static void Bind(RankingRow row, LeaderboardRow line, int stripe, bool mine)
        {
            row.Rect.gameObject.SetActive(true);
            row.Backing.color = mine ? HudStyle.LocalKillBacking
                : stripe % 2 == 0 ? new Color(1f, 1f, 1f, 0.035f) : new Color(1f, 1f, 1f, 0.075f);
            row.Accent.enabled = mine;

            bool medal = line.Rank >= 1 && line.Rank <= 3;
            row.Medal.enabled = medal;
            if (medal) row.Medal.color = HudStyle.MedalColour(line.Rank);
            row.Rank.text = medal ? string.Empty : line.Rank.ToString("N0", CultureInfo.InvariantCulture);
            row.Rank.color = mine ? HudStyle.GoldInk : HudStyle.BoardFaint;

            row.Name.text = string.IsNullOrEmpty(line.Name) ? "#" + line.PlayerId.ToString(CultureInfo.InvariantCulture) : line.Name;
            row.Name.color = mine ? HudStyle.GoldInk : medal ? HudStyle.MedalColour(line.Rank) : HudStyle.Ink;

            int kills = Clamp(line.Kills);
            int deaths = Clamp(line.Deaths);
            float ratio = deaths > 0 ? kills / (float)deaths : kills;
            Set(row.Cells[0], Number(line.Score), mine ? HudStyle.GoldInk : HudStyle.ScoreHeadInk);
            Set(row.Cells[8], Number(line.Kills), mine ? HudStyle.GoldInk : HudStyle.Ink);
            Set(row.Cells[7], Number(line.Deaths), HudStyle.BoardMuted);
            Set(row.Cells[6], ScoreboardWording.Ratio(kills, deaths), HudStyle.RatioInk(ratio, kills + deaths > 0));
            Set(row.Cells[5], Number(line.Headshots), line.Headshots > 0 ? HudStyle.HeadshotInk : HudStyle.BoardFaint);
            Set(row.Cells[4], Number(line.BestStreak), line.BestStreak >= 10 ? HudStyle.StreakInk : HudStyle.BoardMuted);
            Set(row.Cells[3], Number(line.Wins), HudStyle.Ink);
            Set(row.Cells[2], Number(line.Matches), HudStyle.BoardMuted);
            float winRate = line.Matches > 0 ? line.Wins / (float)line.Matches : 0f;
            Set(row.Cells[1], line.Matches > 0 ? Mathf.RoundToInt(winRate * 100f).ToString(CultureInfo.InvariantCulture) + "%" : "—",
                line.Matches == 0 ? HudStyle.BoardFaint : winRate >= 0.5f ? HudStyle.AliveInk : HudStyle.BoardMuted);
        }

        private static void Set(Text cell, string text, Color ink)
        {
            cell.text = text;
            cell.color = ink;
        }

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : value < 0 ? 0 : (int)value;
    }
}
