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
    /// <b>Rows fit the column.</b> A player's row is a third taller than a bot's, and both shrink
    /// together until the side fits, up to a comfortable ceiling; there is deliberately no floor,
    /// which on the first full board pushed rows through the rules line. The two sides size from
    /// the larger count of each group, so the two BOTS headings and every row line up across.
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

        /// <summary>
        /// Lays the queued rows out. <paramref name="scored"/> says which actors just gained a
        /// kill; <paramref name="stagger"/> plays the rows in one after another, for an opening.
        /// <paramref name="sizingHumans"/> and <paramref name="sizingBots"/> are the larger side's
        /// counts, so both sides share one layout and read across as a table.
        /// </summary>
        public void End(System.Func<ushort, bool> scored, bool stagger, int sizingHumans, int sizingBots)
        {
            if (!_complete) return;

            EnsureRows(_pendingCount);

            int humans = PendingHumans;
            int bots = _pendingCount - humans;
            sizingHumans = Mathf.Max(sizingHumans, humans);
            sizingBots = Mathf.Max(sizingBots, bots);

            int sections = (sizingHumans > 0 ? 1 : 0) + (sizingBots > 0 ? 1 : 0);
            float usable = _rows.rect.height - sections * (SectionHeight + SectionGap);
            float units = sizingHumans * HumanRowScale + sizingBots;
            float botPitch = units > 0f ? Mathf.Min(usable / units, MaxBotRowHeight + RowGap) : MaxBotRowHeight;
            float humanPitch = Mathf.Min(botPitch * HumanRowScale, MaxHumanRowHeight + RowGap);

            float botHeight = botPitch - (botPitch >= 20f ? RowGap : 1f);
            float humanHeight = humanPitch - (humanPitch >= 20f ? RowGap : 1f);
            int botFont = Mathf.Clamp(Mathf.RoundToInt(botHeight * 0.56f), 12, 17);
            int humanFont = Mathf.Clamp(Mathf.RoundToInt(humanHeight * 0.5f), 14, 20);

            float top = 0f;

            if (humans > 0)
                _humansSection.Show(HudSprites.Person(), "PLAYERS  ·  " + humans.ToString(CultureInfo.InvariantCulture), _teamColour);
            else
                _humansSection.Hide();

            if (sizingHumans > 0)
            {
                _humansSection.Place(top, SectionHeight);
                top -= SectionHeight + SectionGap;
            }

            float botsTop = top - sizingHumans * humanPitch;

            if (bots > 0)
            {
                _botsSection.Show(HudSprites.Bot(), "BOTS  ·  " + bots.ToString(CultureInfo.InvariantCulture), _teamColour);
                _botsSection.Place(botsTop, SectionHeight);
            }
            else
            {
                _botsSection.Hide();
            }

            float botRowsTop = botsTop - (sizingBots > 0 ? SectionHeight + SectionGap : 0f);

            for (int i = 0; i < _rowViews.Count; i++)
            {
                ScoreboardRowView view = _rowViews[i];

                if (i >= _pendingCount)
                {
                    if (view.gameObject.activeSelf) view.gameObject.SetActive(false);
                    continue;
                }

                bool appearing = stagger || !view.gameObject.activeSelf;
                if (!view.gameObject.activeSelf) view.gameObject.SetActive(true);

                ScoreboardRow row = _pending[i];
                bool human = !row.IsBot;
                int indexInGroup = human ? i : i - humans;

                view.Bind(in row, _teamColour, indexInGroup, scored(row.ActorId));

                if (human)
                    view.Place(top - indexInGroup * humanPitch, humanHeight, humanFont);
                else
                    view.Place(botRowsTop - indexInGroup * botPitch, botHeight, botFont);

                if (appearing) view.Appear(stagger ? i * StaggerSeconds : 0f);
            }

            _shownCount = _pendingCount;
            _empty.gameObject.SetActive(_pendingCount == 0);
        }

        /// <summary>Advances the rows' motion.</summary>
        public void Tick(float deltaSeconds)
        {
            if (!_complete) return;

            for (int i = 0; i < _shownCount && i < _rowViews.Count; i++)
                _rowViews[i].Tick(deltaSeconds);
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
