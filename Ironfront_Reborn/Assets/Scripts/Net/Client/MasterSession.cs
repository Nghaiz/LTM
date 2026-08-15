#nullable enable

using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Transport;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Drives the online flow: log in, list rooms, join one, and hand the resulting ticket to
    /// the UDP transport. phase-03 tasks 2 and 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Written by the lead's assist track
    /// (plans/assist-dev-a/step-06-master-connection.md).
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
    /// settle with Dev D whether <see cref="IMasterClient"/> callbacks arrive on the main
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

        /// <summary>Whether the junction in flight came through the master, or was a direct dial.</summary>
        private bool _junctionDrivesFlow;

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

        /// <summary>The newest room list. Empty until <see cref="OpenRoomBrowserAsync"/> runs.</summary>
        public RoomInfo[] Rooms { get; private set; } = Array.Empty<RoomInfo>();

        /// <summary>The address and ticket from the last successful join.</summary>
        public PendingJoin PendingJoin { get; private set; } = PendingJoin.None;

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

        // ------------------------------------------------------------------ master server

        /// <summary>Opens the TCP link to the master. Does not log in.</summary>
        public async Task<bool> ConnectAsync(string host, int port)
        {
            try
            {
                await _master.ConnectAsync(host, port).ConfigureAwait(false);
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
                    Fail(MasterErrorText.Describe(result.ErrorCode));
                    _flow.Transition(GameFlowState.LoginScreen);
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
                Fail(MasterErrorText.Describe(ex.ErrorCode));
                _flow.Transition(GameFlowState.LoginScreen);
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                _flow.Transition(GameFlowState.LoginScreen);
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
                Rooms = await _master.GetRoomsAsync().ConfigureAwait(false) ?? Array.Empty<RoomInfo>();
                LastError = string.Empty;
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.Describe(ex.ErrorCode));
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
                    Fail(MasterErrorText.Describe(result.ErrorCode));
                    _flow.Transition(GameFlowState.RoomBrowser);
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
                    _flow.Transition(GameFlowState.RoomBrowser);
                    return false;
                }

                LastError = string.Empty;
                _flow.Transition(GameFlowState.RoomLobby);
                return true;
            }
            catch (MasterServerException ex)
            {
                Fail(MasterErrorText.Describe(ex.ErrorCode));
                _flow.Transition(GameFlowState.RoomBrowser);
                return false;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                Fail("Lost the connection to the master server.");
                _flow.Transition(GameFlowState.RoomBrowser);
                return false;
            }
        }

        // ------------------------------------------------------------------ the junction

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
            BeginJunction(PendingJoin);
            return true;
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
        /// is Dev A's call.
        /// </para>
        /// </remarks>
        public void ConnectDirect(string host, int port)
        {
            _junctionDrivesFlow = false;
            BeginJunction(new PendingJoin(host, port, Array.Empty<byte>()));
        }

        private void BeginJunction(in PendingJoin join)
        {
            Inbound.Clear();
            Inbound.Hold();

            _connecting = true;
            _connectDeadline = ConnectTimeoutSeconds;

            _game.Connect(join.Ip, join.Port, join.Ticket);
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

            _game.Disconnect();

            if (_flow.State == GameFlowState.InMatch || _flow.State == GameFlowState.MatchEnd)
                _flow.Transition(GameFlowState.Lobby);
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
                _flow.Transition(GameFlowState.Lobby);
        }

        private void FailJunction(string message)
        {
            Fail(message);

            if (_junctionDrivesFlow && _flow.State == GameFlowState.ConnectingGame)
                _flow.Transition(GameFlowState.RoomLobby);

            OnGameServerFailed?.Invoke(message);
        }

        private void Fail(string message)
        {
            LastError = message;
            OnError?.Invoke(message);
        }

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
            => ex is IOException
            || ex is SocketException
            || ex is ObjectDisposedException
            || ex is OperationCanceledException
            || ex is InvalidOperationException;
    }
}
