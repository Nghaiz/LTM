using System;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// A scripted <see cref="IMasterClient"/>. Answers whatever the test told it to answer.
    /// </summary>
    /// <remarks>
    /// Completes synchronously, so a test can <c>await</c> without pumping. The real client
    /// completes from <c>Poll()</c> instead, and that contract is covered separately by
    /// <c>MasterSessionTests.TickPumpsTheMasterLink</c> plus <c>MasterClientPollTests</c> in
    /// <c>Ironfront.MasterServer.Tests</c>, which drive a real socket.
    /// </remarks>
    internal sealed class FakeMasterClient : IMasterClient
    {
        public MasterConnectionState State { get; private set; } = MasterConnectionState.Disconnected;

        public LoginResult NextLogin { get; set; } = new LoginResult(true, 0, "token", 42, "Tester");
        public JoinResult NextJoin { get; set; } = new JoinResult
        {
            Ok = true,
            GameServerIp = "203.0.113.7",
            GameServerPort = 27015,
            JoinTicket = DefaultTicket(),
        };
        public RoomInfo[] NextRooms { get; set; } = Array.Empty<RoomInfo>();

        /// <summary>A wire-legal 64-byte ticket, as the master would issue.</summary>
        private static byte[] DefaultTicket()
        {
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];
            for (int i = 0; i < ticket.Length; i++) ticket[i] = (byte)(i + 1);
            return ticket;
        }

        /// <summary>Thrown by the next call instead of answering, then cleared.</summary>
        public Exception? ThrowOnNextCall { get; set; }

        public int PollCount { get; private set; }
        public string? LastUsername { get; private set; }
        public string? LastPasswordHash { get; private set; }
        public int LastRoomId { get; private set; }
        public string? LastRoomPasswordHash { get; private set; }

        /// <summary>The TLS policy handed to the most recent connect, if any.</summary>
        public MasterClientTlsOptions? LastTls { get; private set; }

        public event Action<RoomState>? OnRoomStatePush;
        public event Action<ChatMessage>? OnChat;
        public event Action<int, string>? OnError;
        public event Action? OnDisconnected;

        public Task ConnectAsync(string host, int port, CancellationToken ct = default)
        {
            Throw();
            State = MasterConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string host, int port, MasterClientTlsOptions? tls, CancellationToken ct = default)
        {
            LastTls = tls;
            return ConnectAsync(host, port, ct);
        }

        public Task<LoginResult> LoginAsync(string username, string passwordHash, CancellationToken ct = default)
        {
            Throw();
            LastUsername = username;
            LastPasswordHash = passwordHash;
            return Task.FromResult(NextLogin);
        }

        public Task<RoomInfo[]> GetRoomsAsync(CancellationToken ct = default)
        {
            Throw();
            return Task.FromResult(NextRooms);
        }

        public Task<JoinResult> JoinRoomAsync(int roomId, string? passwordHash, CancellationToken ct = default)
        {
            Throw();
            LastRoomId = roomId;
            LastRoomPasswordHash = passwordHash;
            return Task.FromResult(NextJoin);
        }

        public void Poll() => PollCount++;

        /// <summary>
        /// Records what the session sent, so a test can compare it with what login sends. P15.
        /// </summary>
        /// <remarks>
        /// <c>Throw()</c> is called first, exactly as <see cref="LoginAsync"/> does it: the link
        /// failures and <c>MasterServerException</c>s a test arms are supposed to reach register
        /// too, and a fake that could not fail would make the register error paths untestable.
        /// </remarks>
        public Task<RegisterResult> RegisterAsync(string username, string passwordHash, string displayName, CancellationToken ct = default)
        {
            Throw();
            RegisterCalls++;
            LastRegisterUsername = username;
            LastRegisterPasswordHash = passwordHash;
            LastRegisterDisplayName = displayName;
            return Task.FromResult(NextRegister);
        }

        /// <summary>What the next <see cref="RegisterAsync"/> answers.</summary>
        public RegisterResult NextRegister { get; set; } = new RegisterResult(true, 0);

        /// <summary>How many times the session asked the master to register.</summary>
        public int RegisterCalls { get; private set; }

        /// <summary>The username the last register sent.</summary>
        public string? LastRegisterUsername { get; private set; }

        /// <summary>
        /// The hash the last register sent, for comparison with <c>LastPasswordHash</c>.
        /// </summary>
        /// <remarks>
        /// A separate field rather than reusing the login one, because criterion 4's whole
        /// question is whether the TWO agree — and a single shared field would be overwritten by
        /// whichever call happened last, making the test pass by construction.
        /// </remarks>
        public string? LastRegisterPasswordHash { get; private set; }

        /// <summary>The display name the last register sent.</summary>
        public string? LastRegisterDisplayName { get; private set; }
        public Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest request, CancellationToken ct = default)
            => Task.FromResult(new CreateRoomResult(true, 1, 0));
        /// <summary>How many times the session asked the master to leave the room.</summary>
        public int LeaveRoomCalls { get; private set; }

        public Task LeaveRoomAsync(CancellationToken ct = default)
        {
            Throw();
            LeaveRoomCalls++;
            return Task.CompletedTask;
        }
        public Task SetReadyAsync(bool ready, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendChatAsync(byte channel, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MatchmakeResult> MatchmakeAsync(ushort preferredMapId, CancellationToken ct = default)
            => Task.FromResult(new MatchmakeResult(true, 1, 0, 0));
        public Task CancelMatchmakeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose() => State = MasterConnectionState.Disconnected;

        /// <summary>Pushes one room state, as the master does on a lifecycle change.</summary>
        internal void PushRoomState(int roomId, RoomLifecycleState state)
            => OnRoomStatePush?.Invoke(new RoomState { RoomId = roomId, State = (byte)state });

        /// <summary>Keeps the compiler from warning that the push events are never raised.</summary>
        internal void RaisePushes()
        {
            OnRoomStatePush?.Invoke(new RoomState());
            OnChat?.Invoke(new ChatMessage());
            OnError?.Invoke(0, string.Empty);
            OnDisconnected?.Invoke();
        }

        private void Throw()
        {
            Exception? pending = ThrowOnNextCall;
            if (pending == null) return;

            ThrowOnNextCall = null;
            throw pending;
        }
    }

    /// <summary>
    /// A scripted <see cref="ITransportClient"/> that connects only when a test says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The junction is entirely about <i>when</i> things happen — accept, refuse, or say
    /// nothing until the timeout fires — so the fake never completes on its own.
    /// </para>
    /// <para>
    /// <b>It mirrors the two places UdpTransportClient is strict, and that is not decoration.</b>
    /// An earlier version of this fake accepted any ticket and raised nothing from
    /// <c>Disconnect()</c>. Both defects it hid were real: a direct connect threw
    /// <c>ArgumentException</c> out of <c>OnGUI</c> because the ticket was empty, and a
    /// deliberate <c>LeaveMatch</c> painted a red "you were disconnected" line, because the real
    /// <c>Connection.Disconnect</c> raises <c>OnDisconnected</c> synchronously before it
    /// returns. A test double looser than the collaborator it stands in for is a test that
    /// passes for the wrong reason.
    /// </para>
    /// </remarks>
    internal sealed class FakeTransportClient : ITransportClient
    {
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public float SmoothedRttMs => 0f;
        public float PacketLossPercent => 0f;
        public TransportStats Stats => default;

        public string? LastHost { get; private set; }
        public int LastPort { get; private set; }
        public byte[] LastTicket { get; private set; } = Array.Empty<byte>();
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }

        public event Action<ReadOnlyMemory<byte>>? OnMessage;
        public event Action<ConnectResult>? OnConnected;
        public event Action<DisconnectReason>? OnDisconnected;

        public void Connect(string host, int port, ReadOnlySpan<byte> joinTicket)
        {
            // Connection.BeginConnect throws exactly this, before a packet is sent.
            if (joinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
                throw new ArgumentException("A join ticket must be exactly 64 bytes.", nameof(joinTicket));

            if (port < 1 || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(port));

            LastHost = host;
            LastPort = port;
            LastTicket = joinTicket.ToArray();
            ConnectCount++;
            State = ConnectionState.Connecting;
        }

        public void Disconnect()
        {
            DisconnectCount++;

            // Connection.Disconnect calls Fail(reason, notify: true), which raises Disconnected
            // on the calling thread before Disconnect returns. Anything that calls this has the
            // handler run underneath it.
            bool wasUp = State != ConnectionState.Disconnected;
            State = ConnectionState.Disconnected;
            if (wasUp) OnDisconnected?.Invoke(DisconnectReason.LocalRequest);
        }

        public void Send(byte channelId, ReadOnlySpan<byte> payload, bool reliable) { }
        public void Poll() { }
        public void Dispose() { }

        /// <summary>The server accepted.</summary>
        public void Accept(ushort connectionId = 1, uint serverTick = 100)
        {
            State = ConnectionState.Connected;
            OnConnected?.Invoke(new ConnectResult(connectionId, serverTick));
        }

        /// <summary>The link went away, for whatever reason.</summary>
        public void Drop(DisconnectReason reason = DisconnectReason.Timeout)
        {
            State = ConnectionState.Disconnected;
            OnDisconnected?.Invoke(reason);
        }

        /// <summary>Hands a payload to whoever is listening, as the real transport would.</summary>
        public void Deliver(byte[] payload) => OnMessage?.Invoke(payload);
    }
}
