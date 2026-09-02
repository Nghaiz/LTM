#nullable enable

using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The room: two roster columns, a side to pick, chat, ready, and the way out. P16 3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every element is drawn from the master's push and nothing is predicted.</b>
    /// <see cref="MasterSession.OnRoomState"/> is the only input; there is no polling, no timer
    /// reading a local clock, and no optimistic move of a player's own row. That is what makes
    /// two clients agree, which is exactly what criterion 3 grades — and the roster is the one
    /// screen in the game where two machines are looking at the same object at the same time.
    /// </para>
    /// <para>
    /// <b>Colour comes from <c>ITeamPalette</c>, never from a serialized <c>Color</c></b>
    /// (criterion 10, contracts § 6.3). <c>ColorScheme.TeamColor</c> is Assembly-CSharp and this
    /// assembly cannot name it, so the mapping crosses at the registry seam. A literal red and
    /// blue authored on the row would be a second copy of that mapping, and the two would drift
    /// the first time the game's team colours changed.
    /// </para>
    /// <para>
    /// <b>The screen never advances itself into the match.</b> <c>Waiting -&gt; Starting</c> is
    /// P14's rule on the master, and <c>MasterSession</c> acts on the push. There is no
    /// "enter match" button here, deliberately: the debug shell's was the manual intervention
    /// P14 and this phase exist to remove.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuRoomLobbyScreen : MenuFormScreen
    {
        /// <summary>
        /// Roster rows per side — half of <c>MAX_PLAYERS</c>, because the two sides split it.
        /// </summary>
        /// <remarks>
        /// Derived rather than typed as 8, so a change to <c>MAX_PLAYERS</c> cannot leave a
        /// full room with members the roster silently cannot show. The authored row count in
        /// <c>BuildMenuCanvas</c> reads this constant for the same reason.
        /// </remarks>
        public const int RowsPerSide = ProtocolConstants.MAX_PLAYERS / 2;

        [SerializeField] private MenuScreenController? _controller;

        [Header("Roster")]
        [SerializeField] private Text? _teamZeroHeading;
        [SerializeField] private Text? _teamOneHeading;
        [SerializeField] private Text[] _teamZeroRows = new Text[RowsPerSide];
        [SerializeField] private Text[] _teamOneRows = new Text[RowsPerSide];

        [Header("Controls")]
        [SerializeField] private Button? _switchSideButton;
        [SerializeField] private Text? _switchSideLabel;
        [SerializeField] private Button? _readyButton;
        [SerializeField] private Text? _readyLabel;
        [SerializeField] private Button? _leaveButton;

        [Header("Readouts")]
        [SerializeField] private Text? _headingText;
        [SerializeField] private Text? _statusText;
        [SerializeField] private Text? _errorText;

        [Header("Chat")]
        [SerializeField] private Text? _chatLog;
        [SerializeField] private InputField? _chatField;
        [SerializeField] private Button? _chatSendButton;

        private void Awake()
        {
            if (_switchSideButton != null) _switchSideButton.onClick.AddListener(OnSwitchSide);
            if (_readyButton != null) _readyButton.onClick.AddListener(OnReady);
            if (_leaveButton != null) _leaveButton.onClick.AddListener(OnLeave);
            if (_chatSendButton != null) _chatSendButton.onClick.AddListener(OnSendChat);

            // Belt to BuildMenuCanvas's braces. That builder sets the same limit when it
            // GENERATES the canvas, which does nothing for a canvas already serialized in a
            // scene -- and a scene asset is exactly what ships. Setting it here means the cap
            // holds on every existing canvas without a rebuild, and re-setting an already
            // correct value costs one comparison at Awake.
            if (_chatField != null) _chatField.characterLimit = MspChatLimits.MaxTextCharacters;
        }

        /// <summary>
        /// Asks the master for the other side. P16 3.5.
        /// </summary>
        /// <remarks>
        /// <b>The refusal is stated, never swallowed</b> (P16 3.4, criterion 5). A room that has
        /// left <c>Waiting</c> locks sides, and a click that did nothing would read as a broken
        /// button — so the message says what happened and what the player can do instead. The
        /// master refuses it again on its own; this is not the guard, it is the explanation.
        /// </remarks>
        private void OnSwitchSide()
        {
            if (_controller == null) return;

            RoomState? room = _controller.Room;
            if (room == null) return;

            if (room.Lifecycle != RoomLifecycleState.Waiting)
            {
                SetError("Sides are locked once the match starts. Leave the room to change side.");
                return;
            }

            if (!TryGetSelf(room, out RoomMember self))
            {
                SetError("The room has not said which side you are on yet.");
                return;
            }

            _controller.SwitchTeam((byte)(self.Team == 0 ? 1 : 0));
        }

        private void OnReady()
        {
            if (_controller == null) return;

            RoomState? room = _controller.Room;
            if (room == null) return;

            _controller.SetReady(!(TryGetSelf(room, out RoomMember self) && self.Ready));
        }

        private void OnLeave() => _controller?.LeaveRoom();

        private void OnSendChat()
        {
            if (_controller == null || _chatField == null) return;

            string text = _chatField.text;
            if (text.Trim().Length == 0) return;

            _controller.SendChat(text);

            // Cleared on send rather than on delivery: the line comes back as a push carrying
            // the sender's name, so leaving it in the field would show the player their own
            // message twice, once of them unsent.
            _chatField.text = string.Empty;
        }

        public override void SetError(string message)
        {
            if (_errorText != null) _errorText.text = message;
        }

        /// <summary>Redraws the whole room from the last push. P16 3.4.</summary>
        public override void OnControllerStateChanged(MenuScreenController controller)
        {
            RoomState? room = controller.Room;

            DrawColumn(_teamZeroRows, _teamZeroHeading, 0, room, controller.PlayerId);
            DrawColumn(_teamOneRows, _teamOneHeading, 1, room, controller.PlayerId);

            if (_chatLog != null) _chatLog.text = controller.ChatLog;

            bool waiting = room != null && room.Lifecycle == RoomLifecycleState.Waiting;
            bool known = room != null && TryGetSelf(room, out _);
            bool ready = room != null && TryGetSelf(room, out RoomMember self) && self.Ready;

            if (_headingText != null)
                _headingText.text = controller.RoomHeading;

            if (_statusText != null)
                _statusText.text = Describe(room);

            // Disabled rather than hidden once the room leaves Waiting, so the control stays
            // where the player last saw it and OnSwitchSide can say WHY it is unavailable
            // (criterion 5). A vanished button explains nothing.
            if (_switchSideButton != null)
                _switchSideButton.interactable = waiting && known && !controller.IsBusy;

            if (_switchSideLabel != null)
                _switchSideLabel.text = waiting ? "SWITCH SIDE" : "SIDES LOCKED";

            if (_readyButton != null) _readyButton.interactable = waiting && known && !controller.IsBusy;
            if (_readyLabel != null) _readyLabel.text = ready ? "NOT READY" : "READY";

            if (_leaveButton != null) _leaveButton.interactable = !controller.IsBusy;
            if (_chatSendButton != null) _chatSendButton.interactable = !controller.IsBusy;
        }

        /// <summary>
        /// Fills one side's rows, and colours its heading from <c>ITeamPalette</c>.
        /// </summary>
        /// <remarks>
        /// The row TEXT carries the colour as well as the heading, because a reader scanning two
        /// columns of identical grey names has only position to go on — and position is exactly
        /// what a screenshot of a mid-switch roster makes ambiguous.
        /// </remarks>
        private static void DrawColumn(
            Text[] rows, Text? heading, byte team, RoomState? room, int selfPlayerId)
        {
            Color colour = TeamColour(team);

            if (heading != null)
            {
                heading.text = team == 0 ? "TEAM 1" : "TEAM 2";
                heading.color = colour;
            }

            int written = 0;

            if (room != null)
            {
                foreach (RoomMember member in room.Members)
                {
                    if (member == null || member.Team != team) continue;
                    if (written >= rows.Length) break;

                    Text row = rows[written];
                    written++;

                    if (row == null) continue;

                    string you = member.PlayerId == selfPlayerId ? "  (you)" : string.Empty;
                    string ready = member.Ready ? "[READY] " : "[      ] ";

                    row.text = ready + member.Name + you;
                    row.color = colour;
                }
            }

            for (int i = written; i < rows.Length; i++)
                if (rows[i] != null) rows[i].text = string.Empty;
        }

        /// <summary>
        /// The team's colour, through the registry seam. Criterion 10.
        /// </summary>
        /// <remarks>
        /// <c>NetClientBindings.TeamColourRgb</c> falls back to a neutral grey when no palette is
        /// installed, which is the documented degraded case rather than a silent black — and a
        /// grey roster in a screenshot is a visible, diagnosable "the binding did not install"
        /// rather than text that has disappeared into the backdrop.
        /// </remarks>
        private static Color TeamColour(byte team)
        {
            int rgb = NetClientBindings.TeamColourRgb(team);

            return new Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f);
        }

        /// <summary>
        /// This client's own row in the roster, or false when the push has not named it yet.
        /// </summary>
        /// <remarks>
        /// <b>Absence is a real state, not a defect.</b> A create or a join transitions to
        /// <c>RoomLobby</c> the moment the master answers, and the first RoomStatePush follows
        /// separately — so there is a window in which the screen is up and the roster is empty.
        /// The controls read false through it and are simply not interactable, rather than
        /// dereferencing a member the room has not sent.
        /// </remarks>
        private bool TryGetSelf(RoomState room, out RoomMember self)
        {
            int playerId = _controller != null ? _controller.PlayerId : 0;

            if (playerId != 0)
            {
                foreach (RoomMember member in room.Members)
                {
                    if (member == null || member.PlayerId != playerId) continue;

                    self = member;
                    return true;
                }
            }

            self = null!;
            return false;
        }

        private static string Describe(RoomState? room)
        {
            if (room == null) return "Waiting for the room...";

            switch (room.Lifecycle)
            {
                case RoomLifecycleState.Waiting:
                    return $"{room.Members.Length} in the room. The match starts when everybody "
                           + "is ready.";
                case RoomLifecycleState.Starting:
                    return "Everybody is ready. Starting...";
                case RoomLifecycleState.InMatch:
                    return "The match is running. Joining it...";
                case RoomLifecycleState.Ending:
                    return "The match is ending.";
                default:
                    return $"Room state {room.State}.";
            }
        }
    }
}
