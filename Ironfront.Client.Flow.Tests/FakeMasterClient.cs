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
            JoinTicket = new byte[] { 1, 2, 3, 4 },
        };
        public RoomInfo[] NextRooms { get; set; } = Array.Empty<RoomInfo>();

        /// <summary>Thrown by the next call instead of answering, then cleared.</summary>
        public Exception? ThrowOnNextCall { get; set; }

        public int PollCount { get; private set; }
        public string? LastUsername { get; private set; }
        public string? LastPasswordHash { get; private set; }
        public int LastRoomId { get; private set; }
        public string? LastRoomPasswordHash { get; private set; }

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

        public Task<RegisterResult> RegisterAsync(string username, string passwordHash, string displayName, CancellationToken ct = default)
            => Task.FromResult(new RegisterResult(true, 0));
        public Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest request, CancellationToken ct = default)
            => Task.FromResult(new CreateRoomResult(true, 1, 0));
        public Task LeaveRoomAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReadyAsync(bool ready, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendChatAsync(byte channel, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MatchmakeResult> MatchmakeAsync(ushort preferredMapId, CancellationToken ct = default)
            => Task.FromResult(new MatchmakeResult(true, 1, 0, 0));
        public Task CancelMatchmakeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose() => State = MasterConnectionState.Disconnected;

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
    /// The junction is entirely about <i>when</i> things happen — accept, refuse, or say
    /// nothing until the timeout fires — so the fake never completes on its own.
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
            LastHost = host;
            LastPort = port;
            LastTicket = joinTicket.ToArray();
            ConnectCount++;
            State = ConnectionState.Connecting;
        }

        public void Disconnect()
        {
            DisconnectCount++;
            State = ConnectionState.Disconnected;
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
