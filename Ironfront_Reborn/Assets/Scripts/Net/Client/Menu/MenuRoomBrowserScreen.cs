#nullable enable

using Ironfront.MasterClient;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The list of rooms a player can enter, and the way into one. P16 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the player could see before this screen: a count.</b> The debug shell drew
    /// <c>"#{id} {name}  {Players}/{MaxPlayers}"</c> and nothing else — no map, no lifecycle, no
    /// indication that a room wanted a password until the join came back
    /// <c>WrongRoomPassword</c>. Every field below was already on <c>RoomInfo</c> or already on
    /// the master; <c>isPrivate</c> is the one that had to be added to the wire, and it is a
    /// projection of a value <c>Room</c> has always held.
    /// </para>
    /// <para>
    /// <b>A fixed set of authored rows, not an instantiated prefab.</b> The whole Canvas is built
    /// by <c>BuildMenuCanvas</c> and graded by <c>MenuScreenWiringDetectors</c> over serialized
    /// references, and a row spawned at runtime is authored nowhere and gradeable by nothing. The
    /// cap is <see cref="Rows"/>; rooms past it are counted in
    /// <see cref="_overflowText"/> rather than silently dropped, because a browser that shows
    /// eight of twelve rooms and says so is honest and one that shows eight of twelve is a bug
    /// report.
    /// </para>
    /// <para>
    /// <b>The latency line says "master", and means it</b> (owner decision, 2026-09-02). See
    /// <see cref="MasterSession.MasterPingMs"/> for why a per-room game-server ping cannot exist
    /// before a server is allocated.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuRoomBrowserScreen : MenuFormScreen
    {
        /// <summary>
        /// How many rooms fit on screen at once.
        /// </summary>
        /// <remarks>
        /// Eight rather than <c>MAX_PLAYERS</c> or some other borrowed constant: it is a layout
        /// fact about this panel, and tying it to an unrelated protocol number would make a
        /// change to that number silently reflow the menu.
        /// </remarks>
        public const int Rows = 8;

        [SerializeField] private MenuScreenController? _controller;

        [Header("Rows")]
        [Tooltip("One button per visible room. Length must be MenuRoomBrowserScreen.Rows.")]
        [SerializeField] private Button[] _roomButtons = new Button[Rows];
        [SerializeField] private Text[] _roomLabels = new Text[Rows];

        [Header("Controls")]
        [SerializeField] private Button? _refreshButton;
        [SerializeField] private Button? _createRoomButton;

        [Header("Readouts")]
        [SerializeField] private Text? _pingText;
        [SerializeField] private Text? _overflowText;
        [SerializeField] private Text? _errorText;

        [Header("Password prompt, for a private room")]
        [SerializeField] private GameObject? _passwordPrompt;
        [SerializeField] private InputField? _passwordField;
        [SerializeField] private Button? _passwordJoinButton;
        [SerializeField] private Button? _passwordCancelButton;

        /// <summary>The room the password prompt is asking about, or 0 when it is closed.</summary>
        private int _promptRoomId;

        private void Awake()
        {
            for (int i = 0; i < _roomButtons.Length; i++)
            {
                // Captured per iteration, because the closure below outlives the loop. Without
                // the copy every row would join whichever room the LAST iteration indexed.
                int row = i;
                Button button = _roomButtons[i];
                if (button != null) button.onClick.AddListener(() => OnRoomClicked(row));
            }

            if (_refreshButton != null) _refreshButton.onClick.AddListener(OnRefresh);
            if (_createRoomButton != null) _createRoomButton.onClick.AddListener(OnCreateRoom);
            if (_passwordJoinButton != null) _passwordJoinButton.onClick.AddListener(OnPasswordJoin);
            if (_passwordCancelButton != null) _passwordCancelButton.onClick.AddListener(ClosePrompt);

            ClosePrompt();
        }

        private void OnRefresh()
        {
            ClosePrompt();
            _controller?.RefreshRooms();
        }

        private void OnCreateRoom()
        {
            ClosePrompt();
            _controller?.ShowCreateRoom();
        }

        /// <summary>
        /// A row was pressed: join it, or ask for its password first.
        /// </summary>
        /// <remarks>
        /// <b>A room that cannot be joined is refused here rather than at the master</b> (P16
        /// 3.2). A full room, or one whose match has started, would come back
        /// <c>RoomFull</c> / <c>MatchAlreadyStarted</c> after a round trip that told the player
        /// nothing the row did not already say. Saying it immediately is the same answer without
        /// the wait — and the button is drawn non-interactable anyway, so this is the second of
        /// two guards rather than the only one.
        /// </remarks>
        private void OnRoomClicked(int row)
        {
            if (_controller == null) return;

            RoomInfo[] rooms = _controller.Rooms;
            if (row < 0 || row >= rooms.Length) return;

            RoomInfo room = rooms[row];

            if (!room.IsJoinable)
            {
                SetError(room.Lifecycle == RoomLifecycleState.Waiting
                    ? "That room is full."
                    : "That match has already started.");
                return;
            }

            if (room.IsPrivate)
            {
                OpenPrompt(room);
                return;
            }

            _controller.JoinRoom(room.RoomId, null);
        }

        private void OpenPrompt(RoomInfo room)
        {
            _promptRoomId = room.RoomId;

            if (_passwordField != null) _passwordField.text = string.Empty;
            if (_passwordPrompt != null) _passwordPrompt.SetActive(true);

            SetError($"'{room.Name}' is private. Enter its password.");
        }

        private void ClosePrompt()
        {
            _promptRoomId = 0;

            // Dropped on close, not on submit: a wrong password left in the field would be
            // re-sent by the next Join press without the player retyping it, and they would see
            // the same refusal twice for one mistake.
            if (_passwordField != null) _passwordField.text = string.Empty;
            if (_passwordPrompt != null) _passwordPrompt.SetActive(false);
        }

        private void OnPasswordJoin()
        {
            if (_controller == null || _promptRoomId == 0) return;

            string password = _passwordField != null ? _passwordField.text : string.Empty;
            if (password.Length == 0)
            {
                SetError("Enter the room password.");
                return;
            }

            int roomId = _promptRoomId;
            ClosePrompt();
            _controller.JoinRoom(roomId, password);
        }

        public override void SetError(string message)
        {
            if (_errorText != null) _errorText.text = message;
        }

        /// <summary>
        /// Redraws every row from the controller's room list. P16 3.2.
        /// </summary>
        /// <remarks>
        /// Driven from the controller's frame pump rather than from a coroutine or an
        /// <c>Update</c> of its own, so the list a player is looking at only ever changes when
        /// something actually changed.
        /// </remarks>
        public override void OnControllerStateChanged(MenuScreenController controller)
        {
            RoomInfo[] rooms = controller.Rooms;

            for (int i = 0; i < _roomButtons.Length; i++)
            {
                bool used = i < rooms.Length;

                Button button = _roomButtons[i];
                if (button != null)
                {
                    button.gameObject.SetActive(used);
                    button.interactable = used && !controller.IsBusy && rooms[i].IsJoinable;
                }

                Text label = i < _roomLabels.Length ? _roomLabels[i] : null!;
                if (label != null && used) label.text = Describe(rooms[i]);
            }

            if (_overflowText != null)
                _overflowText.text = rooms.Length > _roomButtons.Length
                    ? $"{rooms.Length - _roomButtons.Length} more room(s) not shown."
                    : string.Empty;

            if (_pingText != null)
                _pingText.text = controller.MasterPingMs < 0
                    ? "master --"
                    : $"master {controller.MasterPingMs} ms";

            if (_refreshButton != null) _refreshButton.interactable = !controller.IsBusy;
            if (_createRoomButton != null) _createRoomButton.interactable = !controller.IsBusy;
            if (_passwordJoinButton != null) _passwordJoinButton.interactable = !controller.IsBusy;
        }

        /// <summary>One row's text: the five facts a player needs before committing.</summary>
        /// <remarks>
        /// The lock glyph is a plain ASCII marker rather than an emoji: the Canvas uses Unity's
        /// built-in legacy font, which has no glyph for one, and a missing glyph renders as a
        /// blank — a private room would then be indistinguishable from a public one, which is
        /// criterion 1 failing quietly.
        /// </remarks>
        internal static string Describe(RoomInfo room)
        {
            string map = MapCatalog.TryGetScene(room.MapId, out string scene)
                ? scene
                // Not "Unknown": a map id this build has no entry for is a real thing a newer
                // master can name, and the number is what makes it reportable.
                : $"map {room.MapId}";

            string lockGlyph = room.IsPrivate ? "[LOCKED] " : string.Empty;

            return $"{lockGlyph}{room.Name}   {map}   {room.Players}/{room.MaxPlayers}   "
                   + Describe(room.Lifecycle);
        }

        private static string Describe(RoomLifecycleState state)
        {
            switch (state)
            {
                case RoomLifecycleState.Waiting: return "Waiting";
                case RoomLifecycleState.Starting: return "Starting";
                case RoomLifecycleState.InMatch: return "In match";
                case RoomLifecycleState.Ending: return "Ending";

                // A lifecycle byte from a newer master. Shown as itself for the reason
                // RoomInfo.Lifecycle does not throw on one: a list row must render.
                default: return $"state {(byte)state}";
            }
        }
    }
}
