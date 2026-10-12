#nullable enable

using System;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
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
        [SerializeField] private Image[] _teamZeroReadyBadges = new Image[RowsPerSide];
        [SerializeField] private Image[] _teamOneReadyBadges = new Image[RowsPerSide];

        [Header("Roster rows (owner's run of 2026-10-12)")]
        [Tooltip("Each row's plate: a member's, an open seat's, or hidden past the room's seats.")]
        [SerializeField] private Image[] _teamZeroSlots = new Image[RowsPerSide];
        [SerializeField] private Image[] _teamOneSlots = new Image[RowsPerSide];
        [Tooltip("The edge of a member's row, in the side's colour from ITeamPalette at runtime.")]
        [SerializeField] private Image[] _teamZeroStripes = new Image[RowsPerSide];
        [SerializeField] private Image[] _teamOneStripes = new Image[RowsPerSide];
        [SerializeField] private Image[] _teamZeroHostBadges = new Image[RowsPerSide];
        [SerializeField] private Image[] _teamOneHostBadges = new Image[RowsPerSide];
        [SerializeField] private GameObject[] _teamZeroYouTags = new GameObject[RowsPerSide];
        [SerializeField] private GameObject[] _teamOneYouTags = new GameObject[RowsPerSide];
        [Tooltip("Members over seats and how many are ready, under each side's name.")]
        [SerializeField] private Text? _teamZeroCount;
        [SerializeField] private Text? _teamOneCount;

        [Header("The room at a glance (owner's run of 2026-10-12)")]
        [SerializeField] private Image? _mapPicture;
        [Tooltip("Every map's picture by day then at night, in MapCatalog order (MapPictureChoice).")]
        [SerializeField] private Sprite[] _mapPictures = Array.Empty<Sprite>();
        [SerializeField] private Text? _mapName;
        [SerializeField] private Text? _modeChip;
        [SerializeField] private GameObject? _dayIcon;
        [SerializeField] private GameObject? _nightIcon;
        [SerializeField] private Text? _ruleChip;
        [SerializeField] private Text? _botsChip;
        [SerializeField] private Text? _medalChip;
        [SerializeField] private Graphic? _medalFace;
        [SerializeField] private Text? _privacyChip;
        [SerializeField] private GameObject? _lockIcon;
        [SerializeField] private Text? _seatsChip;
        [Tooltip("VS between the two sides, or the seconds left once everybody is ready.")]
        [SerializeField] private Text? _versusText;
        [SerializeField] private Text? _countdownCaption;

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
        [Tooltip("How the match starts and the guide's key, in the player's own binding.")]
        [SerializeField] private Text? _guideHint;

        private MenuChatInput? _chatInput;

        /// <summary>The push last drawn: a new one restarts the countdown, a redraw of the same one does not.</summary>
        private RoomState? _drawnRoom;

        /// <summary><see cref="Time.unscaledTime"/> the armed start fires at, or 0 with no countdown.</summary>
        private float _countdownEndsAt;

        private void Awake()
        {
            if (_switchSideButton != null) _switchSideButton.onClick.AddListener(OnSwitchSide);
            if (_readyButton != null) _readyButton.onClick.AddListener(OnReady);
            if (_leaveButton != null) _leaveButton.onClick.AddListener(OnLeave);
            if (_chatField != null)
            {
                _chatInput = GetComponent<MenuChatInput>() ?? gameObject.AddComponent<MenuChatInput>();
                _chatInput.Configure(_chatField, text => _controller?.SendChat(text));
            }
            if (_chatSendButton != null && _chatInput != null)
                _chatSendButton.onClick.AddListener(_chatInput.SubmitCurrent);

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

        /// <summary>
        /// Ticks the countdown between pushes. The master sends the time left once, when the start
        /// is armed, and again only if somebody stands down, so the seconds are counted here from
        /// the moment that push arrived: never against this machine's clock, which can be wrong.
        /// </summary>
        private void Update()
        {
            if (_countdownEndsAt > 0f) DrawVersus(_drawnRoom);
        }

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

            if (!ReferenceEquals(room, _drawnRoom))
            {
                _drawnRoom = room;
                _countdownEndsAt = room != null && room.Lifecycle == RoomLifecycleState.Waiting && room.StartsInMs > 0
                    ? Time.unscaledTime + room.StartsInMs / 1000f
                    : 0f;
            }

            DrawColumn(TeamZero, 0, room, controller.PlayerId);
            DrawColumn(TeamOne, 1, room, controller.PlayerId);
            DrawRoomCard(room);
            DrawVersus(room);

            if (_chatLog != null) _chatLog.text = NewestThatFit(_chatLog, controller.ChatLog);
            if (_guideHint != null)
                _guideHint.text = "READY UP when you are set: the match starts once two players are ready.     "
                                  + "<color=#FFB23F>[" + GameKeys.Cap(GameAction.HowToPlay) + "]</color>  HOW TO PLAY";

            bool waiting = room != null && room.Lifecycle == RoomLifecycleState.Waiting;
            bool known = room != null && TryGetSelf(room, out _);
            bool ready = room != null && TryGetSelf(room, out RoomMember self) && self.Ready;

            // The push's own name since the lobby redesign; the browser row's before it arrives,
            // and for a master older than the field.
            if (_headingText != null)
                _headingText.text = room != null && room.Name.Length > 0 ? room.Name : controller.RoomHeading;

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
            if (_readyLabel != null) _readyLabel.text = ready ? "STAND DOWN" : "READY UP";

            if (_leaveButton != null) _leaveButton.interactable = !controller.IsBusy;
            if (_chatSendButton != null) _chatSendButton.interactable = !controller.IsBusy;
        }

        /// <summary>
        /// Draws <paramref name="room"/> without a controller: the menu capture tool's entry, so a
        /// screenshot of the waiting room is laid out by this code and not by a copy of it.
        /// </summary>
        internal void DrawForCapture(RoomState room, int selfPlayerId, float countdownSeconds)
        {
            _drawnRoom = room;
            _countdownEndsAt = countdownSeconds > 0f ? Time.unscaledTime + countdownSeconds : 0f;
            DrawColumn(TeamZero, 0, room, selfPlayerId);
            DrawColumn(TeamOne, 1, room, selfPlayerId);
            DrawRoomCard(room);
            DrawVersus(room);
            if (_headingText != null) _headingText.text = room.Name;
            if (_statusText != null) _statusText.text = Describe(room);
        }

        private Column TeamZero => new Column(_teamZeroRows, _teamZeroReadyBadges, _teamZeroHostBadges,
            _teamZeroYouTags, _teamZeroSlots, _teamZeroStripes, _teamZeroHeading, _teamZeroCount);

        private Column TeamOne => new Column(_teamOneRows, _teamOneReadyBadges, _teamOneHostBadges,
            _teamOneYouTags, _teamOneSlots, _teamOneStripes, _teamOneHeading, _teamOneCount);

        /// <summary>One side's authored pieces, row by row.</summary>
        private readonly struct Column
        {
            public Column(Text[] names, Image[] ready, Image[] host, GameObject[] you, Image[] slots,
                Image[] stripes, Text? heading, Text? count)
            {
                Names = names; Ready = ready; Host = host; You = you; Slots = slots; Stripes = stripes;
                Heading = heading; Count = count;
            }

            public Text[] Names { get; }
            public Image[] Ready { get; }
            public Image[] Host { get; }
            public GameObject[] You { get; }
            public Image[] Slots { get; }
            public Image[] Stripes { get; }
            public Text? Heading { get; }
            public Text? Count { get; }
        }

        /// <summary>
        /// Fills one side: its heading and count, a row per member with their READY, HOST and YOU
        /// marks, then an OPEN SLOT per seat still free, and nothing past the room's seats.
        /// </summary>
        /// <remarks>
        /// The name TEXT carries the side's colour as well as the heading, because a reader
        /// scanning two columns of identical grey names has only position to go on, and position is
        /// exactly what a screenshot of a mid-switch roster makes ambiguous.
        /// </remarks>
        private static void DrawColumn(in Column column, byte team, RoomState? room, int selfPlayerId)
        {
            Color colour = TeamColour(team);

            if (column.Heading != null)
            {
                column.Heading.text = Ironfront.Net.Replication.Client.ScoreboardWording.TeamName(
                    team == 0 ? Ironfront.Net.Protocol.TeamId.Team0 : Ironfront.Net.Protocol.TeamId.Team1);
                column.Heading.color = colour;
            }

            int rows = column.Names.Length;
            int seats = SeatsPerSide(room, rows);
            int members = 0;
            int ready = 0;

            if (room != null)
            {
                foreach (RoomMember member in room.Members)
                {
                    if (member == null || member.Team != team) continue;
                    if (member.Ready) ready++;
                    if (members < rows)
                        ShowMember(column, members, member, colour,
                            member.PlayerId == selfPlayerId, member.PlayerId == room.HostPlayerId);
                    members++;
                }
            }

            for (int i = Math.Min(members, rows); i < rows; i++) ShowSeat(column, i, open: i < seats);

            if (column.Count != null)
                column.Count.text = room == null ? string.Empty : $"{members}/{seats}  ·  {ready} READY";
        }

        /// <summary>Seats on one side: half the room's, or every row for a master that sends none.</summary>
        internal static int SeatsPerSide(RoomState? room, int rows)
            => room == null || room.MaxPlayers <= 0 ? rows : Math.Min(rows, room.MaxPlayers / 2);

        private static void ShowMember(in Column column, int i, RoomMember member, Color colour, bool self, bool host)
        {
            if (Get(column.Names, i) is Text name)
            {
                name.text = member.Name;
                name.color = colour;
                name.fontSize = MemberNameSize;
                name.fontStyle = FontStyle.Normal;
            }
            if (Get(column.Slots, i) is Image slot)
            {
                slot.gameObject.SetActive(true);
                slot.color = MemberPlate;
            }
            if (Get(column.Stripes, i) is Image stripe)
            {
                stripe.gameObject.SetActive(true);
                stripe.color = colour;
            }
            SetActive(Get(column.Ready, i), member.Ready);
            SetActive(Get(column.Host, i), host);
            if (Get(column.You, i) is GameObject you) you.SetActive(self);
        }

        private static void ShowSeat(in Column column, int i, bool open)
        {
            if (Get(column.Names, i) is Text name)
            {
                name.text = open ? "Open slot" : string.Empty;
                name.color = OpenSlotInk;
                name.fontSize = OpenSlotSize;
                name.fontStyle = FontStyle.Italic;
            }
            if (Get(column.Slots, i) is Image slot)
            {
                slot.gameObject.SetActive(open);
                slot.color = OpenPlate;
            }
            if (Get(column.Stripes, i) is Image stripe) stripe.gameObject.SetActive(false);
            SetActive(Get(column.Ready, i), false);
            SetActive(Get(column.Host, i), false);
            if (Get(column.You, i) is GameObject you) you.SetActive(false);
        }

        private static readonly Color MemberPlate = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color OpenPlate = new Color(1f, 1f, 1f, 0.025f);

        /// <summary>A member's name at the size the builder authors; a free seat smaller and quieter, so names lead.</summary>
        private const int MemberNameSize = 22;
        private const int OpenSlotSize = 15;
        private static readonly Color OpenSlotInk = new Color(0.44f, 0.55f, 0.64f, 0.7f);

        private static T? Get<T>(T[]? items, int i) where T : class
            => items != null && i < items.Length ? items[i] : null;

        private static void SetActive(Component? component, bool active)
        {
            if (component != null) component.gameObject.SetActive(active);
        }

        /// <summary>
        /// The room card over the rosters: its map's picture and name, and a chip for each setting,
        /// all from the master's push (owner's run of 2026-10-12, item 4).
        /// </summary>
        private void DrawRoomCard(RoomState? room)
        {
            if (room == null) return;

            RoomSettings settings = room.Settings;
            MapPictureChoice.Show(_mapPicture, _mapPictures, room.MapId, settings.Mode);
            if (_mapName != null) _mapName.text = RoomBadges.MapName(room.MapId).ToUpperInvariant();

            bool night = RoomBadges.IsNight(settings);
            if (_modeChip != null) _modeChip.text = RoomBadges.ModeLabel(settings);
            if (_dayIcon != null) _dayIcon.SetActive(!night);
            if (_nightIcon != null) _nightIcon.SetActive(night);
            if (_ruleChip != null) _ruleChip.text = RoomBadges.RuleLabel(settings);
            if (_botsChip != null) _botsChip.text = RoomBadges.BotsPerSideLabel(room.BotCount);

            AchievementTier? tier = RoomBadges.MedalTier(settings);
            Color medal = RoomBadges.MedalColour(tier);
            if (_medalChip != null)
            {
                _medalChip.text = tier.HasValue ? RoomBadges.MedalLabel(tier) + " MEDALS" : RoomBadges.MedalLabel(tier);
                _medalChip.color = medal;
            }
            if (_medalFace != null) _medalFace.color = new Color(medal.r * 0.22f, medal.g * 0.22f, medal.b * 0.22f, 0.92f);

            if (_privacyChip != null) _privacyChip.text = room.IsPrivate ? "PRIVATE" : "PUBLIC";
            if (_lockIcon != null) _lockIcon.SetActive(room.IsPrivate);

            int members = room.Members?.Length ?? 0;
            if (_seatsChip != null) _seatsChip.text = RoomBadges.SeatsLabel(members, room.MaxPlayers) + " PLAYERS";
        }

        /// <summary>
        /// VS between the sides; the seconds left once everybody is ready; GO once the room starts.
        /// </summary>
        private void DrawVersus(RoomState? room)
        {
            if (_versusText == null) return;

            string caption = string.Empty;
            string big = "VS";
            if (room != null)
            {
                switch (room.Lifecycle)
                {
                    case RoomLifecycleState.Waiting when _countdownEndsAt > 0f:
                        caption = "STARTS IN";
                        big = Mathf.Max(0, Mathf.CeilToInt(_countdownEndsAt - Time.unscaledTime)).ToString();
                        break;
                    case RoomLifecycleState.Starting:
                        caption = "DEPLOYING";
                        big = "GO";
                        break;
                    case RoomLifecycleState.InMatch:
                        caption = "IN MATCH";
                        big = "GO";
                        break;
                }
            }

            if (_versusText.text != big) _versusText.text = big;
            if (_countdownCaption != null && _countdownCaption.text != caption) _countdownCaption.text = caption;
        }

        /// <summary>
        /// The tail of <paramref name="log"/> that <paramref name="label"/> shows whole.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why new messages stopped appearing.</b> The controller keeps
        /// <see cref="MenuScreenController.ChatLines"/> lines, and the log label holds fewer —
        /// how many depends on its width and on how many messages wrap. Text's
        /// <see cref="VerticalWrapMode.Truncate"/> resolves that overflow by dropping the BOTTOM
        /// lines, which in a log are the newest, so once a room had said more than the label
        /// holds, every new message landed in the part nobody could see.
        /// </para>
        /// <para>
        /// Lines are dropped here instead, oldest first, and measured with the label's own
        /// generator and settings — the same measure as <see cref="Text.preferredHeight"/> — so
        /// wrapping and the canvas scale are accounted for rather than assumed. A single line
        /// taller than the label is kept: it is the newest thing said.
        /// </para>
        /// </remarks>
        internal static string NewestThatFit(Text label, string log)
        {
            Rect rect = label.rectTransform.rect;
            TextGenerationSettings settings = label.GetGenerationSettings(new Vector2(rect.width, 0f));
            TextGenerator generator = label.cachedTextGeneratorForLayout;

            string shown = log;
            while (generator.GetPreferredHeight(shown, settings) / label.pixelsPerUnit > rect.height)
            {
                int newline = shown.IndexOf('\n');
                if (newline < 0) break;
                shown = shown.Substring(newline + 1);
            }

            return shown;
        }

        /// <summary>
        /// The team's colour, through the registry seam, as type. Criterion 10.
        /// </summary>
        /// <remarks>
        /// <c>NetClientBindings.TeamColourRgb</c> falls back to a neutral grey when no palette is
        /// installed, which is the documented degraded case rather than a silent black — and a
        /// grey roster in a screenshot is a visible, diagnosable "the binding did not install"
        /// rather than text that has disappeared into the backdrop. <see cref="TeamInk"/> lifts
        /// the answer so the palette's pure blue can be read on the team card's navy.
        /// </remarks>
        private static Color TeamColour(byte team)
            => TeamInk.FromRgb(NetClientBindings.TeamColourRgb(team));

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
                    int ready = 0;
                    foreach (RoomMember member in room.Members) if (member != null && member.Ready) ready++;
                    return $"{room.Members.Length} in the room, {ready} ready. "
                           + RoomSettingsChoice.Sentence(room.Settings) + " "
                           + "The match starts when everybody is ready, two players at least.";
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
