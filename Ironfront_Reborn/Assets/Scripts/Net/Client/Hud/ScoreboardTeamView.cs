using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One side's column on the Tab scoreboard: its band, flags, head count, totals and rows, the
    /// players first under their own heading and the bots after them under theirs. Feature 2,
    /// 2026-09-29; the groups are the owner's report of 2026-09-30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rows are cloned, not authored.</b> The builder authors one row template and this clones
    /// as many as a side needs on first use, so the prefab carries one row per side instead of
    /// thirty-two and a restyle is one edit.
    /// </para>
    /// <para>
    /// <b>Rows keep their size and the board pages.</b> A player's row is taller than a bot's and
    /// neither shrinks; what does not fit goes on the next page (<see cref="ScoreboardPaging"/>,
    /// owner's list of 2026-10-09, item 2). The two sides page from the larger count of each group,
    /// so the two BOTS headings and every row line up across.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ScoreboardTeamView : MonoBehaviour
    {
        public const float MaxBotRowHeight = 32f;
        public const float MaxHumanRowHeight = 42f;

        /// <summary>How much taller a player's row is than a bot's.</summary>
        private const float HumanRowScale = 1.3f;

        private const float SectionHeight = 24f;
        private const float SectionGap = 4f;
        private const float RowGap = 2f;
        private const float StaggerSeconds = 0.014f;

        [SerializeField] private Image _band;
        [SerializeField] private Text _teamName;
        [SerializeField] private Image _flagIcon;
        [SerializeField] private Text _flags;
        [SerializeField] private Text _perKill;
        [SerializeField] private Text _players;
        [SerializeField] private Text _totals;
        [SerializeField] private RectTransform _rows;
        [SerializeField] private Text _empty;
        [SerializeField] private ScoreboardRowView _rowTemplate;
        [SerializeField] private ScoreboardSectionView _humansSection;
        [SerializeField] private ScoreboardSectionView _botsSection;

        private readonly List<ScoreboardRowView> _rowViews = new List<ScoreboardRowView>();
        private bool[] _shown = new bool[0];
        private readonly ScoreboardRow[] _pending = new ScoreboardRow[MatchHud.ScoreboardRowsPerTeam];
        private int _pendingCount;
        private int _shownCount;
        private bool _complete;
        private Color _teamColour = Color.grey;

        private void Awake()
        {
            _complete = _band != null && _teamName != null && _flagIcon != null && _flags != null
                        && _perKill != null && _players != null && _totals != null && _rows != null
                        && _empty != null && _rowTemplate != null && _humansSection != null
                        && _botsSection != null && _humansSection.IsComplete && _botsSection.IsComplete;

            if (!_complete)
            {
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the scoreboard.", this);
                return;
            }

            _band.sprite = HudSprites.FadeRight();
            _flagIcon.sprite = HudSprites.Flag();
            _rowTemplate.gameObject.SetActive(false);
        }

        /// <summary>The side's colour, from the palette: the band, the flag, the headings and the rows.</summary>
        public void SetColour(Color team)
        {
            _teamColour = team;
            if (!_complete) return;

            _band.color = new Color(team.r, team.g, team.b, 0.9f);
            _flagIcon.color = HudStyle.TeamInk(team);
        }

        public void SetFlags(int flags)
        {
            if (!_complete) return;
            _flags.text = flags.ToString(CultureInfo.InvariantCulture);

            // What a kill is worth to this side right now, beside the flags that make it so: a score
            // that jumps by three reads as the rule, not as a wrong number.
            _perKill.text = Ironfront.Net.Replication.Client.ScoreboardWording.PerKillLine(flags);
        }

        /// <summary>Starts the column: name, head count and totals. Rows follow.</summary>
        public void Begin(string teamName, string players, string totals)
        {
            if (!_complete) return;

            _teamName.text = teamName;
            _players.text = players;
            _totals.text = totals;
            _pendingCount = 0;
        }

        /// <summary>Queues one row, in display order. Beyond the column's rows it is dropped.</summary>
        public void Add(in ScoreboardRow row)
        {
            if (!_complete || _pendingCount >= _pending.Length) return;
            _pending[_pendingCount++] = row;
        }

        /// <summary>Rows queued since <see cref="Begin"/>.</summary>
        public int PendingCount => _pendingCount;

        /// <summary>Players queued since <see cref="Begin"/>; the rest are bots.</summary>
        public int PendingHumans
        {
            get
            {
                int humans = 0;
                for (int i = 0; i < _pendingCount; i++) if (!_pending[i].IsBot) humans++;
                return humans;
            }
        }

        /// <summary>A player's row: the height a name reads at in a glance.</summary>
        public const float PlayerPitch = MaxHumanRowHeight + RowGap;

        /// <summary>A bot's row: quieter and a little smaller.</summary>
        public const float BotPitch = MaxBotRowHeight + RowGap;

        /// <summary>A group heading and the gap under it.</summary>
        public const float HeadingPitch = SectionHeight + SectionGap;

        /// <summary>
        /// Lays out page <paramref name="page"/> of the queued rows and answers how many pages there
        /// are (<see cref="ScoreboardPaging"/>). <paramref name="scored"/> says which actors just
        /// gained a kill; <paramref name="stagger"/> plays the rows in one after another, for an
        /// opening. <paramref name="sizingHumans"/> and <paramref name="sizingBots"/> are the larger
        /// side's counts, so both sides page alike and read across as one table.
        /// </summary>
        /// <remarks>
        /// Rows keep a fixed, readable height and the board pages, where it used to shrink every row
        /// until a full side fitted: with 100 bots and a dozen players that was type no one could read
        /// (owner's list of 2026-10-09, item 2).
        /// </remarks>
        public int End(System.Func<ushort, bool> scored, bool stagger, int sizingHumans, int sizingBots, int page)
        {
            if (!_complete) return 1;

            EnsureRows(_pendingCount);

            int humans = PendingHumans;
            int bots = _pendingCount - humans;
            sizingHumans = Mathf.Max(sizingHumans, humans);
            sizingBots = Mathf.Max(sizingBots, bots);

            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(
                sizingHumans, sizingBots, _rows.rect.height, HeadingPitch, PlayerPitch, BotPitch);
            page = Mathf.Clamp(page, 0, pages.Count - 1);

            float humanHeight = PlayerPitch - RowGap;
            float botHeight = BotPitch - RowGap;
            int humanFont = Mathf.Clamp(Mathf.RoundToInt(humanHeight * 0.48f), 14, 20);
            int botFont = Mathf.Clamp(Mathf.RoundToInt(botHeight * 0.52f), 12, 17);

            bool humansHeading = false;
            bool botsHeading = false;
            if (_shown.Length < _rowViews.Count) _shown = new bool[_rowViews.Count];
            bool[] shown = _shown;
            System.Array.Clear(shown, 0, shown.Length);

            foreach (ScoreboardPaging.Slot slot in pages[page])
            {
                switch (slot.Kind)
                {
                    case ScoreboardPaging.SlotKind.PlayersHeading:
                        if (humans == 0) break;
                        _humansSection.Show(HudSprites.Person(), "PLAYERS  ·  " + humans.ToString(CultureInfo.InvariantCulture), _teamColour);
                        _humansSection.Place(-slot.Top, SectionHeight);
                        humansHeading = true;
                        break;
                    case ScoreboardPaging.SlotKind.BotsHeading:
                        if (bots == 0) break;
                        _botsSection.Show(HudSprites.Bot(), "BOTS  ·  " + bots.ToString(CultureInfo.InvariantCulture), _teamColour);
                        _botsSection.Place(-slot.Top, SectionHeight);
                        botsHeading = true;
                        break;
                    case ScoreboardPaging.SlotKind.Player:
                        if (slot.Index < humans) PlaceRow(slot.Index, slot.Index, -slot.Top, humanHeight, humanFont);
                        break;
                    case ScoreboardPaging.SlotKind.Bot:
                        if (slot.Index < bots) PlaceRow(humans + slot.Index, slot.Index, -slot.Top, botHeight, botFont);
                        break;
                }
            }

            if (!humansHeading) _humansSection.Hide();
            if (!botsHeading) _botsSection.Hide();
            for (int i = 0; i < _rowViews.Count; i++)
                if (!shown[i] && _rowViews[i].gameObject.activeSelf) _rowViews[i].gameObject.SetActive(false);

            _shownCount = _rowViews.Count;
            _empty.gameObject.SetActive(_pendingCount == 0);
            return pages.Count;

            void PlaceRow(int pendingIndex, int indexInGroup, float top, float height, int font)
            {
                ScoreboardRowView view = _rowViews[pendingIndex];
                bool appearing = stagger || !view.gameObject.activeSelf;
                if (!view.gameObject.activeSelf) view.gameObject.SetActive(true);

                ScoreboardRow row = _pending[pendingIndex];
                view.Bind(in row, _teamColour, indexInGroup, scored(row.ActorId));
                view.Place(top, height, font);
                if (appearing) view.Appear(stagger ? indexInGroup * StaggerSeconds : 0f);
                shown[pendingIndex] = true;
            }
        }

        /// <summary>Which page holds this player's own row, or -1 when it is not on this side.</summary>
        public int PageOfLocal(int sizingHumans, int sizingBots)
        {
            if (!_complete) return -1;
            int humans = PendingHumans;
            for (int i = 0; i < _pendingCount; i++)
            {
                if (!_pending[i].IsLocal) continue;
                List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(
                    Mathf.Max(sizingHumans, humans), Mathf.Max(sizingBots, _pendingCount - humans),
                    _rows.rect.height, HeadingPitch, PlayerPitch, BotPitch);
                return _pending[i].IsBot
                    ? ScoreboardPaging.PageOf(pages, ScoreboardPaging.SlotKind.Bot, i - humans)
                    : ScoreboardPaging.PageOf(pages, ScoreboardPaging.SlotKind.Player, i);
            }
            return -1;
        }

        /// <summary>Advances the rows' motion.</summary>
        public void Tick(float deltaSeconds)
        {
            if (!_complete) return;

            for (int i = 0; i < _shownCount && i < _rowViews.Count; i++)
                if (_rowViews[i].gameObject.activeSelf) _rowViews[i].Tick(deltaSeconds);
        }

        /// <summary>Clones rows from the template until there are <paramref name="needed"/>.</summary>
        private void EnsureRows(int needed)
        {
            while (_rowViews.Count < needed)
            {
                ScoreboardRowView clone = Instantiate(_rowTemplate, _rows);
                clone.name = "Row " + (_rowViews.Count + 1);

                // Explicitly rather than by activation: a clone of an inactive template is
                // inactive, and edit mode runs no Awake at all. A half-authored template has
                // already said so in its own log line.
                clone.Initialize();
                if (!clone.IsComplete)
                {
                    DestroyImmediate(clone.gameObject);
                    return;
                }

                _rowViews.Add(clone);
            }
        }
    }
}
