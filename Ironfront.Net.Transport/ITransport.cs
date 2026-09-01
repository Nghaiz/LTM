using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    // NOTE: transport/plan.md section 4 sketches a ConnectionState enum in this
    // namespace. It is deliberately not declared here. Ironfront.Net.Protocol already ships
    // ConnectionState as the transcription of the protocol-spec.md section 9 state machine,
    // and two enums for one state machine is exactly the duplicate source of truth the
    // conventions forbid. The interfaces below use the Protocol one; the shape the plan
    // promised A and C is otherwise unchanged.

    /// <summary>Why a connection ended. Maps onto the CONNECT_DENIED reasons in section 3.2.</summary>
    public enum DisconnectReason
    {
        LocalRequest     = 0,
        RemoteRequest    = 1,
        Timeout          = 2,
        ProtocolMismatch = 3,
        ServerFull       = 4,
        InvalidTicket    = 5,
        Banned           = 6,
        TransportError   = 7,
        AlreadyConnected = 8,

        /// <summary>
        /// The side named by the join ticket had no free body. The counterpart of
        /// <c>ConnectDenyReason.TeamFull</c>, and needed separately because the slot claim
        /// happens AFTER the handshake: by then the server disconnects rather than denying,
        /// so a deny code alone could never reach the player.
        /// </summary>
        TeamFull         = 9,

        /// <summary>
        /// Refused for a reason this build does not know. The generic fallback for an
        /// unrecognised <c>ConnectDenyReason</c> — a wrong specific reason reads as
        /// authoritative and sends the player chasing the wrong fix, so an unknown code
        /// must say only what is true: the server refused it.
        /// </summary>
        Refused          = 10,
    }

    /// <summary>Handed to the client when a handshake completes.</summary>
    public readonly struct ConnectResult
    {
        public readonly ushort ConnectionId;
        public readonly uint ServerTick;
        public readonly ushort MapId;
        public readonly uint MyPlayerId;

        public ConnectResult(ushort connectionId, uint serverTick)
            : this(connectionId, serverTick, 0, 0)
        {
        }

        public ConnectResult(ushort connectionId, uint serverTick, ushort mapId, uint myPlayerId)
        {
            ConnectionId = connectionId;
            ServerTick   = serverTick;
            MapId        = mapId;
            MyPlayerId    = myPlayerId;
        }
    }

    /// <summary>What the server knows about one connection.</summary>
    public readonly struct ConnectionInfo
    {
        public readonly ushort ConnectionId;
        public readonly string RemoteAddress;
        public readonly float SmoothedRttMs;
        public readonly ConnectionState State;
        public readonly uint PlayerId;
        public readonly TransportStats Stats;

        /// <summary>
        /// The name this peer joined under, already sanitized, or <see cref="string.Empty"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Read out of the signed join ticket and nowhere else</b> (protocol-spec § 12 has
        /// carried <c>u8[16] displayNameUtf8</c> since the freeze). The transport already
        /// verified the HMAC and already parsed the ticket to bind <see cref="PlayerId"/>; this
        /// is the field beside it that the same parse used to discard with an <c>out string _</c>.
        /// So a real name reaching the killfeed costs no new opcode, no layout change and no
        /// <c>PROTOCOL_VERSION</c> move — ledger X-36 was filed believing it needed all three.
        /// </para>
        /// <para>
        /// <b>Empty is the normal state on a transport with no ticket to read</b> — the loopback
        /// has none, and a development stub may carry a ticket whose name field is all zeroes.
        /// It is never null, so a consumer never has to guard, and the consumer that renders it
        /// supplies its own fallback (<c>ServerTickLoop.DisplayNameFor</c>).
        /// </para>
        /// <para>
        /// <b>Sanitized at the transport, not at the label</b>, because this is the ingress: the
        /// bytes have crossed a socket by the time anything else sees them, and every later
        /// reader would otherwise have to remember. See <see cref="Ironfront.Net.Protocol.PlayerNameSanitizer"/>.
        /// </para>
        /// </remarks>
        public readonly string DisplayName;

        /// <summary>
        /// The side the master server put this player on, read out of the same signed ticket
        /// <see cref="PlayerId"/> and <see cref="DisplayName"/> came from. 0 or 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes the lobby's balancing arrive.</b> Before it, the game server
        /// re-derived a side from slot parity, so a player's team was an accident of join
        /// order and the lobby's answer was computed and thrown away.
        /// </para>
        /// <para>
        /// <b>0 on a transport with no ticket to read</b> — the loopback has none, and a
        /// development stub carries a ticket whose payload is all zeroes. That is the same
        /// side the first slot has always had, so a ticketless join behaves exactly as it did
        /// before this field existed; it is a default, not a decision.
        /// </para>
        /// </remarks>
        public readonly byte Team;

        public ConnectionInfo(
            ushort connectionId, string remoteAddress, float smoothedRttMs, ConnectionState state)
            : this(connectionId, remoteAddress, smoothedRttMs, state, 0, default)
        {
        }

        public ConnectionInfo(
            ushort connectionId,
            string remoteAddress,
            float smoothedRttMs,
            ConnectionState state,
            uint playerId,
            TransportStats stats)
            : this(connectionId, remoteAddress, smoothedRttMs, state, playerId, stats, string.Empty, 0)
        {
        }

        public ConnectionInfo(
            ushort connectionId,
            string remoteAddress,
            float smoothedRttMs,
            ConnectionState state,
            uint playerId,
            TransportStats stats,
            string displayName)
            : this(connectionId, remoteAddress, smoothedRttMs, state, playerId, stats, displayName, 0)
        {
        }

        public ConnectionInfo(
            ushort connectionId,
            string remoteAddress,
            float smoothedRttMs,
            ConnectionState state,
            uint playerId,
            TransportStats stats,
            string displayName,
            byte team)
        {
            ConnectionId  = connectionId;
            RemoteAddress = remoteAddress;
            SmoothedRttMs = smoothedRttMs;
            State         = state;
            PlayerId      = playerId;
            Stats         = stats;
            DisplayName   = displayName ?? string.Empty;
            Team          = team;
        }
    }

    /// <summary>Counters for the HUD and for the load-test report.</summary>
    public struct TransportStats
    {
        public long BytesSent, BytesReceived;
        public long PacketsSent, PacketsReceived, PacketsLost, PacketsResent;
        public float SmoothedRttMs, JitterMs;
        public int PendingReliableCount;
        public float BytesPerSecondSent, BytesPerSecondReceived;
        public float PacketLossPercentSent, PacketLossPercentReceived;
        public int CongestionMode;
        public int PendingFragmentGroups;
        public int BufferPoolRented;
    }

    /// <summary>
    /// The client half of the transport. Frozen in week 1 — the client track and the replication track write against
    /// this and nothing else (transport/plan.md section 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Buffer ownership — read this before using <see cref="OnMessage"/>.</b> The
    /// <see cref="ReadOnlyMemory{T}"/> handed to the handler points into a pooled buffer
    /// that is returned to the pool the moment the handler returns. Keeping the reference
    /// and reading it on a later frame reads whatever the pool handed out next. If the
    /// payload must outlive the callback, <b>copy it</b>.
    /// </para>
    /// </remarks>
    public interface ITransportClient : IDisposable
    {
        ConnectionState State { get; }
        float SmoothedRttMs { get; }
        float PacketLossPercent { get; }
        TransportStats Stats { get; }

        void Connect(string host, int port, ReadOnlySpan<byte> joinTicket);
        void Disconnect();

        /// <summary>Queues a payload. <paramref name="payload"/> is copied before returning.</summary>
        void Send(byte channelId, ReadOnlySpan<byte> payload, bool reliable);

        /// <summary>Services the socket and the timers. Call once per frame.</summary>
        void Poll();

        /// <summary>Payload received. The buffer is only valid for the duration of the call.</summary>
        event Action<ReadOnlyMemory<byte>> OnMessage;

        event Action<ConnectResult> OnConnected;
        event Action<DisconnectReason> OnDisconnected;
    }

    /// <summary>
    /// The server half of the transport. See <see cref="ITransportClient"/> for the buffer
    /// ownership rule — it applies identically to <see cref="OnMessage"/> here.
    /// </summary>
    public interface ITransportServer : IDisposable
    {
        int ConnectionCount { get; }

        void Start(int port, int maxConnections);
        void Stop();

        void Send(ushort connectionId, byte channelId, ReadOnlySpan<byte> payload, bool reliable);
        void Broadcast(byte channelId, ReadOnlySpan<byte> payload, bool reliable);
        void Disconnect(ushort connectionId, DisconnectReason reason);

        ConnectionInfo GetInfo(ushort connectionId);
        void Poll();

        event Action<ushort, ReadOnlyMemory<byte>> OnMessage;

        /// <summary>
        /// Validates the HMAC/expiry/player policy for a join ticket. Every registered
        /// validator must return <c>true</c>; if no validator is registered, UDP connections
        /// are rejected (fail-closed). The transport does not know the shared secret itself.
        /// </summary>
        event Func<ReadOnlyMemory<byte>, bool> OnValidateTicket;

        event Action<ushort, ConnectionInfo> OnClientConnected;
        event Action<ushort, DisconnectReason> OnClientDisconnected;
    }
}
