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
        /// <summary>The key that opens the chat line.</summary>
        /// <remarks>
        /// <para>
        /// <b>Not Return, and the reason is a shipped defect.</b> This was <c>KeyCode.Return</c>,
        /// and the remark here used to claim it was "not a gameplay control competing with
        /// anything a player has already bound". That was wrong: <c>ProjectSettings/InputManager.asset</c>
        /// binds the <c>Loadout</c> axis to <c>return</c> with <c>enter</c> as its alternate, and
        /// has since the original import. One press therefore opened the chat line AND toggled
        /// the deploy screen in the same frame, every time.
        /// </para>
        /// <para>
        /// T for open and <see cref="_sendKey"/> for send is the convention every shooter with a
        /// chat box uses, so it costs a player nothing to learn. Enter keeps its original meaning
        /// — deploy — whenever the chat line is closed, which is the state it is in almost always.
        /// </para>
        /// </remarks>
        [Tooltip("Opens the chat line. Not Return: that is the deploy screen's key.")]
        [SerializeField] private KeyCode _openKey = KeyCode.T;

        /// <summary>The key that sends the line, read only while the line is already open.</summary>
        /// <remarks>
        /// Return, because that is what a text field means by Return — and it is safe here in a
        /// way it is not on <see cref="_openKey"/>: while composing, the deploy toggle in
        /// <c>FpsActorController</c> is suppressed by <see cref="LocalTextEntry.Composing"/>, so
        /// the key has exactly one meaning at a time rather than two at once.
        /// </remarks>
        [Tooltip("Sends the line. Only read while the chat line is open.")]
        [SerializeField] private KeyCode _sendKey = KeyCode.Return;

        /// <summary>Abandons the line without sending it.</summary>
        /// <remarks>
        /// A chat box with no way out is the failure being fixed here — a player who opens one
        /// by accident must be able to get their movement keys back without sending a message
        /// or restarting.
        /// </remarks>
        [Tooltip("Closes the chat line and discards the draft.")]
        [SerializeField] private KeyCode _cancelKey = KeyCode.Escape;

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

        private readonly List<ChatLine> _lines = new List<ChatLine>(16);
        private readonly byte[] _body = new byte[ChatTextMessage.MaxClientBodySize];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private string _draft = string.Empty;
        private bool _composing;

        /// <summary>Set on the frame the line opens, consumed by the first <c>OnGUI</c> after it.</summary>
        /// <remarks>
        /// The focus grab has to happen once, not every frame. <c>GUI.FocusControl</c> was being
        /// called unconditionally on every repaint, which re-seats IMGUI's text-editing state
        /// continuously and takes the caret with it — so a player could open the box and then
        /// find their typing landing unpredictably.
        /// </remarks>
        private bool _focusPending;

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
        /// <b>This used to have no consumer, and could not have had one.</b> The remark here
        /// said so outright — "nothing consumes it yet" — and the reason it stayed that way is
        /// structural: this type lives in <c>Ironfront.Net.Unity.Client</c>, whose asmdef sets
        /// <c>autoReferenced: false</c>, so neither <c>Assembly-CSharp</c> nor the input
        /// assemblies can name it. The flag that the input path actually reads is
        /// <see cref="LocalTextEntry.Composing"/>, in the one assembly all of them can see;
        /// this property is now just the local half of the same state, kept for callers that
        /// already hold the component.
        /// </remarks>
        public bool IsComposing => _composing;

        /// <summary>
        /// Moves the composing state, and publishes it where the input path can read it.
        /// </summary>
        /// <remarks>
        /// Every write to <see cref="_composing"/> goes through here. That is the point: the
        /// two flags drifting apart would leave the player's movement keys suppressed with no
        /// chat box on screen, which is worse than the defect this replaces.
        /// </remarks>
        private void SetComposing(bool composing)
        {
            _composing = composing;
            LocalTextEntry.Composing = composing;
        }

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
            // eating the player's movement keys with no server to send to. Through SetComposing
            // so the published flag is cleared too -- this component going away with
            // LocalTextEntry.Composing left true is a player who can never move again.
            SetComposing(false);
            _draft        = string.Empty;
            _focusPending = false;
        }

        /// <summary>
        /// Opens on <see cref="_openKey"/>, sends on <see cref="_sendKey"/>, abandons on
        /// <see cref="_cancelKey"/>.
        /// </summary>
        /// <remarks>
        /// <b>The open key is read only while closed, and the send key only while open.</b> One
        /// key doing both is what the previous shape did, and it is why moving the open key to a
        /// letter would not have been enough on its own: with a single key, every character of
        /// the draft that happened to be that letter would have sent the line mid-word.
        /// </remarks>
        private void Update()
        {
            if (_client == null || !_client.IsConnected)
            {
                // Not SetComposing-guarded on a state check: this runs every frame while
                // disconnected, and publishing false repeatedly is free and idempotent.
                if (_composing) SetComposing(false);
                return;
            }

            ExpireLines();

            if (!_composing)
            {
                if (!Input.GetKeyDown(_openKey)) return;
                _draft = string.Empty;
                _focusPending = true;
                SetComposing(true);
                return;
            }

            if (Input.GetKeyDown(_cancelKey))
            {
                _draft = string.Empty;
                SetComposing(false);
                return;
            }

            if (!Input.GetKeyDown(_sendKey)) return;

            Send(_draft);
            _draft = string.Empty;
            SetComposing(false);
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

            _lines.Add(new ChatLine(NameOf(actorId), text, Time.time + _lineLifetimeSeconds));

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

                // Once, on the first OnGUI pass after the line opened. See _focusPending.
                //
                // Deliberately NOT gated on the repaint event: naming EventType here trips
                // tools/check-net-layering.ps1 rule 6b, which matches predefined-assembly type
                // names by NAME and cannot tell UnityEngine.EventType from the EventType
                // Assembly-CSharp declares. GUI.FocusControl takes on any event, so the gate
                // costs nothing here -- and a "not-a-reference" baseline row would be a second
                // thing to re-check forever in exchange for a line that was never needed.
                if (_focusPending)
                {
                    GUI.FocusControl(DraftControlName);
                    _focusPending = false;
                }
            }

            GUILayout.EndArea();
        }

        private const string DraftControlName = "ironfront.chat.draft";

        /// <summary>One line on screen, with the moment it stops being shown.</summary>
        /// <remarks>
        /// <b>Not <c>Line</c>.</b> Assembly-CSharp declares <c>Pathfinding.RVO.Line</c>, and the
        /// layering gate matches predefined-assembly type names by name — it cannot see that a
        /// private nested struct in this file is not a reference to that one, and
        /// <c>Ironfront.Net.Unity.Client</c> could not reference Assembly-CSharp even if it
        /// wanted to. Renaming removes the ambiguity outright, which is better than adding a
        /// "not-a-reference" baseline row that a future reader would have to re-check.
        /// </remarks>
        private readonly struct ChatLine
        {
            public readonly string Speaker;
            public readonly string Text;
            public readonly float ExpiresAt;

            public ChatLine(string speaker, string text, float expiresAt)
            {
                Speaker   = speaker;
                Text      = text;
                ExpiresAt = expiresAt;
            }
        }
    }
}
