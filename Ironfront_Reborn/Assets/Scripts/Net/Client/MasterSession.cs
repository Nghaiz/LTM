#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Drives the online flow: log in, list rooms, join one, and hand the resulting ticket to
    /// the UDP transport. phase-03 tasks 2 and 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by the lead's assist track
    /// (plans/unity-client/study/step-06-master-connection.md).
    /// </para>
    /// <para>
    /// <b>This is the half of the online flow that did not exist.</b> The server side has been
    /// done and tested for a while — <c>MspMessageDispatcher</c> answers a room join with the
    /// game server's address, port and a signed ticket. On the client, <c>MasterClient</c> was
    /// referenced only by <i>server</i> components, and <c>NetClientBootstrap</c> dialled a host
    /// from the inspector with an <b>empty</b> ticket. Until this class, "connect to the VPS and
    /// play online" was not something the client could do, however complete the server was.
    /// </para>
    /// <para>
    /// <b>No thread marshaller, and the reason is not optimism.</b> phase-03 trap 1 says to
    /// settle with the master-server track whether <see cref="IMasterClient"/> callbacks arrive on the main
    /// thread, and to keep a <c>ConcurrentQueue</c> ready in case they do not. Reading the code
    /// settles it: the client is poll-driven. <c>MasterClient</c> queues every response and
    /// every push internally and runs them from <c>Poll()</c>, so they fire on whichever thread
    /// called it — and <see cref="Tick"/> calls it. <c>MasterClientPollTests</c> is the
    /// executable statement of that contract. A second marshaller here would be a queue drained
    /// by a queue, so there is none (coding-guidelines.md § 2).
    /// </para>
    /// <para>
    /// <b>It is a plain class.</b> Everything here is a decision, and decisions are testable by
    /// <c>dotnet test</c>; a <c>MonoBehaviour</c> would put all of it out of reach of every test
    /// project in the solution. Unity supplies the clock through <see cref="Tick"/> and the
    /// scene through <see cref="OnSceneReady"/>, and draws nothing here.
    /// </para>
    /// </remarks>
    public sealed class MasterSession
    {
        /// <summary>
        /// Seconds to wait for the game server before giving up. phase-03 task 3.
        /// </summary>
        /// <remarks>
        /// The join ticket is valid for 60 seconds, so this is well inside it on purpose: a
        /// client that waited out the ticket would fail the retry too, for a different reason
        /// and with a worse error.
        /// </remarks>
        public const float DefaultConnectTimeoutSeconds = 10f;

        private readonly IMasterClient _master;
        private readonly GameFlowController _flow;
        private readonly ITransportClient _game;
        private readonly GamePayloadRoute _route;

        private float _connectDeadline;
        private bool _connecting;

        /// <summary>Set while our own <c>Disconnect()</c> is on the stack. See <see cref="LeaveMatch"/>.</summary>
        private bool _leaving;

        /// <summary>Whether the junction in flight came through the master, or was a direct dial.</summary>
        private bool _junctionDrivesFlow;

        /// <summary>
        /// A ticket fetch for the starting match is in flight, or the dial has begun. Reset on
        /// every path that leaves the room or the match, so a second match in one session is not
        /// locked out by the first.
        /// </summary>
        private bool _enteringMatch;

        /// <summary>
        /// The most recent room push this session could not yet identify as its own. P16 3.4.
        /// </summary>
        /// <remarks>
        /// Volatile because it is WRITTEN from whichever thread called <c>Poll</c> and READ from
        /// the continuation of a create or join, which <c>ConfigureAwait(false)</c> puts on the
        /// thread pool. That split is the whole reason this field exists.
        /// </remarks>
        private volatile RoomState? _unclaimedRoom;

        public MasterSession(
            IMasterClient master,
            GameFlowController flow,
            ITransportClient game,
            GamePayloadRoute route)
        {
            _master = master ?? throw new ArgumentNullException(nameof(master));
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _route = route ?? throw new ArgumentNullException(nameof(route));

            _game.OnConnected += OnGameConnected;
            _game.OnDisconnected += OnGameDisconnected;
            _master.OnRoomStatePush += OnRoomStatePushed;
            _master.OnChat += OnChatPushed;
            _master.OnError += OnErrorPushed;
        }

        /// <summary>
        /// An unsolicited <c>ErrorPush</c> — a refusal of something sent fire-and-forget. P16 3.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nothing was subscribed to this before P16, and that was a hole rather than a
        /// choice.</b> <c>MasterClient</c> raises <c>OnError</c> for every <c>ErrorPush</c> and
        /// additionally faults the pending request, so a refusal of a REQUEST always surfaced as
        /// a <see cref="MasterServerException"/> at its caller. Ready, leave and now team are
        /// sent with no response opcode, so their refusals arrive here and nowhere else — and
        /// with no subscriber they were dropped on the floor. P16 3.5 requires the team refusals
        /// to reach the screen, and this is the path they take.
        /// </para>
        /// <para>
        /// <b>Phrased from the code, not from the server's message.</b>
        /// <see cref="MasterErrorText"/> is the client's one place for player-facing wording; the
        /// master's own string is an operator's sentence ("Cannot change team.") and says less
        /// than the code does.
        /// </para>
        /// </remarks>
        private void OnErrorPushed(int code, string message) => Fail(MasterErrorText.DescribeFailure(code));

        private void OnChatPushed(ChatMessage message)
        {
            if (message != null) OnChat?.Invoke(message);
        }

        /// <summary>
        /// The master says our room's match has begun, so dial the game server. X-77.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The only automatic edge out of <c>RoomLobby</c>.</b> The master's half of this
        /// worked from the day it was written -- <c>MspMessageDispatcher.HandleMatchStarted</c>
        /// sets <c>InMatch</c> and broadcasts the room -- but the event was declared, raised,
        /// and subscribed by nothing outside a test fake. So the room lobby's one exit was the
        /// shell's "Enter match now (debug)" button: a human pressing a key the flow should not
        /// need, and one of M3's two remaining manual interventions.
        /// </para>
        /// <para>
        /// <b>Three guards, each for a push that really arrives.</b> The master BROADCASTS room
        /// state, so a push about a room we are not in is ordinary and must be ignored. The
        /// push also repeats on every member change and on a retransmit, so it must be
        /// idempotent -- reaching <c>Transition</c> twice would throw
        /// <c>IllegalGameFlowTransitionException</c> out of a network callback. And an
        /// unrecognised state byte from a newer master is not an edge; <c>Lifecycle</c> returns
        /// the raw value rather than throwing precisely so this reads as "not one I act on".
        /// </para>
        /// <para>
        /// <c>Starting</c> counts as well as <c>InMatch</c>: the room is calling its members in,
        /// and waiting for the second edge would put every client a broadcast behind the match
        /// it is joining.
        /// </para>
        /// </remarks>
        private void OnRoomStatePushed(RoomState room)
        {
            if (room == null) return;

            if (room.RoomId != JoinedRoomId || JoinedRoomId == 0)
            {
                // HELD, not dropped -- and only until the create or join in flight decides
                // whether it was ours. See ClaimRoomState for why a push about our own room
                // routinely arrives before we know its id.
                _unclaimedRoom = room;
                return;
            }

            // Re-surfaced for P16 3.1: the room lobby screen renders the roster, the ready
            // marks and the lifecycle straight off this. It reads the push rather than polling,
            // and it reads it THROUGH this class rather than adding a second subscriber to
            // _master -- two subscribers would see the same push in an unspecified order, and
            // the screen would sometimes draw a roster for a room this session had already
            // decided was not ours.
            Room = room;
            OnRoomState?.Invoke(room);

            if (_flow.State != GameFlowState.RoomLobby) return;

            if (room.Lifecycle != RoomLifecycleState.Starting
                && room.Lifecycle != RoomLifecycleState.InMatch) return;

            // The push repeats -- on every member change, and on a retransmit -- and the flow
            // stays RoomLobby across the await below, so the state check above stops guarding
            // once the ticket fetch is asynchronous. Without this flag a second push would
            // start a second fetch and a second EnterMatch, and the second reaches
            // Transition(ConnectingGame) from ConnectingGame: an exception out of a network
            // callback.
            if (_enteringMatch) return;
            _enteringMatch = true;

            _ = EnterMatchWithFreshTicketAsync();
        }

        /// <summary>
        /// Adopts a push that arrived before this session knew which room it was entering.
        /// P16 3.4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The master's push about our own room routinely beats the answer that names it.</b>
        /// Creating or joining changes the roster, so the master broadcasts — and that broadcast
        /// is on the wire before the response carrying the room id. Both frames are then drained
        /// by ONE <c>Poll</c>, while the response's continuation runs on the thread pool
        /// (<c>ConfigureAwait(false)</c>) and has not set <see cref="JoinedRoomId"/> yet. So the
        /// guard in <see cref="OnRoomStatePushed"/> reads zero and discards the one push that
        /// was about us.
        /// </para>
        /// <para>
        /// <b>The symptom is a player alone in a room they just made, looking at two empty
        /// roster columns</b> under "Waiting for the room...". It self-heals the moment anybody
        /// else changes anything, which is exactly how it survives a two-machine run and ships.
        /// Found by the P16 runtime smoke; no test had a reason to look.
        /// </para>
        /// <para>
        /// <b>The guard is not softened, and a second server push does not fix this.</b> Making
        /// the master send the state again after the response was tried and MEASURED not to
        /// work: the extra frame is drained by the same <c>Poll</c> and loses the same race. The
        /// answer is to keep the push until the id is known, then decide — which is what this
        /// does. Nothing is rendered from a held push whose id does not match.
        /// </para>
        /// <para>
        /// <b>It can only claim a push from the round trip that is asking.</b> Both callers clear
        /// the held push before they send, so a state overheard minutes ago — about a room this
        /// client later happens to enter — cannot be adopted as current.
        /// </para>
        /// </remarks>
        private void ClaimRoomState()
        {
            RoomState? held = _unclaimedRoom;
            _unclaimedRoom = null;

            if (held == null || JoinedRoomId == 0 || held.RoomId != JoinedRoomId) return;

            Room = held;
            OnRoomState?.Invoke(held);
        }

        /// <summary>
        /// Fetches a ticket for the room we are already in, then dials the game server. P16 3.4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Always re-fetched, never reused, and both reasons are P16's.</b> A room's CREATOR
        /// holds no ticket at all — <c>RoomCreate</c> adds them to the roster and allocates no
        /// game server, because none exists until somebody joins — so without this the player
        /// who made the room could not enter it. And a ticket carries the member's TEAM as the
        /// roster held it when the ticket was minted, so a player who used P16's side-switch
        /// control would arrive at the game server on their old side: the two rosters in
        /// criterion 3 would agree with each other and disagree with the match.
        /// </para>
        /// <para>
        /// <b>The master answers a member's re-request with a fresh ticket</b> rather than
        /// <c>AlreadyInAnotherRoom</c> — see <c>MspMessageDispatcher.JoinRoom</c>, which is where
        /// that arm and its reasoning live. Nothing here special-cases the creator; there is one
        /// path and everybody takes it.
        /// </para>
        /// <para>
        /// <b>A failure lands back in the room lobby, not in a dial.</b> <c>EnterMatch</c> would
        /// otherwise be called with a stale or empty <see cref="PendingJoin"/> and fail ten
        /// seconds later as a UDP timeout, blaming the game server for a master-side refusal.
        /// </para>
        /// </remarks>
        private async Task EnterMatchWithFreshTicketAsync()
        {
            try
            {
                int roomId = JoinedRoomId;
                JoinResult result = await _master.JoinRoomAsync(roomId, null).ConfigureAwait(false);

                if (!result.Ok)
                {
                    Fail(MasterErrorText.DescribeFailure(result.ErrorCode));
                    _enteringMatch = false;
                    return;
                }

                var join = new PendingJoin(result.GameServerIp, result.GameServerPort, result.JoinTicket);
                if (!join.IsValid)
                {
                    Fail("The master server did not name a game server for that room.");
                    _enteringMatch = false;
                    return;
                }

                PendingJoin = join;
                EnterMatch();
            }
            catch (Exception ex) when (ex is MasterServerException || IsLinkFailure(ex))
            {
                Fail(ex is MasterServerException master
                    ? MasterErrorText.DescribeFailure(master.ErrorCode)
                    : "Lost the connection to the master server.");
                _enteringMatch = false;
            }
        }

        /// <summary>Seconds to wait for the game server. Lower it for a LAN, never past 60.</summary>
        public float ConnectTimeoutSeconds { get; set; } = DefaultConnectTimeoutSeconds;

        /// <summary>The master's session token, or empty before a successful login.</summary>
        public string SessionToken { get; private set; } = string.Empty;

        /// <summary>The account's id, or 0 before a successful login.</summary>
        public int PlayerId { get; private set; }

        /// <summary>The account's display name, or empty.</summary>
        public string DisplayName { get; private set; } = string.Empty;

        /// <summary>Whether a login has succeeded on this connection.</summary>
        public bool IsLoggedIn => SessionToken.Length > 0;

        /// <summary>
        /// Whether the TCP link to the master is up.
        /// </summary>
        /// <remarks>
        /// Asked of the client rather than remembered, so a caller cannot hold a stale "yes"
        /// across a dropped link and then skip reconnecting forever.
        /// </remarks>
        public bool IsMasterConnected => _master.State == MasterConnectionState.Connected;

        /// <summary>The newest room list. Empty until <see cref="OpenRoomBrowserAsync"/> runs.</summary>
        public RoomInfo[] Rooms { get; private set; } = Array.Empty<RoomInfo>();

        /// <summary>The address and ticket from the last successful join.</summary>
        public PendingJoin PendingJoin { get; private set; } = PendingJoin.None;

        /// <summary>
        /// The map the joined room is being played on, or 0 when nothing named one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The client cannot load the right scene without it.</b> <c>JOIN</c> answers with an
        /// address, a port and a ticket — the three things needed to reach the server, and none
        /// of the one thing needed to render what it is simulating. The id is on
        /// <c>RoomInfo</c>, which the browser already holds, so it is taken from the row the
        /// player pressed Join on rather than added to the wire.
        /// </para>
        /// <para>
        /// <b>Zero means "nobody said", and is not a map.</b> A direct dial never passes through
        /// a room, and a room list fetched before this build knew about map ids carries an
        /// unset ushort. Both leave it 0 so the caller can say which map it fell back to instead
        /// of loading one silently — see <c>MapCatalog.SceneOrDefault</c>.
        /// </para>
        /// </remarks>
        public ushort JoinedMapId { get; private set; }

        /// <summary>
        /// The room this client is in, or 0. Set beside <see cref="JoinedMapId"/> and cleared
        /// with it, for the same reason: a failed join must not leave either pointing at a room
        /// this client is not in.
        /// </summary>
        /// <remarks>
        /// Needed because the master BROADCASTS room state (X-77). Without an id to compare
        /// against, a push about somebody else's room would drag this client into a match it
        /// never joined.
        /// </remarks>
        public int JoinedRoomId { get; private set; }

        /// <summary>The last failure, already phrased for a player. Empty when nothing failed.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>Buffers inbound payloads while the match scene loads. phase-03 trap 3.</summary>
        public SnapshotHoldingQueue Inbound { get; } = new SnapshotHoldingQueue();

        /// <summary>
        /// The game server accepted. The caller starts loading the map and calls
        /// <see cref="OnSceneReady"/> when it is up.
        /// </summary>
        public event Action<ConnectResult>? OnGameServerConnected;

        /// <summary>The junction failed — refused, dropped, or timed out. Carries the reason text.</summary>
        public event Action<string>? OnGameServerFailed;

        /// <summary><see cref="LastError"/> changed. Drives the error line on the login screen.</summary>
        public event Action<string>? OnError;

        /// <summary>
        /// The master pushed the state of the room this client is in. P16 3.1.
        /// </summary>
        /// <remarks>
        /// Raised only for OUR room — the master broadcasts, and a push about somebody else's
        /// room is ordinary traffic that no screen should draw. Every P16 room-lobby element is
        /// driven from this: the two roster columns, the ready marks, the host controls and the
        /// countdown. There is no polling anywhere in that screen, which is what stops two
        /// clients rendering different rosters.
        /// </remarks>
        public event Action<RoomState>? OnRoomState;

        /// <summary>A lobby chat line arrived. P16 3.4.</summary>
        public event Action<ChatMessage>? OnChat;

        /// <summary>
        /// The last room state the master pushed for our room, or null. P16 3.4.
        /// </summary>
        /// <remarks>
        /// Held as well as raised so a screen that becomes visible between pushes has something
        /// to draw. A room lobby that rendered nothing until the next member change would look
        /// broken for as long as nobody moved.
        /// </remarks>
        public RoomState? Room { get; private set; }

        /// <summary>
        /// This client's round trip to the MASTER on the last room-list refresh, in
        /// milliseconds, or -1 before the first one. P16 3.2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It is not a per-room ping, and the browser must not label it as one</b> (owner
        /// decision, 2026-09-02). A room has no game server until somebody joins it —
        /// <c>MspMessageDispatcher</c> allocates one on the FIRST join — so there is no host to
        /// ping for an empty room, and measuring one would mean allocating a server for every
        /// listed room merely to be looked at.
        /// </para>
        /// <para>
        /// What this measures is the request the browser already makes, so it costs nothing, and
        /// it is the latency the player can actually observe before committing to anything.
        /// </para>
        /// </remarks>
        public int MasterPingMs { get; private set; } = -1;

        // ------------------------------------------------------------------ master server

        /// <summary>Opens the TCP link to the master. Does not log in.</summary>
        /// <remarks>
        /// <paramref name="tls"/> null is the plaintext LAN path; a populated policy is what a
        /// production client needs against a master that presents a certificate. The password
        /// is hashed before it leaves the machine either way, but a public deployment carries a
        /// session token as well, so the link itself must be encrypted there.
        /// </remarks>
        public async Task<bool> ConnectAsync(string host, int port, MasterClientTlsOptions? tls = null)
        {
            try
            {
                await _master.ConnectAsync(host, port, tls).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail($"Could not reach the master server at {host}:{port}.");
                return false;
            }
        }

        /// <summary>
        /// Logs in, hashing the password before it leaves the machine. phase-03 traps 1 and 2.
        /// </summary>
        /// <remarks>
        /// Drives <c>LoginScreen -> Authenticating -> Lobby</c>, or back to
        /// <c>LoginScreen</c> with <see cref="LastError"/> set. The plaintext password is never
        /// sent, TLS or not, and is never stored on this object.
        /// </remarks>
        public async Task<bool> LoginAsync(string username, string password)
        {
            _flow.Transition(GameFlowState.Authenticating);

            try
            {
                string hash = PasswordHasher.Hash(password, username);
                LoginResult result = await _master.LoginAsync(username, hash).ConfigureAwait(false);

                if (!result.Ok)
                {
                    // The master's own wait, not an adjective: a lockout is fifteen minutes and
                    // the login budget's window is sixty seconds, and the screen used to promise
                    // "a few seconds" for both.
                    Fail(MasterErrorText.DescribeFailure(result.ErrorCode, result.RetryAfterSeconds));
                    Recover(GameFlowState.LoginScreen);
                    return false;
                }

                SessionToken = result.SessionToken ?? string.Empty;
                PlayerId = result.PlayerId;
                DisplayName = result.DisplayName ?? string.Empty;
                LastError = string.Empty;

                _flow.Transition(GameFlowState.Lobby);
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.DescribeFailure(ex.ErrorCode));
                Recover(GameFlowState.LoginScreen);
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                Recover(GameFlowState.LoginScreen);
                return false;
            }
        }

        /// <summary>
        /// Creates an account, hashing the password with the same function login uses. P15 3.1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The wire half has been there all along.</b>
        /// <c>IMasterClient.RegisterAsync</c> is declared, implemented over
        /// <c>MspMessageType.RegisterRequest</c>, and tested server-side; what did not exist was
        /// a caller. Until this wrapper the only way to create an account was a harness —
        /// <c>run-e2e.ps1</c> composes <c>IMasterClient</c> itself, and the room-creation leg of
        /// it has to open a SECOND account for exactly this reason.
        /// </para>
        /// <para>
        /// <b>It does not move the flow, and that is the answer to 3.1's question.</b> A
        /// successful register leaves the client on <c>LoginScreen</c> with the username it just
        /// claimed, and the player logs in with it. The alternative — registering and logging in
        /// in one step — buys one click and costs a state transition that has to be right in
        /// both the success and the half-success case (account created, login refused), and
        /// there is no edge in the table for "already Authenticating when the register answers".
        /// Coming back to the login screen also tells the player something the auto-login cannot:
        /// that the account now exists and those are the credentials for it.
        /// </para>
        /// <para>
        /// <b>The hash is <see cref="PasswordHasher.Hash"/>, the same call
        /// <see cref="LoginAsync"/> makes</b>, salted with the same username. Two hashing paths
        /// is how an account gets created that cannot log in — a wrong-password error with the
        /// correct password, which is close to undebuggable from the player's side. Criterion 4
        /// grades this end to end rather than as two separate tests, so a divergence here fails
        /// as "the account I just made will not let me in".
        /// </para>
        /// <para>
        /// <see cref="IsLoggedIn"/> is <b>unchanged</b> by this call, success or failure: it
        /// reads <see cref="SessionToken"/>, and a register answer carries no token. That is
        /// stated rather than left to be inferred because it is the post-condition a caller is
        /// most likely to assume the other way.
        /// </para>
        /// </remarks>
        /// <param name="displayName">
        /// Shown to other players. Blank means "use the username", which the master applies —
        /// the client does not substitute it here, because then a master that decided otherwise
        /// and this client would disagree about the player's own name.
        /// </param>
        public async Task<bool> RegisterAsync(string username, string password, string? displayName = null)
        {
            try
            {
                string hash = PasswordHasher.Hash(password, username);

                RegisterResult result = await _master
                    .RegisterAsync(username, hash, displayName ?? string.Empty)
                    .ConfigureAwait(false);

                if (!result.Ok)
                {
                    Fail(MasterErrorText.DescribeFailure(result.ErrorCode));
                    return false;
                }

                LastError = string.Empty;
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.DescribeFailure(ex.ErrorCode));
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                return false;
            }
        }

        /// <summary>
        /// Moves to the room browser and fetches the list. <c>Lobby -> RoomBrowser</c>.
        /// </summary>
        /// <remarks>
        /// The transition happens first and is not undone by a failed fetch: an empty browser
        /// showing an error is somewhere the player can retry from, whereas bouncing back to the
        /// lobby loses the error along with the screen that was going to display it.
        /// </remarks>
        public async Task<bool> OpenRoomBrowserAsync()
        {
            if (_flow.State != GameFlowState.RoomBrowser)
                _flow.Transition(GameFlowState.RoomBrowser);

            return await RefreshRoomsAsync().ConfigureAwait(false);
        }

        /// <summary>Re-fetches the room list without changing state. The browser's refresh button.</summary>
        public async Task<bool> RefreshRoomsAsync()
        {
            try
            {
                long startedTicks = Stopwatch.GetTimestamp();
                Rooms = await _master.GetRoomsAsync().ConfigureAwait(false) ?? Array.Empty<RoomInfo>();

                // Measured around the request the browser was making anyway (P16 3.2). Rounded
                // up rather than truncated so a fast LAN reads "1 ms" instead of "0 ms", which
                // is indistinguishable from "not measured".
                double elapsedMs = (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency;
                MasterPingMs = (int)Math.Ceiling(elapsedMs);

                LastError = string.Empty;
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.DescribeFailure(ex.ErrorCode));
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                return false;
            }
        }

        /// <summary>
        /// Joins a room and keeps the address and ticket it answers with.
        /// </summary>
        /// <remarks>
        /// Drives <c>RoomBrowser -> JoiningRoom -> RoomLobby</c>, or back to
        /// <c>RoomBrowser</c> on failure. It does not dial the game server — that is
        /// <see cref="EnterMatch"/>, which the room lobby triggers when the match starts.
        /// </remarks>
        public async Task<bool> JoinRoomAsync(int roomId, string? password)
        {
            _flow.Transition(GameFlowState.JoiningRoom);

            // Cleared before the send, for the reason CreateRoomAsync records.
            _unclaimedRoom = null;

            try
            {
                // Unsalted, unlike the account password: the master bcrypt-verifies this against
                // what the room's creator sent, and there is no value both sides hold at both
                // moments to salt with. See PasswordHasher.HashRoomPassword.
                string? hash = string.IsNullOrEmpty(password)
                    ? null
                    : PasswordHasher.HashRoomPassword(password!);

                JoinResult result = await _master.JoinRoomAsync(roomId, hash).ConfigureAwait(false);

                if (!result.Ok)
                {
                    Fail(MasterErrorText.DescribeFailure(result.ErrorCode));
                    Recover(GameFlowState.RoomBrowser);
                    return false;
                }

                PendingJoin = new PendingJoin(result.GameServerIp, result.GameServerPort, result.JoinTicket);

                if (!PendingJoin.IsValid)
                {
                    // The master said ok and then named nowhere to go. Reporting it as a join
                    // failure is honest; carrying it forward would surface as a UDP connect
                    // timeout ten seconds later, blaming the wrong machine.
                    PendingJoin = PendingJoin.None;
                    Fail("The master server did not name a game server for that room.");
                    Recover(GameFlowState.RoomBrowser);
                    return false;
                }

                // Read off the browser's own row rather than the join response, which does not
                // carry a map. Set only after PendingJoin is known good, so a failed join can
                // never leave a map id pointing at a room this client is not in.
                JoinedMapId = MapIdOf(roomId);
                JoinedRoomId = roomId;

                ClaimRoomState();

                LastError = string.Empty;
                _flow.Transition(GameFlowState.RoomLobby);
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.DescribeFailure(ex.ErrorCode));
                Recover(GameFlowState.RoomBrowser);
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                Recover(GameFlowState.RoomBrowser);
                return false;
            }
        }

        /// <summary>
        /// Creates a room and puts this client in it. P16 3.1 and 3.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The first caller <c>RoomCreate</c> has ever had from the game.</b> The opcode, the
        /// handler and the server-side tests have existed since the lobby was written; what was
        /// missing was a client. That is why <c>run-e2e.ps1</c> composes
        /// <see cref="IMasterClient"/> itself and opens a second account merely to make a room.
        /// </para>
        /// <para>
        /// <b>The flow moves exactly as a join does</b> -- <c>RoomBrowser -&gt; JoiningRoom -&gt;
        /// RoomLobby</c> -- because the outcome is the same: this client is now a member of a
        /// room, and the room lobby is what draws that. No edge is added to the transition table.
        /// </para>
        /// <para>
        /// <b>No <see cref="PendingJoin"/> is set, and that is correct rather than
        /// incomplete.</b> A room has no game server until somebody joins it, so there is no
        /// address and no ticket to hold yet. Both are fetched when the room starts -- see
        /// <see cref="EnterMatchWithFreshTicketAsync"/>, which is the same path every other
        /// member takes.
        /// </para>
        /// <para>
        /// <b>The caller hashes nothing.</b> A private room's password is hashed here with
        /// <see cref="PasswordHasher.HashRoomPassword"/>, the same unsalted function
        /// <see cref="JoinRoomAsync"/> uses -- the master bcrypt-verifies a joiner's hash against
        /// the creator's, so the two call sites must agree byte for byte. One hasher, three call
        /// sites (P16 3.2).
        /// </para>
        /// </remarks>
        public async Task<bool> CreateRoomAsync(
            string name, ushort mapId, byte maxPlayers, byte botCount, string? password)
        {
            _flow.Transition(GameFlowState.JoiningRoom);

            // Cleared before the send so only a push from THIS round trip can be claimed below.
            _unclaimedRoom = null;

            try
            {
                bool isPrivate = !string.IsNullOrEmpty(password);

                var request = new CreateRoomRequest
                {
                    Name = name ?? string.Empty,
                    MapId = mapId,
                    MaxPlayers = maxPlayers,
                    BotCount = botCount,
                    IsPrivate = isPrivate,
                    PasswordHash = isPrivate ? PasswordHasher.HashRoomPassword(password!) : null,
                };

                CreateRoomResult result = await _master.CreateRoomAsync(request).ConfigureAwait(false);

                if (!result.Ok || result.RoomId == 0)
                {
                    Fail(MasterErrorText.DescribeFailure(result.ErrorCode));
                    Recover(GameFlowState.RoomBrowser);
                    return false;
                }

                // Taken from the form rather than from a room-list row, because the list this
                // client holds predates the room it just made. Both are set together and only on
                // success, for the reason JoinRoomAsync records: neither may ever be left
                // pointing at a room this client is not in.
                JoinedRoomId = result.RoomId;
                JoinedMapId = mapId;

                ClaimRoomState();

                LastError = string.Empty;
                _flow.Transition(GameFlowState.RoomLobby);
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.DescribeFailure(ex.ErrorCode));
                Recover(GameFlowState.RoomBrowser);
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                Recover(GameFlowState.RoomBrowser);
                return false;
            }
        }

        /// <summary>
        /// Marks this client ready, or not ready. P16 3.1.
        /// </summary>
        /// <remarks>
        /// The answer is the next <see cref="OnRoomState"/>, not this task: the ready mark the
        /// screen draws is the master's, and P14's countdown is armed by the master's own rule
        /// over the whole roster. A client that drew its own tick before the push would show
        /// itself ready in a room that had refused it.
        /// </remarks>
        public Task<bool> SetReadyAsync(bool ready) => FireAsync(_master.SetReadyAsync(ready));

        /// <summary>
        /// Asks the master to move this client to <paramref name="team"/>. P16 3.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Never predicted.</b> The master is the only writer of a member's side, and it can
        /// refuse -- the sides would differ by more than one, or the room has left
        /// <c>Waiting</c>. A client that moved its own row and then had to move it back would
        /// show two clients disagreeing for exactly as long as the round trip took, which is the
        /// thing criterion 3 is graded on.
        /// </para>
        /// <para>
        /// A refusal arrives as an <c>ErrorPush</c> and reaches the screen through
        /// <see cref="OnErrorPushed"/>. There is no second error channel.
        /// </para>
        /// </remarks>
        public Task<bool> SetTeamAsync(byte team) => FireAsync(_master.SetTeamAsync(team));

        /// <summary>Sends a lobby chat line. P16 3.1 and 3.4.</summary>
        /// <remarks>
        /// Whitespace is refused here rather than sent, so an accidental Enter does not put a
        /// blank line carrying the sender's name in front of everybody in the room.
        /// </remarks>
        public Task<bool> SendChatAsync(byte channel, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);

            return FireAsync(_master.SendChatAsync(channel, text.Trim()));
        }

        /// <summary>
        /// Awaits a fire-and-forget MSP send, turning its failure into <see cref="LastError"/>.
        /// </summary>
        /// <remarks>
        /// These three opcodes have no response, so the task completes when the bytes are
        /// written and a REFUSAL never lands here -- it arrives later as an <c>ErrorPush</c>.
        /// What this catches is the link failing under the write, which would otherwise be an
        /// unobserved task exception and a control that had silently stopped working.
        /// </remarks>
        private async Task<bool> FireAsync(Task send)
        {
            try
            {
                await send.ConfigureAwait(false);
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.DescribeFailure(ex.ErrorCode));
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                return false;
            }
        }

        // ------------------------------------------------------------------ the junction

        /// <summary>
        /// Leaves the room and returns to the browser. The one edge out of <c>RoomLobby</c> that
        /// is not into a match.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A client that joined a room could not leave it: the transition table had no
        /// <c>RoomLobby -&gt; RoomBrowser</c> edge, so the only way back was to quit the process.
        /// The wire for it existed on both ends the whole time --
        /// <c>MspMessageType.RoomLeaveRequest</c> is sent by <c>MasterClient.LeaveRoomAsync</c>
        /// and handled by <c>MspMessageDispatcher</c>.
        /// </para>
        /// <para>
        /// <b>Refuses rather than throws when the flow has moved on.</b> The button lives on the
        /// room screen, but a click queued one frame before a match start lands after it, and an
        /// <c>IllegalGameFlowTransitionException</c> out of a UI callback is a crash rather than
        /// a declined action.
        /// </para>
        /// <para>
        /// The join is cleared with the room, for <see cref="JoinRoomAsync"/>'s reason read
        /// backwards: <see cref="PendingJoin"/> carries a signed ticket for a room the master has
        /// just removed us from, and leaving it behind would let <see cref="EnterMatch"/> dial a
        /// game server for it.
        /// </para>
        /// </remarks>
        public async Task<bool> LeaveRoomAsync()
        {
            if (_flow.State != GameFlowState.RoomLobby) return false;

            try
            {
                await _master.LeaveRoomAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MasterServerException || IsLinkFailure(ex))
            {
                // The room is left locally either way. A master that did not hear us drops the
                // membership on disconnect, and stranding the player on a room screen they have
                // already left is the worse of the two failures.
                Fail("Lost the connection to the master server.");
            }

            PendingJoin = PendingJoin.None;
            JoinedRoomId = 0;
            JoinedMapId = 0;
            Room = null;
            _unclaimedRoom = null;

            // Cleared with the rest of the room, not just on the match ending: a player who
            // leaves a room that had already started would otherwise carry the flag into the
            // NEXT room and be locked out of its start push.
            _enteringMatch = false;

            _flow.Transition(GameFlowState.RoomBrowser);
            return true;
        }

        /// <summary>
        /// Dials the game server with the ticket from the last join. phase-03 task 3.
        /// </summary>
        /// <remarks>
        /// <c>RoomLobby -> ConnectingGame</c>, then <see cref="OnGameServerConnected"/> or
        /// <see cref="OnGameServerFailed"/>. Holding starts here rather than on the connected
        /// callback so there is no window in which a payload could be routed into a scene that
        /// is not loaded.
        /// </remarks>
        public bool EnterMatch()
        {
            if (!PendingJoin.IsValid)
            {
                Fail("There is no room to join.");
                return false;
            }

            _flow.Transition(GameFlowState.ConnectingGame);
            _junctionDrivesFlow = true;
            return BeginJunction(PendingJoin);
        }

        /// <summary>
        /// Dials a game server directly, with no master and no ticket. phase-03 UI item 14.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The LAN path and phase-03's own stated contingency for the master not being ready:
        /// with a game server running standalone, a peer's address typed into step 07's
        /// direct-connect field is the whole route into a match.
        /// </para>
        /// <para>
        /// <b>It does not drive the flow machine, because the diagram has no edge for it.</b>
        /// Every route into <c>ConnectingGame</c> in phase-03 task 1 comes from
        /// <c>RoomLobby</c>, which a direct dial never passes through. Rather than invent an
        /// edge here and put the table out of sync with its specification, this path reports
        /// through <see cref="OnGameServerConnected"/> and <see cref="OnGameServerFailed"/> and
        /// leaves the flow where it was. Whether the diagram should grow a direct-connect edge
        /// is the client track's call.
        /// </para>
        /// </remarks>
        public void ConnectDirect(string host, int port)
        {
            _junctionDrivesFlow = false;

            // No room, so no map. Cleared rather than left over from an earlier join: a direct
            // dial after a room join would otherwise load the previous room's map.
            JoinedMapId = 0;
            JoinedRoomId = 0;

            // NOT Array.Empty: Connection.BeginConnect rejects a ticket that is not exactly 64
            // bytes before it sends anything, so an empty one never reaches the server that was
            // going to accept it. See PendingJoin.CreateUnsignedTicket.
            BeginJunction(new PendingJoin(host, port, PendingJoin.CreateUnsignedTicket()));
        }

        private bool BeginJunction(in PendingJoin join)
        {
            Inbound.Clear();
            Inbound.Hold();

            _connecting = true;
            _connectDeadline = ConnectTimeoutSeconds;

            try
            {
                _game.Connect(join.Ip, join.Port, join.Ticket);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is SocketException || ex is NotSupportedException)
            {
                // The dial can fail before a packet is sent: a ticket of the wrong length, a
                // port out of range, a host name that will not resolve. Letting that escape
                // would take down whichever frame called it -- and the UI calls this straight
                // out of a button.
                _connecting = false;
                Inbound.Clear();
                FailJunction($"Could not dial {join.Ip}:{join.Port} — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Buffers a payload if the scene is not ready, and reports whether it did.
        /// </summary>
        /// <remarks>
        /// The whole of the receive path's decision:
        /// <c>if (!session.HoldIfLoading(payload)) router.Route(payload);</c>
        /// </remarks>
        public bool HoldIfLoading(ReadOnlySpan<byte> payload) => Inbound.TryHold(payload);

        /// <summary>
        /// The match scene is up: replay what arrived during the load and start playing.
        /// </summary>
        /// <remarks>
        /// Called from the scene load's completion callback. On the master-mediated path this
        /// is what moves the flow to <c>InMatch</c> — phase-03 task 3 puts the transition in
        /// exactly that callback, so the HUD cannot appear over a half-loaded map.
        /// </remarks>
        /// <returns>Payloads replayed.</returns>
        public int OnSceneReady()
        {
            int replayed = Inbound.Release(_route);

            if (_junctionDrivesFlow && _flow.State == GameFlowState.ConnectingGame)
                _flow.Transition(GameFlowState.InMatch);

            return replayed;
        }

        /// <summary>
        /// Leaves the match: drops the UDP link and keeps the TCP one.
        /// </summary>
        /// <remarks>
        /// The asymmetry is the point, and phase-03 task 6 names it: the master connection is
        /// what the lobby is reached through, so closing it here would log the player out every
        /// time a match ended.
        /// </remarks>
        public void LeaveMatch()
        {
            _connecting = false;
            Inbound.Clear();
            PendingJoin = PendingJoin.None;
            _enteringMatch = false;

            // The real transport raises OnDisconnected synchronously from inside Disconnect()
            // (Connection.Disconnect -> Fail(reason, notify: true)), so the handler below runs
            // before the next line does. Without this flag it would report "Disconnected from
            // the game server (LocalRequest)" in red at a player who chose to leave.
            _leaving = true;
            try
            {
                _game.Disconnect();
            }
            finally
            {
                _leaving = false;
            }

            // ConnectingGame is included on purpose. Leaving mid-dial clears _connecting, so the
            // timeout in Tick can never fire, and ConnectingGame's only exits are driven by a
            // junction that no longer exists -- the flow would park there permanently.
            if (_flow.State == GameFlowState.InMatch || _flow.State == GameFlowState.MatchEnd)
                Recover(GameFlowState.Lobby);
            else if (_flow.State == GameFlowState.ConnectingGame)
                Recover(GameFlowState.RoomLobby);
        }

        /// <summary>
        /// One frame of the session: pumps the master link and ages the connect timeout.
        /// </summary>
        /// <remarks>
        /// <c>Poll()</c> is what makes every <see cref="IMasterClient"/> continuation run on the
        /// caller's thread, so this must be called from Unity's main thread and from nowhere
        /// else. That single call is the whole answer to phase-03 trap 1.
        /// </remarks>
        public void Tick(float deltaSeconds)
        {
            _master.Poll();

            if (!_connecting) return;

            _connectDeadline -= deltaSeconds;
            if (_connectDeadline > 0f) return;

            _connecting = false;
            _game.Disconnect();
            Inbound.Clear();

            FailJunction($"The game server did not answer within {ConnectTimeoutSeconds:0} seconds.");
        }

        /// <summary>Unsubscribes from the transport. Call before dropping the session.</summary>
        public void Dispose()
        {
            _master.OnRoomStatePush -= OnRoomStatePushed;
            _master.OnChat -= OnChatPushed;
            _master.OnError -= OnErrorPushed;

            _game.OnConnected -= OnGameConnected;
            _game.OnDisconnected -= OnGameDisconnected;
        }

        // ------------------------------------------------------------------ transport callbacks

        private void OnGameConnected(ConnectResult result)
        {
            _connecting = false;
            LastError = string.Empty;

            // Deliberately NOT a transition to InMatch. The scene has not loaded, and phase-03
            // task 3 puts that transition in the load's completion callback -- see OnSceneReady.
            OnGameServerConnected?.Invoke(result);
        }

        private void OnGameDisconnected(DisconnectReason reason)
        {
            // Our own Disconnect(), re-entering synchronously. LeaveMatch owns the tidy-up and
            // the flow move; reporting an error here would contradict the player's own action.
            if (_leaving) return;

            bool duringJunction = _connecting;
            _connecting = false;
            Inbound.Clear();

            if (duringJunction)
            {
                FailJunction($"The game server refused the connection ({reason}).");
                return;
            }

            // Dropped mid-match. phase-03 criterion 6: back to the lobby with a message, rather
            // than a frozen world nobody is updating.
            Fail($"Disconnected from the game server ({reason}).");

            if (_flow.State == GameFlowState.InMatch || _flow.State == GameFlowState.MatchEnd)
                Recover(GameFlowState.Lobby);
        }

        private void FailJunction(string message)
        {
            Fail(message);

            // The dial failed, so the room is still ours and its next start push must be acted
            // on. Left set, the player would be dropped back into a room lobby whose start
            // button silently no longer worked (P16 3.4).
            _enteringMatch = false;

            if (_junctionDrivesFlow && _flow.State == GameFlowState.ConnectingGame)
                Recover(GameFlowState.RoomLobby);

            OnGameServerFailed?.Invoke(message);
        }

        private void Fail(string message)
        {
            LastError = message;
            OnError?.Invoke(message);
        }

        /// <summary>The map id the newest room list gives for <paramref name="roomId"/>, or 0.</summary>
        /// <remarks>
        /// A linear scan over at most a screenful of rooms, run once per join. A dictionary here
        /// would have to be rebuilt on every refresh to save nothing measurable.
        /// </remarks>
        private ushort MapIdOf(int roomId)
        {
            RoomInfo[] rooms = Rooms;
            for (int i = 0; i < rooms.Length; i++)
                if (rooms[i].RoomId == roomId) return rooms[i].MapId;

            return 0;
        }

        /// <summary>
        /// Moves the flow along a recovery edge, and does nothing if the table has none.
        /// </summary>
        /// <remarks>
        /// Recovery paths use this rather than <c>Transition</c> because they run from inside a
        /// catch block or a transport callback, where the state may already have moved --
        /// two overlapping requests, or a disconnect landing during a response. Throwing there
        /// would replace the failure being reported with a second one nobody is catching. The
        /// happy path still calls <c>Transition</c>, so a genuine caller bug still throws where
        /// it can be seen.
        /// </remarks>
        private void Recover(GameFlowState next) => _flow.TryTransition(next);

        /// <summary>
        /// Whether an exception is the link dying rather than a bug worth propagating.
        /// </summary>
        /// <remarks>
        /// Narrow on purpose. Swallowing everything here would turn a genuine
        /// <c>NullReferenceException</c> in this class into "lost the connection", which is the
        /// silent fallback development-principles.md forbids — the failure would be reported,
        /// but as the wrong thing, which is worse than not catching it.
        /// </remarks>
        private static bool IsLinkFailure(Exception ex)
        {
            // An illegal transition is a bug in this class, not a dead socket. It derives from
            // InvalidOperationException -- which MasterClient also throws, for "not connected"
            // and "already connected" -- so it has to be excluded by name or the filter below
            // would launder a state-machine bug into "lost the connection to the master server".
            if (ex is IllegalGameFlowTransitionException) return false;

            return ex is IOException
                || ex is SocketException
                || ex is ObjectDisposedException
                || ex is OperationCanceledException
                || ex is InvalidOperationException;
        }
    }
}
