using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The Tab scoreboard, rebuilt: the match across the top, both sides below, the rules at the
    /// foot. Playtest 2026-09-28, feature 2 (owner's request, 2026-09-29).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the top says.</b> The map, the two scores large in their side's colour, a
    /// tug-of-war bar showing how far the leader has pushed toward the winning MARGIN (victory is
    /// a lead, not a total -- the bar is full when the round is won), the lead in words, and the
    /// clock. The two scores and the clock are the same latched <c>S_MATCH_STATE</c> the score
    /// bar reads, so the board and the HUD cannot disagree.
    /// </para>
    /// <para>
    /// <b>Motion.</b> The board fades and settles in, each column's rows play in one after
    /// another, a player who scores lights up, the leader's score breathes, and the clock pulses
    /// through the final minute. Unscaled time throughout: the board reports the match, it does
    /// not take part in it.
    /// </para>
    /// <para>
    /// <b>It draws; it decides nothing.</b> Every word arrives from the presenter already
    /// written (<c>ScoreboardWording</c>), and every colour of a side from <see cref="MatchHud"/>,
    /// which is where the palette is read.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ScoreboardView : MonoBehaviour
    {
        private const float OpenSeconds = 0.22f;
        private const float CloseSeconds = 0.14f;
        private const float OpenScale = 0.965f;
        private const float OpenDrop = 22f;

        /// <summary>Clear space kept between a shrunk board and the edges of the screen.</summary>
        public const float FitMargin = 16f;
        private const float LeadRate = 5f;
        private const float BreathHertz = 0.55f;

        [SerializeField] private CanvasGroup _group;
        [SerializeField] private Image _vignette;
        [SerializeField] private RectTransform _panel;

        [Header("Top")]
        [SerializeField] private Text _map;
        [SerializeField] private Text _summary;
        [SerializeField] private Text _team0Label;
        [SerializeField] private Text _team1Label;
        [SerializeField] private Text _score0;
        [SerializeField] private Text _score1;
        [SerializeField] private Outline _score0Glow;
        [SerializeField] private Outline _score1Glow;
        [SerializeField] private RectTransform _leadTrack;
        [SerializeField] private Image _leadFill0;
        [SerializeField] private Image _leadFill1;
        [SerializeField] private Text _leadLine;
        [SerializeField] private Text _clock;
        [SerializeField] private Text _phase;
        [SerializeField] private Image _divider;

        [Header("Sides")]
        [SerializeField] private ScoreboardTeamView _team0;
        [SerializeField] private ScoreboardTeamView _team1;

        [Header("Foot")]
        [SerializeField] private Text _rules;

        private bool _complete;
        private bool _visible;
        private float _open;
        private float _time;

        private float _lead;
        private float _leadShown;
        private int _leader = TeamId.None;
        private bool _clockUrgent;

        private Color _team0Colour = Color.grey;
        private Color _team1Colour = Color.grey;

        /// <summary>Kills each actor had when the board last drew, to light up whoever scored.</summary>
        private readonly int[] _lastKills = new int[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _lastSeen = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _nextKills = new int[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _nextSeen = new bool[ProtocolConstants.MAX_ACTORS];

        /// <summary>The first columns after opening play in; later ones only update.</summary>
        private bool _staggerNext;

        private void Awake()
        {
            _complete = _group != null && _vignette != null && _panel != null && _map != null
                        && _summary != null && _team0Label != null && _team1Label != null
                        && _score0 != null && _score1 != null && _score0Glow != null
                        && _score1Glow != null && _leadTrack != null && _leadFill0 != null
                        && _leadFill1 != null && _leadLine != null && _clock != null
                        && _phase != null && _divider != null && _team0 != null && _team1 != null
                        && _rules != null;

            if (!_complete)
            {
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the scoreboard.", this);
                return;
            }

            _vignette.sprite = HudSprites.Vignette();
            _divider.sprite = HudSprites.Rule();
        }

        /// <summary>The two sides' colours, from the palette. <see cref="MatchHud"/> reads it.</summary>
        public void SetPalette(Color team0, Color team1)
        {
            _team0Colour = team0;
            _team1Colour = team1;
            if (!_complete) return;

            _team0.SetColour(team0);
            _team1.SetColour(team1);
            _team0Label.color = HudStyle.TeamInk(team0);
            _team1Label.color = HudStyle.TeamInk(team1);
            _score0.color = HudStyle.TeamInk(team0);
            _score1.color = HudStyle.TeamInk(team1);
            _score0Glow.effectColor = new Color(team0.r, team0.g, team0.b, 0f);
            _score1Glow.effectColor = new Color(team1.r, team1.g, team1.b, 0f);
            _leadFill0.color = HudStyle.TeamInk(team0);
            _leadFill1.color = HudStyle.TeamInk(team1);
        }

        /// <summary>Opens or closes the board. The board fades either way.</summary>
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;

            if (visible)
            {
                gameObject.SetActive(true);
                _staggerNext = true;

                // Nobody lights up on the first drawing after opening: what changed while the
                // board was shut is not news the moment it opens.
                System.Array.Clear(_lastSeen, 0, _lastSeen.Length);
            }
        }

        /// <summary>Takes the board down at once, no fade. For a HUD being reset.</summary>
        public void HideImmediately()
        {
            _visible = false;
            _open = 0f;
            if (_group != null) _group.alpha = 0f;
            gameObject.SetActive(false);
        }

        public void SetMatch(in ScoreboardMatch match)
        {
            if (!_complete) return;

            _map.text = match.MapName;
            _summary.text = match.Summary;
            _team0Label.text = ScoreboardWording.TeamName(TeamId.Team0);
            _team1Label.text = ScoreboardWording.TeamName(TeamId.Team1);
            _score0.text = match.Score0.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _score1.text = match.Score1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _leadLine.text = match.LeadLine;
            _clock.text = match.Clock;
            _phase.text = match.PhaseLabel;
            _rules.text = match.Rules;

            _lead = match.Lead;
            _leader = match.WinningTeam != TeamId.None ? match.WinningTeam
                : match.Score0 > match.Score1 ? TeamId.Team0
                : match.Score1 > match.Score0 ? TeamId.Team1
                : TeamId.None;

            _clockUrgent = match.ClockUrgent;
            if (!_clockUrgent) _clock.color = HudStyle.Ink;

            _team0.SetFlags(match.Flags0);
            _team1.SetFlags(match.Flags1);
        }

        public void BeginColumn(int team, string teamName, string players, string totals)
        {
            if (!_complete) return;

            ScoreboardTeamView column = Column(team);
            if (column != null) column.Begin(teamName, players, totals);
        }

        public void AddRow(int team, in ScoreboardRow row)
        {
            if (!_complete) return;

            ScoreboardTeamView column = Column(team);
            if (column != null) column.Add(in row);

            if (row.ActorId < _nextKills.Length)
            {
                _nextKills[row.ActorId] = row.Kills;
                _nextSeen[row.ActorId] = true;
            }
        }

        /// <summary>Lays both columns out, lighting up whoever scored since the last drawing.</summary>
        public void End()
        {
            if (!_complete) return;

            bool stagger = _staggerNext;
            _staggerNext = false;

            int sizingRows = Mathf.Max(_team0.PendingCount, _team1.PendingCount);
            _team0.End(Scored, stagger, sizingRows);
            _team1.End(Scored, stagger, sizingRows);

            System.Array.Copy(_nextKills, _lastKills, _lastKills.Length);
            System.Array.Copy(_nextSeen, _lastSeen, _lastSeen.Length);
            System.Array.Clear(_nextSeen, 0, _nextSeen.Length);
        }

        private bool Scored(ushort actorId)
            => actorId < _lastSeen.Length && _lastSeen[actorId] && _nextKills[actorId] > _lastKills[actorId];

        private ScoreboardTeamView Column(int team)
            => team == TeamId.Team0 ? _team0 : team == TeamId.Team1 ? _team1 : null;

        private void Update() => Tick(Time.unscaledDeltaTime);

        /// <summary>
        /// Advances every motion on the board by <paramref name="delta"/> seconds. Driven by
        /// <c>Update</c>; public so an edit-mode capture can settle the board without play mode.
        /// </summary>
        public void Tick(float delta)
        {
            if (!_complete) return;

            _time += delta;

            _open = _visible
                ? Mathf.Min(1f, _open + delta / OpenSeconds)
                : Mathf.Max(0f, _open - delta / CloseSeconds);

            if (!_visible && _open <= 0f)
            {
                HideImmediately();
                return;
            }

            float eased = HudStyle.EaseOut(_open);
            _group.alpha = eased;
            float fit = FitScale(((RectTransform)_panel.parent).rect.size, _panel.rect.size, FitMargin);
            _panel.localScale = Vector3.one * (Mathf.Lerp(OpenScale, 1f, eased) * fit);
            _panel.anchoredPosition = new Vector2(0f, (1f - eased) * -OpenDrop);

            TickLead(delta);
            TickBreath();

            _team0.Tick(delta);
            _team1.Tick(delta);
        }

        /// <summary>
        /// How far a <paramref name="board"/>-sized panel must shrink to sit inside
        /// <paramref name="area"/> with <paramref name="margin"/> to spare: 1 on a 16:9 or 16:10
        /// screen, where the board was drawn to fit, and less in a narrow window or on 4:3, where
        /// the full-width board ran off both edges (release test 2026-09-29). Never more than 1:
        /// the board does not grow past the size its type was set at.
        /// </summary>
        public static float FitScale(Vector2 area, Vector2 board, float margin)
        {
            if (board.x <= 0f || board.y <= 0f) return 1f;

            float wide = (area.x - 2f * margin) / board.x;
            float tall = (area.y - 2f * margin) / board.y;
            return Mathf.Clamp(Mathf.Min(wide, tall), 0.1f, 1f);
        }

        /// <summary>The tug-of-war bar closes on the lead rather than jumping to it.</summary>
        private void TickLead(float delta)
        {
            _leadShown = Mathf.Lerp(_leadShown, _lead, 1f - Mathf.Exp(-LeadRate * delta));

            float half = _leadTrack.rect.width * 0.5f;
            float height = _leadTrack.rect.height;

            ((RectTransform)_leadFill0.transform).sizeDelta = new Vector2(Mathf.Max(0f, -_leadShown) * half, height);
            ((RectTransform)_leadFill1.transform).sizeDelta = new Vector2(Mathf.Max(0f, _leadShown) * half, height);
        }

        /// <summary>The leader's score glows and fades; in the final minute the clock pulses.</summary>
        private void TickBreath()
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(_time * Mathf.PI * 2f * BreathHertz);

            SetGlow(_score0Glow, _team0Colour, _leader == TeamId.Team0 ? 0.16f + 0.26f * wave : 0f);
            SetGlow(_score1Glow, _team1Colour, _leader == TeamId.Team1 ? 0.16f + 0.26f * wave : 0f);

            if (_clockUrgent) _clock.color = Color.Lerp(HudStyle.Ink, HudStyle.Blood, wave);
        }

        private static void SetGlow(Outline glow, Color team, float alpha)
        {
            glow.enabled = alpha > 0f;
            if (alpha > 0f) glow.effectColor = new Color(team.r, team.g, team.b, alpha);
        }
    }
}
