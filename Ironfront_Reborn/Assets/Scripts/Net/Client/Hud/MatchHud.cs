using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The in-match readout, on <c>Ingame UI Container.prefab</c>: the side you are on, the
    /// killfeed, the deploy screen, and the Tab scoreboard. The one implementation of
    /// <see cref="IMatchHud"/>. P17, extended by P18 3.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One component for four elements, deliberately.</b> They share a Canvas, a lifetime
    /// and a single registration into <see cref="NetClientBindings.MatchHud"/>; splitting them
    /// would mean four registrations, four slots and four ways for a build to be half-wired.
    /// The authoring gate still grades every field separately (P17 3.4, P18 3.4), so "a detector
    /// per element" is a property of the checks rather than of the component count.
    /// </para>
    /// <para>
    /// <b>It renders and does not decide.</b> Every value below arrives through
    /// <see cref="IMatchHud"/> already resolved — the local team from the snapshot, the names
    /// from <c>PlayerNameTable</c>, the scoreboard's rows and sides from
    /// <c>PlayerScoreTable</c>, the clock from <c>ClientCombatState</c>. Nothing here reads the
    /// wire; it does not even poll Tab, because the board's visibility is a level the caller
    /// pushes. The deploy screen's visibility is likewise the caller's alive signal rather than
    /// this object's own idea of whether the player is dead. That is what makes criterion 5 — a
    /// respawn the player did not request closes the screen — hold by construction.
    /// </para>
    /// <para>
    /// <b>Inert offline, and the guard is the presenters'.</b> <c>GameManager.StartGame</c>
    /// instantiates this prefab for the offline bot match too, where there is no snapshot, no
    /// killfeed and no networked death. <c>NetClientPresenterGuard.IsPresentable</c> is the same
    /// test every client presenter makes; failing it disables this component before
    /// <c>OnEnable</c> can register it, so the offline game reaches an unregistered slot rather
    /// than a live HUD nobody is driving.
    /// </para>
    /// <para>
    /// <b>Colour comes from <c>ITeamPalette</c>, never from a serialized <c>Color</c></b>
    /// (contracts § 6.3, the rule P16's roster is graded on). <c>ColorScheme.TeamColor</c> is
    /// <c>Assembly-CSharp</c> and this assembly cannot name it, so a red and blue authored here
    /// would be a second copy of a mapping the game already owns — and the copies drift the
    /// first time a side is re-themed, with nothing failing.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MatchHud : MonoBehaviour, IMatchHud
    {
        /// <summary>
        /// Killfeed rows authored on the prefab. Matches <c>KillfeedModel.DefaultCapacity</c>.
        /// </summary>
        /// <remarks>
        /// Read off the model's own constant rather than typed as 5, so raising the model's
        /// capacity cannot leave the newest kills with no row to render in — the same discipline
        /// <c>MenuRoomLobbyScreen.RowsPerSide</c> applies to <c>MAX_PLAYERS</c>. The builder
        /// authors this many rows by reading this constant.
        /// </remarks>
        public const int KillfeedRows = Ironfront.Net.Replication.Client.KillfeedModel.DefaultCapacity;

        /// <summary>
        /// Rows one side's column will render before it starts dropping them.
        /// </summary>
        /// <remarks>
        /// Half the actor id space, derived rather than typed as 32: the protocol's ceiling is
        /// what decides how many players a side can hold, and a hand-written number would go on
        /// looking right after <c>MAX_ACTORS</c> moved. A column asked for more than this renders
        /// the first <see cref="ScoreboardRowsPerTeam"/> and states the true roster size in its
        /// heading, so the truncation is visible rather than silent.
        /// </remarks>
        public const int ScoreboardRowsPerTeam = ProtocolConstants.MAX_ACTORS / 2;

        [Header("Which side you are on (3.1)")]
        [Tooltip("Names the local team, blank until the first snapshot answers.")]
        [SerializeField] private Text _teamReadoutText;

        [Header("Killfeed (3.3, rebuilt for feature 2)")]
        [Tooltip("The rows a kill is drawn in; the builder authors KillfeedRows of them. A row "
                 + "follows its kill as newer kills push it down, so these are not in screen order.")]
        [SerializeField] private KillfeedRowView[] _killfeedRows = new KillfeedRowView[KillfeedRows];

        [Header("Deploy screen (3.2)")]
        [Tooltip("The whole death overlay. Activated and deactivated, never merely faded.")]
        [SerializeField] private GameObject _deployRoot;

        [Tooltip("Names who killed you, coloured by their side.")]
        [SerializeField] private Text _deployKillerText;

        [Tooltip("The respawn countdown, and what to do when it reaches zero.")]
        [SerializeField] private Text _deployTimerText;

        [Tooltip("Sends the same empty C_SPAWN_REQUEST the respawn key sends.")]
        [SerializeField] private Button _deployButton;

        [Header("Scoreboard (P18 3.3, rebuilt for feature 2)")]
        [Tooltip("The Tab board: the match across the top, both sides below. Draws what it is "
                 + "handed; this component reads the palette and passes the colours on.")]
        [SerializeField] private ScoreboardView _scoreboard;

        /// <summary>Set by the Deploy control, cleared by the read. See the seam's remark.</summary>
        private bool _deployPressed;

        /// <summary>
        /// The killfeed push in progress: its lines, which have arrived, and how many are owed.
        /// </summary>
        /// <remarks>
        /// <b>Reconciled once the push is whole, never line by line.</b> The newest kill arrives
        /// first, and claiming it a row before the older lines have said which rows they keep
        /// would take a row from a kill that is still on screen. So lines are collected and
        /// matched to rows together; see <see cref="ReconcileKillfeed"/>.
        /// </remarks>
        private readonly KillfeedLine[] _pendingLines = new KillfeedLine[KillfeedRows];
        private readonly bool[] _pendingHas = new bool[KillfeedRows];
        private int _pendingCount = -1;
        private int _pendingReceived;

        /// <summary>
        /// Scratch for <see cref="ReconcileKillfeed"/>: which rows a push kept. Sized in
        /// <see cref="Awake"/> from the rows actually authored, which the gate holds at
        /// <see cref="KillfeedRows"/> but a stale prefab might not.
        /// </summary>
        private bool[] _rowKept = System.Array.Empty<bool>();

        /// <summary>Scratch for <see cref="ReconcileKillfeed"/>: the row each line is drawn in.</summary>
        private readonly int[] _lineRow = new int[KillfeedRows];

        /// <summary>Whether the board is up, so a repeated call costs nothing.</summary>
        private bool _scoreboardVisible;

        /// <summary>
        /// The last countdown rendered, so a per-frame tick writes a string only on a change.
        /// </summary>
        /// <remarks>
        /// <c>int.MinValue</c> rather than <c>-1</c>: the ready state renders at 0 and a fresh
        /// screen must write it, so the sentinel has to be a value the clock cannot produce.
        /// </remarks>
        private int _shownSeconds = int.MinValue;

        private bool _shownCanDeploy;

        /// <summary>The team last written to the readout, so an unchanged team costs nothing.</summary>
        private int _shownTeam = int.MinValue;

        /// <summary>
        /// The cursor state the deploy screen took, and whether it took one.
        /// </summary>
        /// <remarks>
        /// <b>A screen with a button needs a pointer, and this game locks one away.</b>
        /// <c>FpsActorController</c> plays with <c>CursorLockMode.Locked</c>, so a Deploy control
        /// on an overlay is unclickable unless something unlocks it — which is exactly what the
        /// offline path already does: <c>LoadoutUi.ShowCanvas</c> unlocks on open and
        /// <c>HideCanvas</c> re-locks on close. This is that pair, and it is SAVED and RESTORED
        /// rather than re-locked to a constant, for <c>NetClientLocalCombatDriver.RestoreInput</c>'s
        /// reason: give back only what you took. Re-locking unconditionally would slam the cursor
        /// away from an options or loadout screen that legitimately had it open underneath.
        /// </remarks>
        private CursorLockMode _cursorBeforeDeploy;
        private bool _cursorVisibleBeforeDeploy;
        private bool _cursorTaken;

        private void Awake()
        {
            _rowKept = new bool[_killfeedRows != null ? _killfeedRows.Length : 0];

            if (!NetClientPresenterGuard.IsPresentable)
            {
                // Offline. Put the overlay away before disabling, or the authored state of the
                // prefab is whatever the last Editor session left — and a deploy panel visible
                // over the bot match is the X-48 failure one screen over.
                if (_deployRoot != null) _deployRoot.SetActive(false);
                if (_scoreboard != null) _scoreboard.HideImmediately();
                if (_teamReadoutText != null) _teamReadoutText.text = string.Empty;
                ClearKillfeed();

                enabled = false;
                return;
            }

            if (_deployRoot != null) _deployRoot.SetActive(false);
            if (_scoreboard != null) _scoreboard.HideImmediately();
            if (_teamReadoutText != null) _teamReadoutText.text = string.Empty;
            ClearKillfeed();
        }

        private void Update()
        {
            // A push whose lines never all arrived still draws what did, rather than leaving the
            // feed frozen on the push before it. The presenter pushes every line synchronously,
            // so this is a guard for a caller that does not.
            if (_pendingCount > 0) ReconcileKillfeed();

            if (_killfeedRows == null) return;

            float delta = Time.unscaledDeltaTime;
            for (int i = 0; i < _killfeedRows.Length; i++)
                if (_killfeedRows[i] != null) _killfeedRows[i].Tick(delta);
        }

        private void OnEnable()
        {
            NetClientBindings.MatchHud = this;

            if (_deployButton != null) _deployButton.onClick.AddListener(OnDeployClicked);
        }

        private void OnDisable()
        {
            if (_deployButton != null) _deployButton.onClick.RemoveListener(OnDeployClicked);

            // Only if it is still ours. A scene change can instantiate the next HUD before this
            // one is torn down, and clearing unconditionally would unregister the live one.
            if (ReferenceEquals(NetClientBindings.MatchHud, this)) NetClientBindings.MatchHud = null;

            // A HUD torn down while the deploy screen is up would otherwise leave the cursor
            // unlocked for the rest of the session, with nothing left running to put it back.
            ReleaseCursor();

            // A board left up by a teardown mid-hold would be frozen on screen with nothing left
            // to lower it -- the killfeed's own reason for pushing a count of zero on disable.
            SetScoreboardVisible(false);

            _deployPressed = false;
        }

        /// <summary>Frees the pointer for the Deploy control, remembering what it replaced.</summary>
        private void TakeCursor()
        {
            // Guarded, because ShowDeploy is called again when a death names its killer late --
            // taking twice would record the UNLOCKED state as the one to restore.
            if (_cursorTaken) return;

            _cursorTaken = true;
            _cursorBeforeDeploy = Cursor.lockState;
            _cursorVisibleBeforeDeploy = Cursor.visible;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>Gives the pointer back, and only if this screen took it.</summary>
        private void ReleaseCursor()
        {
            if (!_cursorTaken) return;

            _cursorTaken = false;
            Cursor.lockState = _cursorBeforeDeploy;
            Cursor.visible = _cursorVisibleBeforeDeploy;
        }

        /// <inheritdoc/>
        public void SetLocalTeam(int team)
        {
            if (_teamReadoutText == null) return;
            if (team == _shownTeam) return;

            _shownTeam = team;

            if (team == TeamId.None)
            {
                // Blank, not "TEAM 1". Before the first snapshot there is no answer, and stating
                // one would be a fabricated zero -- ScoreUi's own rule for a human count that
                // has not arrived. This element exists to make a wrong team visible; it cannot,
                // if the unknown state is drawn as a side.
                _teamReadoutText.text = string.Empty;
                return;
            }

            _teamReadoutText.text = TeamLabel(team);
            _teamReadoutText.color = TeamColour(team);
        }

        /// <inheritdoc/>
        public void SetKillfeedLineCount(int count)
        {
            if (_killfeedRows == null) return;

            // More lines than rows cannot be drawn; the oldest (the highest indices) go unshown.
            _pendingCount = Mathf.Clamp(count, 0, _pendingLines.Length);
            _pendingReceived = 0;
            System.Array.Clear(_pendingHas, 0, _pendingHas.Length);

            // A count of zero is the whole push: the clear, and the common case.
            if (_pendingCount == 0) ReconcileKillfeed();
        }

        /// <inheritdoc/>
        public void SetKillfeedLine(int index, in KillfeedLine line)
        {
            if (_pendingCount < 0) return;
            if (index < 0 || index >= _pendingCount) return;

            if (!_pendingHas[index]) _pendingReceived++;
            _pendingLines[index] = line;
            _pendingHas[index] = true;

            if (_pendingReceived >= _pendingCount) ReconcileKillfeed();
        }

        /// <summary>
        /// Matches a finished push to rows: a kill still on screen keeps its row, a new kill takes
        /// a free one, and a row whose kill left the push fades out.
        /// </summary>
        /// <remarks>
        /// A new kill prefers a free row, then the faintest row already fading, then -- when the
        /// feed was full -- the row of the kill the model just pushed out, which is leaving anyway.
        /// That last case cuts the oldest line instead of fading it, which is what a full feed
        /// should do: the new kill is the news.
        /// </remarks>
        private void ReconcileKillfeed()
        {
            int count = _pendingCount;
            _pendingCount = -1;

            System.Array.Clear(_rowKept, 0, _rowKept.Length);

            for (int line = 0; line < count; line++)
            {
                _lineRow[line] = -1;
                if (!_pendingHas[line]) continue;

                long sequence = _pendingLines[line].Sequence;
                for (int row = 0; row < _killfeedRows.Length; row++)
                {
                    KillfeedRowView view = _killfeedRows[row];
                    if (_rowKept[row] || view == null || view.IsFree || view.Sequence != sequence) continue;

                    _rowKept[row] = true;
                    _lineRow[line] = row;
                    break;
                }
            }

            for (int line = 0; line < count; line++)
            {
                if (!_pendingHas[line] || _lineRow[line] >= 0) continue;

                int best = -1;
                for (int row = 0; row < _killfeedRows.Length; row++)
                {
                    if (_rowKept[row] || _killfeedRows[row] == null) continue;
                    if (best < 0 || Claimability(_killfeedRows[row]) < Claimability(_killfeedRows[best]))
                        best = row;
                }

                if (best < 0) continue;

                _rowKept[best] = true;
                _lineRow[line] = best;
            }

            for (int line = 0; line < count; line++)
            {
                if (_lineRow[line] < 0) continue;

                KillfeedLine drawn = _pendingLines[line];
                _killfeedRows[_lineRow[line]].Show(
                    in drawn, line, TextInk(drawn.KillerTeam), TextInk(drawn.VictimTeam));
            }

            for (int row = 0; row < _killfeedRows.Length; row++)
                if (!_rowKept[row] && _killfeedRows[row] != null) _killfeedRows[row].Leave();

            System.Array.Clear(_pendingHas, 0, _pendingHas.Length);
            _pendingReceived = 0;
        }

        /// <summary>Lower is better to reuse: free, then fading, then still showing a kill.</summary>
        private static float Claimability(KillfeedRowView row)
        {
            if (row.IsFree) return 0f;
            if (row.IsLeaving) return 1f + row.Opacity;
            return 3f;
        }

        /// <inheritdoc/>
        public void ShowDeploy(string caption, int killerTeam)
        {
            if (_deployRoot != null) _deployRoot.SetActive(true);

            TakeCursor();

            if (_deployKillerText != null)
            {
                _deployKillerText.text = caption ?? string.Empty;
                _deployKillerText.color = TeamColour(killerTeam);
            }

            // Forces the next tick to write, so a second death inside one screen's lifetime does
            // not inherit the previous countdown's last rendered value.
            _shownSeconds = int.MinValue;
        }

        /// <inheritdoc/>
        public void TickDeploy(float secondsUntilRespawn, bool canDeploy)
        {
            if (_deployButton != null) _deployButton.interactable = canDeploy;

            if (_deployTimerText == null) return;

            int seconds = secondsUntilRespawn > 0f ? Mathf.CeilToInt(secondsUntilRespawn) : 0;
            if (seconds == _shownSeconds && canDeploy == _shownCanDeploy) return;

            _shownSeconds = seconds;
            _shownCanDeploy = canDeploy;

            _deployTimerText.text = canDeploy
                ? "Ready. Deploy."
                : "Deploying in " + seconds + "s";
        }

        /// <inheritdoc/>
        public void HideDeploy()
        {
            if (_deployRoot != null) _deployRoot.SetActive(false);

            ReleaseCursor();

            // Dropped here rather than left for the next read: a press that arrived on the frame
            // a force-respawn landed would otherwise be spent on the NEXT death, respawning the
            // player from a screen they never saw.
            _deployPressed = false;
            _shownSeconds = int.MinValue;
        }

        /// <inheritdoc/>
        public void SetScoreboardVisible(bool visible)
        {
            if (visible == _scoreboardVisible) return;
            _scoreboardVisible = visible;

            if (_scoreboard == null) return;

            // Visible first: opening is what activates the board and runs its Awake, and the
            // palette is applied to parts that exist only after that.
            _scoreboard.SetVisible(visible);
            if (visible)
                _scoreboard.SetPalette(TeamColour(TeamId.Team0), TeamColour(TeamId.Team1));
        }

        /// <inheritdoc/>
        public void SetScoreboardMatch(in ScoreboardMatch match)
        {
            if (_scoreboard != null) _scoreboard.SetMatch(in match);
        }

        /// <inheritdoc/>
        public void BeginScoreboardColumn(
            int team, int playerCount, int humanCount, int totalKills, int totalDeaths)
        {
            if (_scoreboard == null) return;

            // The roster size and the column's own totals, on screen, because criterion 7 is the
            // arithmetic that reconciles this board with the team score above it. The count is
            // the TRUE one even when more rows follow than the column can draw.
            _scoreboard.BeginColumn(
                team,
                TeamLabel(team),
                Ironfront.Net.Replication.Client.ScoreboardWording.PlayersLine(playerCount, humanCount),
                Ironfront.Net.Replication.Client.ScoreboardWording.TotalsLine(totalKills, totalDeaths));
        }

        /// <inheritdoc/>
        public void AddScoreboardRow(int team, in ScoreboardRow row)
        {
            if (_scoreboard != null) _scoreboard.AddRow(team, in row);
        }

        /// <inheritdoc/>
        public void EndScoreboard()
        {
            if (_scoreboard != null) _scoreboard.End();
        }

        /// <inheritdoc/>
        public bool ConsumeDeployPressed()
        {
            if (!_deployPressed) return false;

            _deployPressed = false;
            return true;
        }

        private void OnDeployClicked() => _deployPressed = true;

        private void ClearKillfeed()
        {
            _pendingCount = -1;
            _pendingReceived = 0;
            System.Array.Clear(_pendingHas, 0, _pendingHas.Length);

            if (_killfeedRows == null) return;

            for (int i = 0; i < _killfeedRows.Length; i++)
                if (_killfeedRows[i] != null) _killfeedRows[i].Release();
        }

        /// <summary>
        /// The name a side goes by on screen.
        /// </summary>
        /// <remarks>
        /// The same vocabulary the room lobby uses -- team 0 is "TEAM 1" -- because a player who
        /// picked a side on that screen and then reads a different word for it in the match has
        /// been told about two things. P16 criterion 10 is graded on those exact strings.
        /// </remarks>
        private static string TeamLabel(int team)
            => Ironfront.Net.Replication.Client.ScoreboardWording.TeamName((byte)team);

        /// <summary>
        /// A side's colour for a name drawn on the killfeed's dark backing: the palette's, lifted
        /// a little toward white so a dark blue stays readable on near-black.
        /// </summary>
        /// <remarks>
        /// A transform of <see cref="TeamColour"/>, not a second mapping: re-theming a side still
        /// happens in one place.
        /// </remarks>
        private static Color TextInk(int team) => Color.Lerp(TeamColour(team), Color.white, 0.2f);

        /// <summary>
        /// The palette's answer for <paramref name="team"/>, as an engine colour.
        /// </summary>
        /// <remarks>
        /// The unpack is <c>MenuRoomLobbyScreen.TeamColour</c>'s, and it is repeated rather than
        /// shared because sharing it means a helper in <c>Net/Shared</c> that returns a
        /// <c>UnityEngine.Color</c> -- which is exactly the widening <see cref="ITeamPalette"/>
        /// refuses, for the alpha and colour-space reasons its own remark gives. Four lines of
        /// shifting is the cheaper of the two.
        /// </remarks>
        private static Color TeamColour(int team)
        {
            int rgb = NetClientBindings.TeamColourRgb(team);

            return new Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f);
        }
    }
}
