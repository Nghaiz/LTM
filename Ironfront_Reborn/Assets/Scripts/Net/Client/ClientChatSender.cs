using System;
using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The one production sender of <c>C_CHAT</c>, and the in-match chat box that draws what
    /// comes back. Phase P6 task 3.3, ledger X-8; reworked after the 2026-09-28 playtest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Enter opens, Enter sends, Esc closes</b> — the rules live in <see cref="ChatPanelModel"/>.
    /// Shift+Enter opens on the team channel; while the box is open, Tab, the arrows and a click on
    /// the ALL / TEAM tabs change channel (owner request 2026-09-29). The server decides who a
    /// team line reaches; this box only asks, and marks each line with the channel it came on.
    /// Enter used to be the deploy screen's key as well (the <c>Loadout</c> axis in
    /// <c>ProjectSettings/InputManager.asset</c> is bound to return/enter), so the press that sent
    /// a line also opened the deploy screen in the same frame: the send ran here first, cleared
    /// the typing flag, and <c>FpsActorController</c> read the same press a few
    /// microseconds later with nothing left to stop it. A networked match no longer reads that
    /// axis at all, and <see cref="LocalTextEntry.OwnsKeyboard"/> covers the frame the box closes
    /// on, so the key that closed it cannot also be read by whatever runs after.
    /// </para>
    /// <para>
    /// <b>Sender and presenter in one component, deliberately.</b> They are two halves of one
    /// conversation and share the history: a player needs to see their own line land to know it
    /// was sent at all.
    /// </para>
    /// <para>
    /// <b>Drawn from <c>OnGUI</c>.</b> This component is added at runtime by
    /// <c>NetClientBootstrap</c>, so an immediate-mode box has no scene or prefab to be missing
    /// from — a chat box present in one map and absent from the next would read as the server
    /// dropping messages.
    /// </para>
    /// </remarks>
    // Before the EventSystem (-1000). The Enter that opens or sends a line must clear the uGUI
    // selection BEFORE the input module reads the same press as Submit and clicks whatever the
    // deploy screen last had selected -- DEPLOY included. Running after it, the clear lands one
    // frame late.
    [DefaultExecutionOrder(-1010)]
    [DisallowMultipleComponent]
    public sealed class ClientChatSender : MonoBehaviour
    {
        /// <summary>History lines shown while the box is open.</summary>
        [Tooltip("History lines shown while the chat box is open.")]
        [SerializeField] private int _openLines = 8;

        /// <summary>History lines shown while the box lingers after a line is sent or received.</summary>
        [Tooltip("History lines shown after a line is sent or received, before the box fades.")]
        [SerializeField] private int _lingerLines = 6;

        /// <summary>Seconds the history stays up after a line is sent or received.</summary>
        [Tooltip("Seconds the history stays on screen after a line is sent or received.")]
        [SerializeField] private float _lingerSeconds = ChatPanelModel.DefaultLingerSeconds;

        private NetClientBootstrap _client;
        private NetClientCombatPresenter _names;
        private ChatPanelModel _model;

        private readonly byte[] _body = new byte[ChatTextMessage.MaxClientBodySize];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        /// <summary>Set when the box opens, consumed by the first <c>OnGUI</c> after it.</summary>
        /// <remarks>
        /// The focus grab happens once, not every frame: calling <c>GUI.FocusControl</c> on every
        /// repaint re-seats IMGUI's editing state and takes the caret with it.
        /// </remarks>
        private bool _focusPending;

        /// <summary>Set when the box closes, so the next <c>OnGUI</c> hands IMGUI's focus back.</summary>
        private bool _releaseFocusPending;

        /// <summary>The frame the box last opened on. See <see cref="HandleBoxKeys"/>.</summary>
        private int _openedOnFrame = -1;

        /// <summary>Whether this box asked for the pointer, so it gives back only what it took.</summary>
        private bool _pointerTaken;

        /// <summary><c>C_CHAT</c> messages sent. Zero after typing is the tell.</summary>
        public long MessagesSent { get; private set; }

        /// <summary>Lines that arrived from the server, including this client's own.</summary>
        public long MessagesReceived { get; private set; }

        /// <summary>
        /// Drafts refused because nothing survived sanitizing, or because the encoded line did
        /// not fit the wire bound. Surfaced so "I pressed Enter and nothing happened" has an
        /// answer somewhere.
        /// </summary>
        public long DraftsRefused { get; private set; }

        /// <summary>True while the chat line has focus and gameplay keys are ignored.</summary>
        public bool IsComposing => _model != null && _model.IsComposing;

        private void Awake()
        {
            _model = new ChatPanelModel(lingerSeconds: _lingerSeconds);

            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(nameof(ClientChatSender), out _client))
            {
                enabled = false;
                return;
            }

            // Optional. It owns the actor-id-to-name table built from S_PLAYER_LIST and the team
            // of each row; without it a line is attributed by actor id, which is worse than a
            // name and better than nothing. Chat must not depend on the combat presenter existing.
            _names = GetComponent<NetClientCombatPresenter>();
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnChat += OnChat;
        }

        private void OnDisable()
        {
            if (_client != null) _client.Router.OnChat -= OnChat;

            // A disconnect mid-compose would otherwise leave the line open across a reconnect,
            // eating the player's movement keys with no server to send to.
            if (_model != null) _model.Abandon();
            Publish();
            _focusPending = false;
        }

        private void OnDestroy() => DestroyTextures();

        private void Update()
        {
            if (_client == null || !_client.IsConnected)
            {
                if (_model.IsComposing)
                {
                    _model.Abandon();
                    _releaseFocusPending = true;
                    Publish();
                }

                return;
            }

            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);

            if (!_model.IsComposing)
            {
                if (!enter) return;

                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                _model.PressEnter(Time.unscaledTime, out _, shift ? ChatChannel.Team : ChatChannel.All);
                _openedOnFrame = Time.frameCount;
                _focusPending = true;
                ReleaseUiSelection();
                Publish();
                return;
            }

            // Every frame the box is open: a screen closing under it may have locked the pointer.
            NetClientBindings.ChatPointer?.Invoke(true);

            // Usually NOT reached while the box is open, and that is the shipped defect this
            // replaces: once the text field holds the keyboard, Input.GetKeyDown stops reporting
            // Enter and Esc on Windows (the IME takes them), so a line could be typed and never
            // sent. OnGUI reads the same two keys from IMGUI's own events -- see HandleBoxKeys.
            // This path stays for the machines where the legacy read does see them; whichever
            // runs first closes the box and the other finds it closed.
            if (Input.GetKeyDown(KeyCode.Escape)) Cancel();
            else if (enter) Submit();
        }

        /// <summary>Enter on an open box: send what was typed, close the input line.</summary>
        private void Submit()
        {
            if (_model.PressEnter(Time.unscaledTime, out string submitted) == ChatKeyOutcome.Submitted)
                Send(submitted, _model.Channel);

            _releaseFocusPending = true;
            ReleaseUiSelection();
            Publish();
        }

        /// <summary>Esc on an open box: close it and throw the draft away.</summary>
        private void Cancel()
        {
            _model.PressEscape();
            _releaseFocusPending = true;
            Publish();
        }

        /// <summary>
        /// Enter and Esc as IMGUI sees them, while the text field holds the keyboard.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes a line sendable at all.</b> Measured 2026-09-28 against the live
        /// servers: with the draft field focused, neither Enter nor Esc ever reached
        /// <c>Input.GetKeyDown</c> -- the typed characters did, through IMGUI -- so the box
        /// opened, took the text, and then could be neither sent nor closed. That is the
        /// owner's "the chat opens and nothing can be sent".
        /// </para>
        /// <para>
        /// <b>The Enter that opened the box is ignored here.</b> It reaches IMGUI in the frame it
        /// was pressed, after <c>Update</c> has already opened the box, and would otherwise send
        /// an empty line and close it again in the same frame. A human cannot open, type and
        /// send inside two frames, so the window costs nothing.
        /// </para>
        /// </remarks>
        private void HandleBoxKeys(Event current)
        {
            if (current == null || current.type != EventType.KeyDown) return;

            // Tab and the arrows change channel, and only here: read on both paths, a Tab that
            // reached Update as well would swap the channel twice in one press.
            if (current.keyCode == KeyCode.Tab || current.character == '\t')
            {
                current.Use();
                if (current.keyCode == KeyCode.Tab) _model.ToggleChannel();
                return;
            }

            if ((current.keyCode == KeyCode.LeftArrow || current.keyCode == KeyCode.RightArrow)
                && _model.PressArrow(current.keyCode == KeyCode.RightArrow))
            {
                current.Use();
                return;
            }

            bool enter = current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter
                         || current.character == '\n' || current.character == '\r';
            bool escape = current.keyCode == KeyCode.Escape || current.character == '\u001b';
            if (!enter && !escape) return;

            current.Use();

            if (Time.frameCount - _openedOnFrame <= 1) return;

            if (escape) Cancel();
            else Submit();
        }

        /// <summary>
        /// Hands the composing state to the input path, which suppresses gameplay keys, and the
        /// pointer to the box while it is open so its channel tabs can be clicked.
        /// </summary>
        private void Publish()
        {
            bool composing = _model != null && _model.IsComposing;
            LocalTextEntry.Composing = composing;

            if (composing)
            {
                _pointerTaken = true;
                NetClientBindings.ChatPointer?.Invoke(true);
            }
            else if (_pointerTaken)
            {
                _pointerTaken = false;
                NetClientBindings.ChatPointer?.Invoke(false);
            }
        }

        /// <summary>
        /// Clears the uGUI selection, so the Enter that opens or sends a line is not also the
        /// <c>Submit</c> that clicks whichever button the deploy screen last had selected.
        /// </summary>
        private static void ReleaseUiSelection()
        {
            EventSystem events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null)
                events.SetSelectedGameObject(null);
        }

        /// <summary>
        /// Frames one line and puts it on the reliable channel.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Reliable, on channel 2</b>: a dropped line is a player who said something nobody
        /// heard, with nothing to re-send it.
        /// </para>
        /// <para>
        /// <b>Sanitized and clipped before encoding.</b> The clip is in CHARACTERS because that is
        /// where a boundary can be found without splitting a code point; the wire bound is in
        /// bytes and <see cref="ChatTextMessage.Encode"/> refuses rather than truncating when a
        /// line of Vietnamese outgrows it. That refusal is counted, not silent.
        /// </para>
        /// <para>
        /// <b>Nothing is echoed locally.</b> The line appears when the server broadcasts it back,
        /// which is what makes what a player sees the same thing everybody else sees.
        /// </para>
        /// </remarks>
        private void Send(string draft, ChatChannel channel)
        {
            string text = PlayerNameSanitizer.Sanitize(draft, ChatTextMessage.MaxTextCharacters);
            if (text.Length == 0)
            {
                DraftsRefused++;
                return;
            }

            Span<byte> encoded = stackalloc byte[ChatTextMessage.MaxTextBytes];
            int textLength = ChatTextMessage.Encode(text, encoded);
            if (textLength < 0)
            {
                DraftsRefused++;
                return;
            }

            int bodyLength = ChatTextMessage.WriteClient(_body, channel, encoded.Slice(0, textLength));
            if (bodyLength < 0)
            {
                DraftsRefused++;
                return;
            }

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);

            if (!writer.WriteMessage(
                    ClientMessageType.Chat, new ReadOnlySpan<byte>(_body, 0, bodyLength)))
                return;

            if (!writer.TryFinish(out int total)) return;

            _client.Send(
                ChannelId.ReliableOrdered, new ReadOnlySpan<byte>(_payload, 0, total),
                reliable: true);

            MessagesSent++;
        }

        /// <summary>
        /// A line arrived, already sanitized by the router at this client's own ingress.
        /// </summary>
        /// <remarks>
        /// No <c>IsLocalActor</c> guard: a line from a remote player is exactly what chat is for.
        /// </remarks>
        private void OnChat(byte actorId, ChatChannel channel, string text)
        {
            MessagesReceived++;
            _model.Add(actorId, channel, text, Time.unscaledTime);
        }

        // ------------------------------------------------------------------------------ drawing

        private const string DraftControlName = "ironfront.chat.draft";

        /// <summary>Screen pixels kept clear under the box for the health and ammo readout.</summary>
        private const float BottomClearance = 118f;

        private const float ReferenceHeight = 1080f;

        private GUIStyle _titleStyle;
        private GUIStyle _metaStyle;
        private GUIStyle _rosterStyle;
        private GUIStyle _lineStyle;
        private GUIStyle _shadowStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _panelStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _tabAllStyle;
        private GUIStyle _tabTeamStyle;
        private GUIStyle _chipStyle;
        private Texture2D _panelTexture;
        private Texture2D _inputTexture;
        private Texture2D _dividerTexture;
        private Texture2D _tabTexture;
        private Texture2D _tabHoverTexture;
        private Texture2D _tabAllTexture;
        private Texture2D _tabTeamTexture;
        private int _tabTeamRgb = -1;
        private float _stylesScale = -1f;

        private readonly StringBuilder _text = new StringBuilder(160);
        private readonly List<string> _lineRich = new List<string>(8);
        private readonly List<string> _linePlain = new List<string>(8);
        private string _rosterRich = string.Empty;
        private int _rosterCount;
        private int _rosterNamesRevision = -1;
        private int _rosterScoresRevision = -1;
        private ushort _rosterLocalActor = ushort.MaxValue;

        /// <summary>
        /// The box: a header naming who is in the match, the history, and the input line.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Anchored to the bottom-left and grown upwards</b>, so the input line sits where the
        /// eye already is and a longer history pushes the top up instead of covering the HUD.
        /// </para>
        /// <para>
        /// <b>Rich text is safe here only because '&lt;' cannot reach it.</b> Names and lines are
        /// sanitized of angle brackets at two ingresses; <see cref="AppendEscaped"/> swaps any that
        /// ever got past both for a look-alike, so the only markup drawn is the colour this method
        /// writes itself.
        /// </para>
        /// </remarks>
        private void OnGUI()
        {
            if (_model == null) return;

            if (_model.IsComposing) HandleBoxKeys(Event.current);

            if (_releaseFocusPending && !_model.IsComposing)
            {
                GUIUtility.keyboardControl = 0;
                _releaseFocusPending = false;
            }

            float opacity = _model.Opacity(Time.unscaledTime);
            if (opacity <= 0f) return;

            float scale = Mathf.Clamp(Screen.height / ReferenceHeight, 0.85f, 2.5f);
            EnsureStyles(scale);

            bool open = _model.IsComposing;
            float margin = 18f * scale;
            float pad = 12f * scale;
            float width = Mathf.Min(620f * scale, Screen.width - 2f * margin);
            float inner = width - 2f * pad;
            float gap = 6f * scale;

            CollectLines(open ? _openLines : _lingerLines, open);

            // Measure first, so the box can be anchored by its bottom edge.
            float titleHeight = 0f, rosterHeight = 0f;
            if (open)
            {
                RefreshRoster();
                EnsureTeamTab(scale);
                titleHeight = _tabStyle.CalcHeight(new GUIContent(TeamTabLabel), inner);
                rosterHeight = _rosterStyle.CalcHeight(new GUIContent(_rosterRich), inner);
            }

            float linesHeight = 0f;
            for (int i = 0; i < _lineRich.Count; i++)
                linesHeight += _lineStyle.CalcHeight(new GUIContent(_lineRich[i]), inner);

            float inputHeight = open ? _inputStyle.fixedHeight : 0f;
            float hintHeight = open ? _hintStyle.CalcHeight(new GUIContent("ENTER"), inner) : 0f;

            float height = pad;
            if (open) height += titleHeight + gap * 0.5f + rosterHeight + gap * 2f + 1f;
            height += linesHeight;
            if (open) height += gap * 1.5f + inputHeight + gap * 0.5f + hintHeight;
            height += pad;

            float x = margin;
            float y = Screen.height - BottomClearance * scale - height;

            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, opacity * (open ? 1f : 0.75f));
            GUI.Box(new Rect(x, y, width, height), GUIContent.none, _panelStyle);
            GUI.color = new Color(1f, 1f, 1f, opacity);

            float cx = x + pad;
            float cy = y + pad;

            if (open)
            {
                DrawChannelTabs(cx, cy, titleHeight, scale);
                GUI.Label(new Rect(cx, cy + 4f * scale, inner, titleHeight),
                    _rosterCount == 1 ? "1 PLAYER" : _rosterCount + " PLAYERS", _metaStyle);
                cy += titleHeight + gap * 0.5f;

                GUI.Label(new Rect(cx, cy, inner, rosterHeight), _rosterRich, _rosterStyle);
                cy += rosterHeight + gap;

                GUI.DrawTexture(new Rect(cx, cy, inner, 1f), _dividerTexture);
                cy += 1f + gap;
            }

            for (int i = 0; i < _lineRich.Count; i++)
            {
                float h = _lineStyle.CalcHeight(new GUIContent(_lineRich[i]), inner);
                float shadow = Mathf.Max(1f, 1.5f * scale);

                GUI.Label(new Rect(cx + shadow, cy + shadow, inner, h), _linePlain[i], _shadowStyle);
                GUI.Label(new Rect(cx, cy, inner, h), _lineRich[i], _lineStyle);
                cy += h;
            }

            if (open)
            {
                cy += gap * 1.5f;

                // The channel the line will go to, in front of it, as a chip in its colour.
                bool team = _model.Channel == ChatChannel.Team;
                GUIContent chip = new GUIContent(team ? TeamTabLabel : AllTabLabel);
                float chipWidth = _chipStyle.CalcSize(chip).x;
                GUI.Label(new Rect(cx, cy, chipWidth, inputHeight), chip, team ? _tabTeamStyle : _tabAllStyle);

                float field = chipWidth + gap;
                GUI.SetNextControlName(DraftControlName);
                _model.Draft = GUI.TextField(
                    new Rect(cx + field, cy, inner - field, inputHeight), _model.Draft ?? string.Empty,
                    ChatTextMessage.MaxTextCharacters, _inputStyle);

                // Once, on the first pass after the box opened. See _focusPending.
                if (_focusPending)
                {
                    GUI.FocusControl(DraftControlName);
                    _focusPending = false;
                }

                cy += inputHeight + gap * 0.5f;
                GUI.Label(new Rect(cx, cy, inner, hintHeight),
                    "ENTER  send     TAB / ARROWS  channel     ESC  close", _hintStyle);
            }

            GUI.color = previous;
        }

        /// <summary>The newest <paramref name="count"/> lines, as drawn text and as its shadow.</summary>
        private void CollectLines(int count, bool open)
        {
            _lineRich.Clear();
            _linePlain.Clear();

            IReadOnlyList<ChatEntry> entries = _model.History;
            int first = Mathf.Max(0, entries.Count - Mathf.Max(1, count));

            if (open && entries.Count == 0)
            {
                _lineRich.Add("<i><color=#9AA3AD>No messages yet. Say hello to your squad.</color></i>");
                _linePlain.Add("<i>No messages yet. Say hello to your squad.</i>");
                return;
            }

            for (int i = first; i < entries.Count; i++)
            {
                ChatEntry entry = entries[i];
                string speaker = NameOf(entry.Speaker);
                bool team = entry.Channel == ChatChannel.Team;
                string speakerHex = ColourHex(TeamOf(entry.Speaker), 0.28f);

                // Every line says where it went: a team line in its side's colour, so a player
                // never mistakes what only their side saw for something the enemy read too.
                _text.Length = 0;
                _text.Append(team ? "<b><color=#" + speakerHex + ">[TEAM]</color></b>  " : "<color=#9AA3AD>[ALL]</color>  ");
                _text.Append("<b><color=#").Append(speakerHex).Append('>');
                AppendEscaped(_text, speaker);
                _text.Append("</color></b>  ");
                AppendEscaped(_text, entry.Text);
                _lineRich.Add(_text.ToString());

                _text.Length = 0;
                _text.Append(team ? "<b>[TEAM]</b>  " : "[ALL]  ");
                _text.Append("<b>");
                AppendEscaped(_text, speaker);
                _text.Append("</b>  ");
                AppendEscaped(_text, entry.Text);
                _linePlain.Add(_text.ToString());
            }
        }

        private const string AllTabLabel = "ALL";
        private const string TeamTabLabel = "TEAM";

        /// <summary>
        /// The ALL and TEAM tabs, the open channel filled in. A click picks a tab; the text field
        /// is handed the keyboard back afterwards, because the click took it.
        /// </summary>
        private void DrawChannelTabs(float x, float y, float height, float scale)
        {
            float gap = 6f * scale;
            float all = _tabStyle.CalcSize(new GUIContent(AllTabLabel)).x;
            float team = _tabStyle.CalcSize(new GUIContent(TeamTabLabel)).x;
            bool onTeam = _model.Channel == ChatChannel.Team;

            if (GUI.Button(new Rect(x, y, all, height), AllTabLabel, onTeam ? _tabStyle : _tabAllStyle)
                && _model.SelectChannel(ChatChannel.All))
                _focusPending = true;

            if (GUI.Button(new Rect(x + all + gap, y, team, height), TeamTabLabel, onTeam ? _tabTeamStyle : _tabStyle)
                && _model.SelectChannel(ChatChannel.Team))
                _focusPending = true;
        }

        /// <summary>
        /// Paints the TEAM tab in this player's side's colour, once per side: a player who moves
        /// to the other side sees their new team's colour on it.
        /// </summary>
        private void EnsureTeamTab(float scale)
        {
            int rgb = NetClientPresenterGuard.TryResolveLocalTeam(out byte team) && team != TeamId.None
                ? NetClientBindings.TeamColourRgb(team)
                : 0x5A8F4E;
            if (rgb == _tabTeamRgb && _tabTeamTexture != null) return;

            _tabTeamRgb = rgb;
            if (_tabTeamTexture != null) Destroy(_tabTeamTexture);
            _tabTeamTexture = RoundedTexture(
                new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 0.92f), 6);

            _tabTeamStyle.normal.background = _tabTeamTexture;
            _tabTeamStyle.hover.background = _tabTeamTexture;
            _tabTeamStyle.active.background = _tabTeamTexture;
        }

        /// <summary>
        /// Rebuilds the "who is here" line when the name or score table has moved.
        /// </summary>
        /// <remarks>
        /// Cached on the two tables' revisions: the roster is drawn every frame the box is open,
        /// and a string per frame for a line that changes a few times a match is waste.
        /// </remarks>
        private void RefreshRoster()
        {
            if (_names == null)
            {
                _rosterRich = "<color=#9AA3AD>Player list unavailable</color>";
                _rosterCount = 0;
                return;
            }

            ushort local = _client != null ? _client.LocalActorId : ushort.MaxValue;
            if (_names.Names.Revision == _rosterNamesRevision
                && _names.Scores.Revision == _rosterScoresRevision
                && local == _rosterLocalActor)
                return;

            _rosterNamesRevision = _names.Names.Revision;
            _rosterScoresRevision = _names.Scores.Revision;
            _rosterLocalActor = local;

            _text.Length = 0;
            _rosterCount = 0;

            for (int id = 0; id < ProtocolConstants.MAX_ACTORS; id++)
            {
                string name = _names.Names.NameOf((ushort)id);
                if (string.IsNullOrEmpty(name)) continue;

                if (_rosterCount > 0) _text.Append("     ");
                _text.Append("<color=#").Append(ColourHex(TeamOf((byte)id), 0.28f)).Append(">● ");
                AppendEscaped(_text, name);
                if (id == local) _text.Append(" <i>(you)</i>");
                _text.Append("</color>");
                _rosterCount++;
            }

            _rosterRich = _rosterCount == 0
                ? "<color=#9AA3AD>Waiting for the player list...</color>"
                : _text.ToString();
        }

        /// <summary>The speaker's name, or their actor id when no name has arrived.</summary>
        private string NameOf(byte actorId)
            => _names != null ? _names.Names.NameOr(actorId, "#" + actorId) : "#" + actorId;

        private byte TeamOf(byte actorId) => _names != null ? _names.Scores.TeamOf(actorId) : TeamId.None;

        /// <summary>
        /// A team's colour as hex, pulled towards white by <paramref name="lift"/> so a dark team
        /// colour stays readable on the dark box.
        /// </summary>
        private static string ColourHex(byte team, float lift)
        {
            if (team == TeamId.None) return "D7DDE3";

            int rgb = NetClientBindings.TeamColourRgb(team);
            int r = Lift((rgb >> 16) & 0xFF, lift);
            int g = Lift((rgb >> 8) & 0xFF, lift);
            int b = Lift(rgb & 0xFF, lift);

            return ((r << 16) | (g << 8) | b).ToString("X6");
        }

        private static int Lift(int channel, float lift)
            => Mathf.Clamp(Mathf.RoundToInt(channel + (255 - channel) * lift), 0, 255);

        /// <summary>
        /// Appends <paramref name="value"/> with every angle bracket swapped for a look-alike, so a
        /// line can never close or open a rich-text tag of its own.
        /// </summary>
        private static void AppendEscaped(StringBuilder into, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '<') into.Append('‹');
                else if (c == '>') into.Append('›');
                else into.Append(c);
            }
        }

        private void EnsureStyles(float scale)
        {
            if (_panelStyle != null && Mathf.Approximately(scale, _stylesScale)) return;
            _stylesScale = scale;

            if (_panelTexture == null)
            {
                _panelTexture = RoundedTexture(new Color(0.06f, 0.07f, 0.09f, 0.78f), 10);
                _inputTexture = RoundedTexture(new Color(1f, 1f, 1f, 0.12f), 6);
                _dividerTexture = SolidTexture(new Color(1f, 1f, 1f, 0.16f));
                _tabTexture = RoundedTexture(new Color(1f, 1f, 1f, 0.06f), 6);
                _tabHoverTexture = RoundedTexture(new Color(1f, 1f, 1f, 0.14f), 6);
                _tabAllTexture = RoundedTexture(new Color(1f, 1f, 1f, 0.26f), 6);
            }

            Font regular = FindFont("Roboto-Medium");
            Font bold = FindFont("Roboto-Bold") ?? regular;

            _panelStyle = new GUIStyle
            {
                normal = { background = _panelTexture },
                border = new RectOffset(10, 10, 10, 10),
            };

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                font = bold,
                fontSize = Mathf.RoundToInt(18f * scale),
                fontStyle = bold != null ? FontStyle.Normal : FontStyle.Bold,
                normal = { textColor = new Color(1f, 1f, 1f, 0.95f) },
                padding = new RectOffset(0, 0, 0, Mathf.RoundToInt(2f * scale)),
                margin = new RectOffset(0, 0, 0, 0),
                richText = false,
            };

            _metaStyle = new GUIStyle(_titleStyle)
            {
                font = regular,
                fontSize = Mathf.RoundToInt(13f * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.UpperRight,
                normal = { textColor = new Color(0.72f, 0.76f, 0.81f, 1f) },
            };

            _rosterStyle = new GUIStyle(GUI.skin.label)
            {
                font = regular,
                fontSize = Mathf.RoundToInt(16f * scale),
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, Mathf.RoundToInt(2f * scale), 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = Color.white },
            };

            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                font = regular,
                fontSize = Mathf.RoundToInt(20f * scale),
                wordWrap = true,
                richText = true,
                padding = new RectOffset(0, 0, Mathf.RoundToInt(2f * scale), Mathf.RoundToInt(2f * scale)),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = new Color(0.94f, 0.95f, 0.96f, 1f) },
            };

            _shadowStyle = new GUIStyle(_lineStyle)
            {
                normal = { textColor = new Color(0f, 0f, 0f, 0.7f) },
            };

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                font = regular,
                fontSize = Mathf.RoundToInt(20f * scale),
                fixedHeight = Mathf.Round(40f * scale),
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(
                    Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(10f * scale), 0, 0),
                border = new RectOffset(6, 6, 6, 6),
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            _inputStyle.normal.background = _inputTexture;
            _inputStyle.focused.background = _inputTexture;
            _inputStyle.hover.background = _inputTexture;
            _inputStyle.active.background = _inputTexture;
            _inputStyle.normal.textColor = Color.white;
            _inputStyle.focused.textColor = Color.white;
            _inputStyle.hover.textColor = Color.white;
            _inputStyle.active.textColor = Color.white;

            _hintStyle = new GUIStyle(_metaStyle)
            {
                fontSize = Mathf.RoundToInt(13f * scale),
                alignment = TextAnchor.UpperRight,
                normal = { textColor = new Color(0.62f, 0.67f, 0.73f, 1f) },
            };

            // A tab: a pill with its label centred. Inactive is a faint outline of a pill that
            // brightens under the pointer; ALL fills white and TEAM fills with the side's colour.
            _tabStyle = new GUIStyle(GUI.skin.button)
            {
                font = bold,
                fontSize = Mathf.RoundToInt(15f * scale),
                fontStyle = bold != null ? FontStyle.Normal : FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(
                    Mathf.RoundToInt(16f * scale), Mathf.RoundToInt(16f * scale),
                    Mathf.RoundToInt(6f * scale), Mathf.RoundToInt(6f * scale)),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(6, 6, 6, 6),
                richText = false,
            };
            SetTabLook(_tabStyle, _tabTexture, _tabHoverTexture, new Color(0.72f, 0.76f, 0.81f, 1f));

            _tabAllStyle = new GUIStyle(_tabStyle);
            SetTabLook(_tabAllStyle, _tabAllTexture, _tabAllTexture, Color.white);

            _tabTeamStyle = new GUIStyle(_tabStyle);
            SetTabLook(_tabTeamStyle, _tabTeamTexture ?? _tabAllTexture, _tabTeamTexture ?? _tabAllTexture, Color.white);

            // The chip in front of the input line is a tab that cannot be clicked.
            _chipStyle = new GUIStyle(_tabStyle);
        }

        private static void SetTabLook(GUIStyle style, Texture2D rest, Texture2D hover, Color ink)
        {
            style.normal.background = rest;
            style.focused.background = rest;
            style.hover.background = hover;
            style.active.background = hover;
            style.normal.textColor = ink;
            style.focused.textColor = ink;
            style.hover.textColor = Color.white;
            style.active.textColor = Color.white;
        }

        /// <summary>
        /// One of the project's Roboto faces when the scene has already loaded it, else null and
        /// IMGUI's default. Looked up rather than referenced: this component has no scene to hold
        /// a reference in.
        /// </summary>
        private static Font FindFont(string name)
        {
            Font[] loaded = Resources.FindObjectsOfTypeAll<Font>();
            for (int i = 0; i < loaded.Length; i++)
                if (loaded[i] != null && loaded[i].name == name) return loaded[i];

            return null;
        }

        private static Texture2D SolidTexture(Color colour)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixel(0, 0, colour);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// A small rounded rectangle for nine-slicing: corners of <paramref name="radius"/> pixels,
        /// anti-aliased by coverage, flat fill in between.
        /// </summary>
        private static Texture2D RoundedTexture(Color colour, int radius)
        {
            int size = radius * 2 + 2;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(radius - (px + 0.5f), px + 0.5f - (size - radius)));
                float dy = Mathf.Max(0f, Mathf.Max(radius - (py + 0.5f), py + 0.5f - (size - radius)));
                float coverage = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));

                texture.SetPixel(px, py, new Color(colour.r, colour.g, colour.b, colour.a * coverage));
            }

            texture.Apply();
            return texture;
        }

        private void DestroyTextures()
        {
            if (_panelTexture != null) Destroy(_panelTexture);
            if (_inputTexture != null) Destroy(_inputTexture);
            if (_dividerTexture != null) Destroy(_dividerTexture);
            if (_tabTexture != null) Destroy(_tabTexture);
            if (_tabHoverTexture != null) Destroy(_tabHoverTexture);
            if (_tabAllTexture != null) Destroy(_tabAllTexture);
            if (_tabTeamTexture != null) Destroy(_tabTeamTexture);
        }
    }
}
