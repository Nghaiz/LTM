using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The in-match readout, on <c>Ingame UI Container.prefab</c>: the side you are on, the
    /// killfeed, and the deploy screen. The one implementation of <see cref="IMatchHud"/>. P17.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One component for three elements, deliberately.</b> They share a Canvas, a lifetime
    /// and a single registration into <see cref="NetClientBindings.MatchHud"/>; splitting them
    /// would mean three registrations, three slots and three ways for a build to be half-wired.
    /// The authoring gate still grades every field separately (P17 3.4), so "a detector per
    /// element" is a property of the checks rather than of the component count.
    /// </para>
    /// <para>
    /// <b>It renders and does not decide.</b> Every value below arrives through
    /// <see cref="IMatchHud"/> already resolved — the team from the snapshot, the names from
    /// <c>PlayerNameTable</c>, the clock from <c>ClientCombatState</c>. Nothing here reads the
    /// wire, and the deploy screen's visibility is the caller's alive signal rather than this
    /// object's own idea of whether the player is dead. That is what makes criterion 5 — a
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

        [Header("Which side you are on (3.1)")]
        [Tooltip("Names the local team, blank until the first snapshot answers.")]
        [SerializeField] private Text _teamReadoutText;

        [Header("Killfeed (3.3)")]
        [Tooltip("Newest first. One Text per line; the builder authors KillfeedRows of them.")]
        [SerializeField] private Text[] _killfeedRows = new Text[KillfeedRows];

        [Header("Deploy screen (3.2)")]
        [Tooltip("The whole death overlay. Activated and deactivated, never merely faded.")]
        [SerializeField] private GameObject _deployRoot;

        [Tooltip("Names who killed you, coloured by their side.")]
        [SerializeField] private Text _deployKillerText;

        [Tooltip("The respawn countdown, and what to do when it reaches zero.")]
        [SerializeField] private Text _deployTimerText;

        [Tooltip("Sends the same empty C_SPAWN_REQUEST the respawn key sends.")]
        [SerializeField] private Button _deployButton;

        /// <summary>Set by the Deploy control, cleared by the read. See the seam's remark.</summary>
        private bool _deployPressed;

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
            if (!NetClientPresenterGuard.IsPresentable)
            {
                // Offline. Put the overlay away before disabling, or the authored state of the
                // prefab is whatever the last Editor session left — and a deploy panel visible
                // over the bot match is the X-48 failure one screen over.
                if (_deployRoot != null) _deployRoot.SetActive(false);
                if (_teamReadoutText != null) _teamReadoutText.text = string.Empty;
                ClearKillfeed();

                enabled = false;
                return;
            }

            if (_deployRoot != null) _deployRoot.SetActive(false);
            if (_teamReadoutText != null) _teamReadoutText.text = string.Empty;
            ClearKillfeed();
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

            for (int i = count; i < _killfeedRows.Length; i++)
                if (_killfeedRows[i] != null) _killfeedRows[i].text = string.Empty;
        }

        /// <inheritdoc/>
        public void SetKillfeedLine(
            int index, string killerName, int killerTeam, string victimName, int victimTeam,
            bool headshot)
        {
            if (_killfeedRows == null) return;
            if (index < 0 || index >= _killfeedRows.Length) return;

            Text row = _killfeedRows[index];
            if (row == null) return;

            // Rich text, because ONE Text per line is what an authored row is and the two names
            // are on different sides. The alternative -- three Texts per row, laid out by hand --
            // is nine more references for the gate to grade and a layout that breaks on a long
            // name. The tags are built from the palette, so there is still exactly one mapping.
            row.supportRichText = true;
            row.text = Coloured(killerName, killerTeam)
                       + (headshot ? " <b>▸</b> " : " → ")
                       + Coloured(victimName, victimTeam);
        }

        /// <inheritdoc/>
        public void ShowDeploy(string killerName, int killerTeam)
        {
            if (_deployRoot != null) _deployRoot.SetActive(true);

            TakeCursor();

            if (_deployKillerText != null)
            {
                _deployKillerText.text = string.IsNullOrEmpty(killerName)
                    ? string.Empty
                    : "Killed by " + killerName;
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
                ? "Ready. Deploy, or press Space."
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
        public bool ConsumeDeployPressed()
        {
            if (!_deployPressed) return false;

            _deployPressed = false;
            return true;
        }

        private void OnDeployClicked() => _deployPressed = true;

        private void ClearKillfeed()
        {
            if (_killfeedRows == null) return;

            for (int i = 0; i < _killfeedRows.Length; i++)
                if (_killfeedRows[i] != null) _killfeedRows[i].text = string.Empty;
        }

        /// <summary>
        /// The name a side goes by on screen.
        /// </summary>
        /// <remarks>
        /// The same vocabulary the room lobby uses -- team 0 is "TEAM 1" -- because a player who
        /// picked a side on that screen and then reads a different word for it in the match has
        /// been told about two things. P16 criterion 10 is graded on those exact strings.
        /// </remarks>
        private static string TeamLabel(int team) => team == TeamId.Team0 ? "TEAM 1" : "TEAM 2";

        /// <summary>
        /// <paramref name="text"/> wrapped in the colour <paramref name="team"/> is drawn in.
        /// </summary>
        private static string Coloured(string text, int team)
            => "<color=#" + ColourHex(team) + ">" + text + "</color>";

        private static string ColourHex(int team)
            => (NetClientBindings.TeamColourRgb(team) & 0xFFFFFF).ToString("X6");

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
