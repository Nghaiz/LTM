#nullable enable

using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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

        /// <summary>
        /// How many of this player's running matches the YOUR MATCHES section can show. Two, not
        /// more: a player can leave a running match only by walking out of it, and a second one
        /// is already unusual. A third is counted in the overflow line rather than dropped.
        /// </summary>
        public const int RejoinRows = 2;

        /// <summary>The y of the table's header strip, in the panel's space. Authored and read here.</summary>
        /// <remarks>
        /// 160 since P30's redesign, which put the header block (kicker, heading, search) where the
        /// other operations screens have theirs and moved the table up 48px with it.
        /// </remarks>
        public const float TableTop = 160f;

        /// <summary>The distance between two rows' centres.</summary>
        public const float RowPitch = 46f;

        /// <summary>
        /// The lowest a row's centre may sit before it would run into the overflow line and the
        /// buttons under the table: the eighth row's own centre, so the authored table exactly
        /// fits and a table pushed down by YOUR MATCHES gives up rows rather than the gap.
        /// </summary>
        private const float LowestRowCentre = TableTop - 40f - ((Rows - 1) * RowPitch);

        [SerializeField] private MenuScreenController? _controller;

        [Header("Rows")]
        [Tooltip("One row per visible room. Length must be MenuRoomBrowserScreen.Rows.")]
        [SerializeField] private RoomRow[] _rows = new RoomRow[Rows];

        [Header("Your matches")]
        [Tooltip("Running matches this player left and may rejoin. Length must be RejoinRows.")]
        [SerializeField] private RoomRow[] _rejoinRows = new RoomRow[RejoinRows];

        [Tooltip("The YOUR MATCHES section. Shown only while there is a match to go back to.")]
        [SerializeField] private GameObject? _rejoinSection;

        [Tooltip("The open-rooms table, header and rows, moved down under YOUR MATCHES.")]
        [SerializeField] private RectTransform? _openRoomsTable;

        [Header("Room art and the list's state (owner's run of 2026-10-12)")]
        [Tooltip("Every map's picture by day then at night, in MapCatalog order (MapPictureChoice).")]
        [SerializeField] private Sprite[] _mapPictures = Array.Empty<Sprite>();

        [Tooltip("How many open rooms are listed and how many players are in them.")]
        [SerializeField] private Text? _summaryText;

        [Tooltip("Shown in place of the table when no open room is listed.")]
        [SerializeField] private GameObject? _emptyState;

        [Tooltip("The empty state's sentence: no rooms at all, or none matching the search.")]
        [SerializeField] private Text? _emptyText;

        /// <summary>
        /// One visible room: its map's picture, its name and map, a chip for each setting, how full
        /// it is and its status (owner's run of 2026-10-12, item 4: "icons, colours, badges, chips,
        /// and more about each room").
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A row used to be a single <see cref="Text"/>, then four plain cells.</b> The settings
        /// sat in one sentence in the MAP cell ("Dustbowl · 100 bots · LEAD BY 200"), so nothing
        /// lined up and a private room was a "[LOCKED]" prefix on its status. Each setting now has
        /// its own chip in its own column, and every chip is computed by <see cref="RoomBadges"/>
        /// from what the room list already carries.
        /// </para>
        /// <para>
        /// <b>The medal chip</b> names the largest achievement size a round in the room counts for
        /// (<c>CareerRules.IsRoundOf</c>), so a player can see before joining which round
        /// achievements the room can award. The readout above the table stays labelled
        /// <c>master</c>: it is this client's round trip to the master, not a per-room ping.
        /// </para>
        /// </remarks>
        [Serializable]
        private struct RoomRow
        {
            public Button Join;
            public Text Name;
            public Text Map;
            public Text Players;
            public Text Status;
            public Image Thumb;
            public Text Mode;
            public GameObject DayIcon;
            public GameObject NightIcon;
            public Text Rule;
            public Text Bots;
            public Text Medal;
            public Graphic MedalFace;
            public GameObject Lock;
            public Image SeatsFill;
            public Image StatusDot;
        }

        [Header("Controls")]
        [SerializeField] private InputField? _searchField;
        [SerializeField] private Button? _refreshButton;
        [SerializeField] private Button? _createRoomButton;

        [Header("Filters and quick match (owner's list of 2026-10-09, item 1)")]
        [Tooltip("Cycles ALL MODES, then each game mode; the list shows only rooms of that mode.")]
        [SerializeField] private Button? _modeFilterButton;
        [SerializeField] private Text? _modeFilterLabel;
        [Tooltip("Joins the fullest open room that still has a place.")]
        [SerializeField] private Button? _quickMatchButton;

        [Header("Account")]
        [SerializeField] private Text? _accountText;
        [SerializeField] private Button? _signOutButton;

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

        private RoomInfo[] _visibleRooms = Array.Empty<RoomInfo>();
        private RoomInfo[] _visibleRejoins = Array.Empty<RoomInfo>();

        /// <summary>-1 for every mode, else an index into <see cref="RoomSettingsChoice.Modes"/>.</summary>
        private int _modeFilter = -1;

        private void Awake()
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                // Captured per iteration, because the closure below outlives the loop. Without
                // the copy every row would join whichever room the LAST iteration indexed.
                int row = i;
                Button button = _rows[i].Join;
                if (button != null) button.onClick.AddListener(() => OnRoomClicked(row));
            }

            for (int i = 0; i < _rejoinRows.Length; i++)
            {
                int row = i;
                Button button = _rejoinRows[i].Join;
                if (button != null) button.onClick.AddListener(() => OnRejoinClicked(row));
            }

            if (_refreshButton != null) _refreshButton.onClick.AddListener(OnRefresh);
            if (_createRoomButton != null) _createRoomButton.onClick.AddListener(OnCreateRoom);
            if (_searchField != null) _searchField.onValueChanged.AddListener(OnSearchChanged);
            if (_modeFilterButton != null) _modeFilterButton.onClick.AddListener(OnModeFilter);
            if (_quickMatchButton != null) _quickMatchButton.onClick.AddListener(OnQuickMatch);
            if (_signOutButton != null) _signOutButton.onClick.AddListener(OnSignOut);
            if (_passwordJoinButton != null) _passwordJoinButton.onClick.AddListener(OnPasswordJoin);
            if (_passwordCancelButton != null) _passwordCancelButton.onClick.AddListener(ClosePrompt);

            ClosePrompt();
        }

        private void Update()
        {
            if (_passwordPrompt == null || !_passwordPrompt.activeSelf) return;
            if (GameOverlays.OwnsEscape) return;

            if (Input.GetKeyDown(KeyCode.Escape)) ClosePrompt();
            else if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                     && EventSystem.current != null
                     && EventSystem.current.currentSelectedGameObject == _passwordField?.gameObject)
                OnPasswordJoin();
        }

        private void OnSearchChanged(string query)
        {
            ClosePrompt();
            if (_controller != null) DrawRooms(_controller);
        }

        private void OnModeFilter()
        {
            ClosePrompt();
            _modeFilter = NextModeFilter(_modeFilter);
            if (_modeFilterLabel != null) _modeFilterLabel.text = ModeFilterLabel(_modeFilter);
            if (_controller != null) DrawRooms(_controller);
        }

        /// <summary>The filter after <paramref name="current"/>: every mode, then each mode in turn.</summary>
        internal static int NextModeFilter(int current)
            => current + 1 >= RoomSettingsChoice.Modes.Length ? -1 : current + 1;

        internal static string ModeFilterLabel(int filter)
            => filter < 0 ? "ALL MODES" : RoomSettingsChoice.ModeOption(RoomSettingsChoice.Modes[filter]);

        /// <summary>
        /// QUICK MATCH: into the fullest open room that still has a place, so the player is in a
        /// battle at once and a half-empty room fills before a new one is needed.
        /// </summary>
        private void OnQuickMatch()
        {
            ClosePrompt();
            if (_controller == null) return;

            RoomInfo? pick = QuickMatchPick(_controller.Rooms);
            if (pick == null)
            {
                SetError("No open room has a place right now. Create one and others will join you.");
                return;
            }

            _controller.JoinRoom(pick.RoomId, null);
        }

        /// <summary>
        /// The room QUICK MATCH joins: public, waiting and not full; the most players first, then
        /// the most seats left, then the oldest. Null when there is none.
        /// </summary>
        internal static RoomInfo? QuickMatchPick(RoomInfo[]? rooms)
        {
            RoomInfo? best = null;
            if (rooms == null) return null;
            foreach (RoomInfo room in rooms)
            {
                if (room == null || room.IsPrivate || room.CanRejoin || !room.IsJoinable) continue;
                if (best == null
                    || room.Players > best.Players
                    || (room.Players == best.Players && room.MaxPlayers - room.Players > best.MaxPlayers - best.Players)
                    || (room.Players == best.Players && room.MaxPlayers == best.MaxPlayers && room.RoomId < best.RoomId))
                    best = room;
            }
            return best;
        }

        private void OnSignOut()
        {
            ClosePrompt();
            _controller?.SignOut();
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

            RoomInfo[] rooms = _visibleRooms;
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

        /// <summary>A YOUR MATCHES row was pressed: go back into that running match.</summary>
        private void OnRejoinClicked(int row)
        {
            if (_controller == null) return;

            RoomInfo[] rooms = _visibleRejoins;
            if (row < 0 || row >= rooms.Length) return;

            ClosePrompt();
            _controller.RejoinMatch(rooms[row].RoomId);
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
            => DrawRooms(controller);

        private void DrawRooms(MenuScreenController controller)
        {
            string query = _searchField != null ? _searchField.text : string.Empty;
            Split(controller.Rooms, query, _modeFilter, out RoomInfo[] rejoins, out RoomInfo[] rooms);

            Draw(rejoins, rooms, controller.IsBusy);

            if (_pingText != null)
                _pingText.text = controller.MasterPingMs < 0
                    ? "master --"
                    : $"master {controller.MasterPingMs} ms";

            if (_refreshButton != null) _refreshButton.interactable = !controller.IsBusy;
            if (_createRoomButton != null) _createRoomButton.interactable = !controller.IsBusy;
            if (_quickMatchButton != null) _quickMatchButton.interactable = !controller.IsBusy;
            if (_signOutButton != null) _signOutButton.interactable = !controller.IsBusy;
            if (_accountText != null)
                _accountText.text = controller.SignedInName.Length > 0
                    ? "SIGNED IN AS  <color=#FFFFFF>" + controller.SignedInName.ToUpperInvariant() + "</color>"
                    : string.Empty;
            if (_passwordJoinButton != null) _passwordJoinButton.interactable = !controller.IsBusy;
        }

        /// <summary>
        /// Draws both sections from lists already split by <see cref="Split"/>. Also what the menu
        /// capture tool calls, so a screenshot is laid out by this code and not by a copy of it.
        /// </summary>
        internal void Draw(RoomInfo[] rejoins, RoomInfo[] rooms, bool busy)
        {
            // YOUR MATCHES first: it moves the open table, so it decides how many open rows fit.
            int shownRejoins = Math.Min(rejoins.Length, _rejoinRows.Length);
            _visibleRejoins = rejoins;
            if (_rejoinSection != null) _rejoinSection.SetActive(shownRejoins > 0);

            for (int i = 0; i < _rejoinRows.Length; i++)
            {
                RoomRow row = _rejoinRows[i];
                bool used = i < shownRejoins;
                if (row.Join != null)
                {
                    GameObject rowObject = row.Join.transform.parent != null
                        ? row.Join.transform.parent.gameObject
                        : row.Join.gameObject;
                    rowObject.SetActive(used);
                    row.Join.interactable = used && !busy;
                }

                if (!used) continue;

                RoomInfo room = rejoins[i];
                FillRow(row, room, _mapPictures);
                if (row.Status != null) row.Status.text = RejoinStatusLabel(room);
                if (row.StatusDot != null) row.StatusDot.color = UiStyle.Orange;
            }

            float shift = RejoinSectionHeight(shownRejoins);
            if (_openRoomsTable != null) _openRoomsTable.anchoredPosition = new Vector2(0f, -shift);
            int openRows = Math.Min(_rows.Length, OpenRowCapacity(shift));

            _visibleRooms = rooms;

            for (int i = 0; i < _rows.Length; i++)
            {
                RoomRow row = _rows[i];
                bool used = i < rooms.Length && i < openRows;

                if (row.Join != null)
                {
                    GameObject rowObject = row.Join.transform.parent != null
                        ? row.Join.transform.parent.gameObject
                        : row.Join.gameObject;
                    rowObject.SetActive(used);
                    row.Join.interactable = used && !busy && rooms[i].IsJoinable;
                }

                if (!used) continue;

                FillRow(row, rooms[i], _mapPictures);
            }

            if (_overflowText != null)
                _overflowText.text = OverflowLabel(
                    Math.Max(0, rooms.Length - openRows), Math.Max(0, rejoins.Length - shownRejoins));

            if (_summaryText != null) _summaryText.text = SummaryLabel(rooms);

            bool empty = rooms.Length == 0;
            if (_emptyState != null) _emptyState.SetActive(empty);
            if (_emptyText != null && empty)
            {
                string query = _searchField != null ? _searchField.text : string.Empty;
                _emptyText.text = string.IsNullOrWhiteSpace(query) && _modeFilter < 0
                    ? "Create one and it shows here for everyone, or refresh in a moment."
                    : "No open room matches the search or the mode filter.";
            }
        }

        /// <summary>Writes one room into one row: its picture, name, map, chips, seats and status.</summary>
        private static void FillRow(RoomRow row, RoomInfo room, Sprite[] pictures)
        {
            RoomSettings settings = room.Settings;

            if (row.Thumb != null) MapPictureChoice.Show(row.Thumb, pictures, room.MapId, settings.Mode);
            if (row.Name != null) row.Name.text = room.Name;
            if (row.Map != null) row.Map.text = MapLabel(room);

            bool night = RoomBadges.IsNight(settings);
            if (row.Mode != null) row.Mode.text = RoomBadges.ModeLabel(settings);
            if (row.DayIcon != null) row.DayIcon.SetActive(!night);
            if (row.NightIcon != null) row.NightIcon.SetActive(night);
            if (row.Rule != null) row.Rule.text = RoomBadges.RuleLabel(settings);
            if (row.Bots != null) row.Bots.text = RoomBadges.BotsLabel(room.BotCount);

            AchievementTier? tier = RoomBadges.MedalTier(settings);
            Color medal = RoomBadges.MedalColour(tier);
            if (row.Medal != null)
            {
                row.Medal.text = RoomBadges.MedalLabel(tier);
                row.Medal.color = medal;
            }
            if (row.MedalFace != null) row.MedalFace.color = new Color(medal.r * 0.22f, medal.g * 0.22f, medal.b * 0.22f, 0.92f);

            if (row.Lock != null) row.Lock.SetActive(room.IsPrivate);

            if (row.Players != null) row.Players.text = PlayerLabel(room);
            if (row.SeatsFill != null)
            {
                row.SeatsFill.fillAmount = RoomBadges.SeatsFill(room.Players, room.MaxPlayers);
                row.SeatsFill.color = RoomBadges.SeatsColour(room.Players, room.MaxPlayers);
            }

            Color status = RoomBadges.StatusColour(room.Lifecycle, room.Players, room.MaxPlayers);
            if (row.Status != null)
            {
                row.Status.text = StatusLabel(room);
                row.Status.color = status;
            }
            if (row.StatusDot != null) row.StatusDot.color = status;
        }

        /// <summary>
        /// The line above the table: how many open rooms are listed and how many players they hold,
        /// so an empty-looking evening and a busy one read differently before anybody scrolls.
        /// </summary>
        internal static string SummaryLabel(RoomInfo[] rooms)
        {
            if (rooms.Length == 0) return "No open rooms";
            int players = 0;
            foreach (RoomInfo room in rooms) players += room.Players;
            string roomWord = rooms.Length == 1 ? "open room" : "open rooms";
            string playerWord = players == 1 ? "player" : "players";
            return $"{rooms.Length} {roomWord}  ·  {players} {playerWord} in them";
        }

        /// <summary>
        /// Splits the master's list into this player's running matches and the rooms anyone may
        /// look at, the second filtered by the search box.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A running match is listed only to the people who played in it</b> (owner, 2026-09-30):
        /// "những người chưa từng vào cái phòng này thì sẽ không hiện gì ở phía họ". The master
        /// already refuses everyone else, so a row that could only ever say "That match has
        /// already started" is not drawn.
        /// </para>
        /// <para>
        /// The search box filters the open rooms only. YOUR MATCHES is at most a couple of rows,
        /// and hiding the way back into your own match behind a search term is the one filter
        /// result nobody wants.
        /// </para>
        /// </remarks>
        internal static void Split(RoomInfo[]? all, string query, out RoomInfo[] rejoins, out RoomInfo[] open)
            => Split(all, query, -1, out rejoins, out open);

        /// <summary>As above, the open rooms also limited to one mode unless <paramref name="modeFilter"/> is -1.</summary>
        internal static void Split(RoomInfo[]? all, string query, int modeFilter, out RoomInfo[] rejoins, out RoomInfo[] open)
        {
            var mine = new List<RoomInfo>();
            var others = new List<RoomInfo>();

            if (all != null)
            {
                foreach (RoomInfo room in all)
                {
                    if (room == null) continue;
                    if (room.CanRejoin) mine.Add(room);
                    else if (IsOpenRoom(room) && MatchesSearch(room, query) && MatchesMode(room, modeFilter)) others.Add(room);
                }
            }

            rejoins = mine.ToArray();
            open = others.ToArray();
        }

        /// <summary>A room the open list draws: gathering players, or about to start.</summary>
        internal static bool IsOpenRoom(RoomInfo room)
            => room.Lifecycle == RoomLifecycleState.Waiting || room.Lifecycle == RoomLifecycleState.Starting;

        /// <summary>
        /// How far YOUR MATCHES pushes the open table down: its header strip, its rows and a gap.
        /// Zero when it is hidden, which is the authored layout.
        /// </summary>
        internal static float RejoinSectionHeight(int rows) => rows <= 0 ? 0f : 38f + (rows * RowPitch);

        /// <summary>How many open rows fit once the table has moved down by <paramref name="shift"/>.</summary>
        internal static int OpenRowCapacity(float shift)
        {
            float firstRow = TableTop - 40f - shift;
            if (firstRow < LowestRowCentre) return 0;
            return (int)Math.Floor((firstRow - LowestRowCentre) / RowPitch) + 1;
        }

        /// <summary>The line under the table, naming what did not fit in either section.</summary>
        internal static string OverflowLabel(int hiddenRooms, int hiddenMatches)
        {
            if (hiddenRooms == 0 && hiddenMatches == 0) return string.Empty;
            if (hiddenMatches == 0) return $"{hiddenRooms} more room(s) not shown.";
            if (hiddenRooms == 0) return $"{hiddenMatches} more of your matches not shown.";
            return $"{hiddenRooms} more room(s) and {hiddenMatches} of your matches not shown.";
        }

        /// <summary>
        /// The STATUS cell of a YOUR MATCHES row: where the match is, and the side a rejoin puts
        /// the player on. Blue is team 0 and red team 1, as on the in-match HUD.
        /// </summary>
        internal static string RejoinStatusLabel(RoomInfo room)
        {
            string phase = room.Lifecycle == RoomLifecycleState.Starting ? "STARTING" : "IN MATCH";
            string side = room.RejoinTeam == 1 ? "<color=#FF6B6B>RED</color>" : "<color=#6FA8FF>BLUE</color>";
            return $"{phase} · {side}";
        }

        /// <summary>The MAP cell: the scene this build plays for the room's map id.</summary>
        /// <remarks>
        /// Not "Unknown" for an id with no entry: a map id this build cannot name is a real thing a
        /// newer master can send, and the number is what makes it reportable.
        /// </remarks>
        internal static string MapLabel(RoomInfo room) => RoomBadges.MapName(room.MapId);

        /// <summary>The mode filter: every mode, or only rooms of the chosen one.</summary>
        internal static bool MatchesMode(RoomInfo room, int modeFilter)
            => modeFilter < 0 || room.GameMode == (byte)RoomSettingsChoice.Modes[modeFilter];

        /// <summary>The PLAYERS cell.</summary>
        internal static string PlayerLabel(RoomInfo room) => $"{room.Players}/{room.MaxPlayers}";

        /// <summary>
        /// The STATUS chip: the lifecycle, or FULL for a waiting room with no seat left. A private
        /// room says so with its own PRIVATE chip and lock icon, which is a sprite rather than a
        /// font glyph, so it cannot render as a blank the way an emoji lock would.
        /// </summary>
        internal static string StatusLabel(RoomInfo room)
            => RoomBadges.StatusLabel(room.Lifecycle, room.Players, room.MaxPlayers);

        internal static bool MatchesSearch(RoomInfo room, string query)
        {
            if (room == null || string.IsNullOrWhiteSpace(query)) return room != null;

            string needle = query.Trim();
            if (room.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return MapLabel(room).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }
}
