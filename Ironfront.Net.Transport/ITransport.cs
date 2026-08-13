using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    // NOTE: dev-b-transport/plan.md section 4 sketches a ConnectionState enum in this
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
    }

    /// <summary>Handed to the client when a handshake completes.</summary>
    public readonly struct ConnectResult
    {
        public readonly ushort ConnectionId;
        public readonly uint ServerTick;

        public ConnectResult(ushort connectionId, uint serverTick)
        {
            ConnectionId = connectionId;
            ServerTick   = serverTick;
        }
    }

    /// <summary>What the server knows about one connection.</summary>
    public readonly struct ConnectionInfo
    {
        public readonly ushort ConnectionId;
        public readonly string RemoteAddress;
        public readonly float SmoothedRttMs;
        public readonly ConnectionState State;

        public ConnectionInfo(
            ushort connectionId, string remoteAddress, float smoothedRttMs, ConnectionState state)
        {
            ConnectionId  = connectionId;
            RemoteAddress = remoteAddress;
            SmoothedRttMs = smoothedRttMs;
            State         = state;
        }
    }

    /// <summary>Counters for the HUD and for the load-test report.</summary>
    public struct TransportStats
    {
        public long BytesSent, BytesReceived;
        public long PacketsSent, PacketsReceived, PacketsLost, PacketsResent;
        public float SmoothedRttMs, JitterMs;
        public int PendingReliableCount;
    }

    /// <summary>
    /// The client half of the transport. Frozen in week 1 — Dev A and Dev C write against
    /// this and nothing else (dev-b-transport/plan.md section 4).
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
