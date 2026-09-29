using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One side's column on the Tab scoreboard: its band, flags, head count, totals and rows.
    /// Feature 2, 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rows are cloned, not authored.</b> The builder authors one row template and this clones
    /// <see cref="MatchHud.ScoreboardRowsPerTeam"/> of it on first use, so the prefab carries one
    /// row per side instead of thirty-two and a restyle is one edit.
    /// </para>
    /// <para>
    /// <b>Rows fit the column.</b> A usual side is sixteen bots and a few players, and a full one
    /// is <see cref="MatchHud.ScoreboardRowsPerTeam"/>; rows are as tall as the column allows, up
    /// to <see cref="MaxRowHeight"/>, and the text follows the row. There is deliberately no
    /// floor: the first capture of a full board had one, and it pushed rows 31 and 32 through the
    /// rules line -- the same fault the old board had, one clamp further down.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ScoreboardTeamView : MonoBehaviour
    {
        public const float MaxRowHeight = 40f;

        /// <summary>A short column still lays out as if it held this many, so a row is not huge.</summary>
        private const int MinRowsForSizing = 16;

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

        private readonly List<ScoreboardRowView> _rowViews = new List<ScoreboardRowView>();
        private readonly ScoreboardRow[] _pending = new ScoreboardRow[MatchHud.ScoreboardRowsPerTeam];
        private int _pendingCount;
        private int _shownCount;
        private bool _complete;

        private void Awake()
        {
            _complete = _band != null && _teamName != null && _flagIcon != null && _flags != null
                        && _perKill != null && _players != null && _totals != null && _rows != null
                        && _empty != null && _rowTemplate != null;

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

        /// <summary>The side's colour, from the palette: the band and the flag.</summary>
        public void SetColour(Color team)
        {
            if (!_complete) return;

            _band.color = new Color(team.r, team.g, team.b, 0.9f);
            _flagIcon.color = HudStyle.TeamInk(team);
        }

        public void SetFlags(int flags)
        {
            if (!_complete) return;
            _flags.text = flags.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

        /// <summary>
        /// Lays the queued rows out. <paramref name="scored"/> says which actors just gained a
        /// kill; <paramref name="stagger"/> plays the rows in one after another, for an opening.
        /// <paramref name="sizingRows"/> is the longer column's count, so both sides share one
        /// row height and read across as a table.
        /// </summary>
        public void End(System.Func<ushort, bool> scored, bool stagger, int sizingRows)
        {
            if (!_complete) return;

            EnsureRows(_pendingCount);

            float area = _rows.rect.height;
            sizingRows = Mathf.Max(Mathf.Max(sizingRows, _pendingCount), MinRowsForSizing);
            float pitch = Mathf.Min(area / sizingRows, MaxRowHeight + RowGap);
            float height = pitch - (pitch >= 20f ? RowGap : 1f);
            int fontSize = Mathf.Clamp(Mathf.RoundToInt(height * 0.62f), 14, 24);

            // The top of a column earns the star only by having scored: 0 kills leads nobody.
            bool starred = _pendingCount > 0 && _pending[0].Kills > 0;

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
                view.Bind(in row, i + 1, starred && i == 0, scored(row.ActorId));
                view.Place(-i * pitch, height, fontSize);

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
