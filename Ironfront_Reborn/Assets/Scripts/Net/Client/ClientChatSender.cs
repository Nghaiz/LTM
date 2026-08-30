using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The one production sender of <c>C_CHAT</c>, and the thing that draws what comes back.
    /// Phase P6 task 3.3, ledger X-8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing sent this opcode and nothing routed it.</b> <c>ClientWiringGate</c>'s G10 held
    /// a named gap for it whose retire condition was explicit: "when Chat gets a handler AND a
    /// sender, not before", because a sender alone would have shipped a write-only path the
    /// server counted in <c>UnknownMessages</c> — its corruption counter. The route landed
    /// first (<c>ServerMessageRouter</c> → <see cref="Ironfront.Net.Replication.Server.IChatHandler"/>
    /// → <c>ServerTickLoop</c>), then this.
    /// </para>
    /// <para>
    /// <b>Sender and presenter in one component, deliberately.</b> They are two halves of one
    /// conversation and they share the buffer of recent lines: a player needs to see their own
    /// message land to know it was sent at all. Splitting them would also mean two components to
    /// author or to add, and the second one missing is a chat box that swallows everything.
    /// </para>
    /// <para>
    /// <b>Lobby chat as M3 names it, and nothing more.</b> No history beyond what is on screen,
    /// no channels, no moderation beyond the sanitizing every ingress already does. Each of
    /// those is a feature with its own decisions; none of them is needed for two players to talk
    /// to each other.
    /// </para>
    /// <para>
    /// <b>Drawn from <c>OnGUI</c>, like <c>LobbyShellOverlay</c>.</b> This phase owns no scenes
    /// or prefabs, so a canvas-based box would have to be authored somewhere to exist — and a
    /// chat box present in one scene and missing from the next reads as the server dropping
    /// messages. An immediate-mode overlay has no scene to be missing from. It is the plainest
    /// possible surface and is meant to be replaced by a real one.
    /// </para>
    /// <para>
    /// At execution order -40, alongside <see cref="ClientSeatRequester"/>: it needs the router
    /// and nothing else, so it only has to be later than the bootstrap that owns it.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-40)]
    [DisallowMultipleComponent]
    public sealed class ClientChatSender : MonoBehaviour
    {
        /// <summary>The key that opens the chat line, and sends it once open.</summary>
        /// <remarks>
        /// Return rather than a rebindable input-manager button, unlike
        /// <c>ClientSeatRequester</c>'s Use key: this is not a gameplay control competing with
        /// anything a player has already bound, and the input manager has no Return axis
        /// authored to borrow.
        /// </remarks>
        [Tooltip("Opens the chat line, and sends it when the line is already open.")]
        [SerializeField] private KeyCode _openKey = KeyCode.Return;

        /// <summary>How many recent lines stay on screen.</summary>
        [Tooltip("Lines kept on screen. Older ones fall off the top.")]
        [SerializeField] private int _visibleLines = 6;

        /// <summary>Seconds a line stays up after it arrives.</summary>
        /// <remarks>
        /// Chat that never fades covers the game; chat that fades too fast is missed by anyone
        /// who was aiming at the time. Ten seconds is long enough to read a line you were not
        /// looking for.
        /// </remarks>
        [Tooltip("Seconds a line stays on screen after it arrives.")]
        [SerializeField] private float _lineLifetimeSeconds = 10f;

        private NetClientBootstrap _client;
        private NetClientCombatPresenter _names;

        private readonly List<Line> _lines = new List<Line>(16);
        private readonly byte[] _body = new byte[ChatTextMessage.MaxClientBodySize];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private string _draft = string.Empty;
        private bool _composing;

        /// <summary><c>C_CHAT</c> messages sent. Zero after typing is the tell.</summary>
        public long MessagesSent { get; private set; }

        /// <summary>Lines that arrived from the server, including this client's own.</summary>
        public long MessagesReceived { get; private set; }

        /// <summary>
        /// Drafts refused because nothing survived sanitizing, or because the encoded line did
        /// not fit the wire bound.
        /// </summary>
        /// <remarks>
        /// Surfaced rather than logged so "I pressed Return and nothing happened" has an answer
        /// somewhere. It rises on a line of pure markup or whitespace, which is a player typing
        /// something the wire will not carry rather than a fault.
        /// </remarks>
        public long DraftsRefused { get; private set; }

        /// <summary>True while the chat line has focus and gameplay keys should be ignored.</summary>
        /// <remarks>
        /// Public because whoever owns input has to be able to ask. Nothing consumes it yet —
        /// the overlay draws its own text field and Unity's IMGUI keeps focus itself — so this
        /// is the handle for the moment a real HUD replaces the overlay.
        /// </remarks>
        public bool IsComposing => _composing;

        private void Awake()
        {
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

            // Optional. It owns the actor-id-to-name table built from S_PLAYER_LIST; without it
            // a line is attributed by actor id, which is worse than a name and better than
            // nothing. Chat must not depend on the combat presenter existing.
            _names = GetComponent<NetClientCombatPresenter>();
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnChat += OnChat;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnChat -= OnChat;

            // A disconnect mid-compose would otherwise leave the line open across a reconnect,
            // eating the player's movement keys with no server to send to.
            _composing = false;
            _draft     = string.Empty;
        }

        private void Update()
        {
            if (_client == null || !_client.IsConnected)
            {
                _composing = false;
                return;
            }

            ExpireLines();

            if (!Input.GetKeyDown(_openKey)) return;

            if (!_composing)
            {
                _composing = true;
                _draft     = string.Empty;
                return;
            }

            Send(_draft);
            _composing = false;
            _draft     = string.Empty;
        }

        /// <summary>
        /// Frames one line and puts it on the reliable channel.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Reliable, on channel 2</b>, for <c>C_SEAT_REQUEST</c>'s reason: a dropped line is
        /// a player who said something nobody heard, with nothing to re-send it and no next
        /// frame carrying the same intent.
        /// </para>
        /// <para>
        /// <b>Sanitized and clipped before encoding.</b> The clip is in CHARACTERS
        /// (<see cref="ChatTextMessage.MaxTextCharacters"/>) because that is where a boundary can be
        /// found without splitting a multi-byte code point; the wire bound is in bytes and
        /// <see cref="ChatTextMessage.Encode"/> refuses rather than truncating when the two
        /// disagree, which is a line of Vietnamese long enough to exceed 120 bytes inside 60
        /// characters. That refusal is counted, not silent.
        /// </para>
        /// <para>
        /// <b>Nothing is echoed locally.</b> The line appears when — and only when — the server
        /// broadcasts it back, which is what makes what a player sees the same thing everybody
        /// else sees. A local echo would show a message that was refused or dropped as though it
        /// had been delivered.
        /// </para>
        /// </remarks>
        private void Send(string draft)
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

            int bodyLength = ChatTextMessage.WriteClient(_body, encoded.Slice(0, textLength));
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
        /// A line arrived. Already sanitized by the router at this client's own ingress.
        /// </summary>
        /// <remarks>
        /// <b>No <c>IsLocalActor</c> guard, and that is not an omission.</b> Every other
        /// per-actor handler in this folder guards, because it writes the local player's camera,
        /// health or rig from an event that may name a remote actor. This one writes a shared
        /// message list: a line from a remote player is exactly what chat is for, and guarding
        /// here would show a player only their own messages.
        /// </remarks>
        private void OnChat(byte actorId, string text)
        {
            MessagesReceived++;

            _lines.Add(new Line(NameOf(actorId), text, Time.time + _lineLifetimeSeconds));

            // Bounded from the front, so a busy match cannot grow this list for the length of
            // the round. The lifetime usually gets there first; this is what covers the case
            // where it does not.
            while (_lines.Count > _visibleLines) _lines.RemoveAt(0);
        }

        /// <summary>The speaker's name, or their actor id when no name has arrived.</summary>
        /// <remarks>
        /// The fallback is here rather than in <c>PlayerNameTable</c>, which returns null, for
        /// the reason <c>NetClientCombatPresenter</c> gives: only the consumer knows what a
        /// missing name should read as, and here it is an id rather than a blank.
        /// </remarks>
        private string NameOf(byte actorId)
            => _names != null ? _names.Names.NameOr(actorId, "#" + actorId) : "#" + actorId;

        private void ExpireLines()
        {
            // From the front only: the list is append-ordered by arrival, so every expired line
            // is a prefix of it and a full scan would cost the same answer.
            while (_lines.Count > 0 && Time.time >= _lines[0].ExpiresAt) _lines.RemoveAt(0);
        }

        /// <summary>
        /// The plainest possible chat surface. Meant to be replaced by a real HUD.
        /// </summary>
        /// <remarks>
        /// <b>Rich text is off on the label.</b> The text has been sanitized of angle brackets
        /// at two ingresses already, and turning it off here as well costs one line and means a
        /// third way in would still render as characters rather than as markup.
        /// </remarks>
        private void OnGUI()
        {
            if (_lines.Count == 0 && !_composing) return;

            var style = new GUIStyle(GUI.skin.label) { richText = false };

            GUILayout.BeginArea(new Rect(12f, Screen.height - 190f, 520f, 178f));

            for (int i = 0; i < _lines.Count; i++)
                GUILayout.Label($"{_lines[i].Speaker}: {_lines[i].Text}", style);

            if (_composing)
            {
                GUI.SetNextControlName(DraftControlName);
                _draft = GUILayout.TextField(_draft, ChatTextMessage.MaxTextCharacters);
                GUI.FocusControl(DraftControlName);
            }

            GUILayout.EndArea();
        }

        private const string DraftControlName = "ironfront.chat.draft";

        /// <summary>One line on screen, with the moment it stops being shown.</summary>
        private readonly struct Line
        {
            public readonly string Speaker;
            public readonly string Text;
            public readonly float ExpiresAt;

            public Line(string speaker, string text, float expiresAt)
            {
                Speaker   = speaker;
                Text      = text;
                ExpiresAt = expiresAt;
            }
        }
    }
}
