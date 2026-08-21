using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.Transport
{
    /// <summary>Client implementation of the frozen transport API over a polled UDP socket.</summary>
    public sealed class UdpTransportClient : ITransportClient
    {
        private readonly SimulatorConfig? _simulatorConfig;
        private UdpPeer? _peer;
        private Connection? _connection;
        private bool _disposed;

        public UdpTransportClient(SimulatorConfig? simulatorConfig = null)
            => _simulatorConfig = simulatorConfig;

        /// <summary>
        /// Controls the ACK history for a new connection. Keep enabled except for the Phase 4
        /// ACK-bitfield comparison run; set it before <see cref="Connect"/>.
        /// </summary>
        public bool AckBitfieldEnabled { get; set; } = true;

        public ConnectionState State => _connection?.State ?? ConnectionState.Disconnected;

        public float SmoothedRttMs => _connection?.SmoothedRttMs ?? 0f;

        public float PacketLossPercent
        {
            get
            {
                if (_connection == null) return 0f;
                TransportStats stats = _connection.Stats;
                return stats.PacketLossPercentSent;
            }
        }

        public TransportStats Stats => _connection?.Stats ?? default;

        /// <summary>
        /// Whether a DATA packet has seeded the ack cursor. False immediately after a handshake.
        /// </summary>
        /// <remarks>See <see cref="Connection.HasSeededAckCursor"/> for why this is public.</remarks>
        public bool HasSeededAckCursor => _connection?.HasSeededAckCursor ?? false;

        /// <summary>The sequence this client is currently acking.</summary>
        /// <remarks>See <see cref="Connection.HasSeededAckCursor"/> for why this is public.</remarks>
        public ushort AckCursor => _connection?.AckCursor ?? (ushort)0;

        /// <summary>Periodic keep-alives this client has emitted.</summary>
        /// <remarks>See <see cref="Connection.PeriodicKeepAlivesSent"/> for why this is public.</remarks>
        public long PeriodicKeepAlivesSent => _connection?.PeriodicKeepAlivesSent ?? 0;

        /// <summary>Datagrams this client discarded, by reason.</summary>
        /// <remarks>See <see cref="Connection.DroppedReservedFlags"/> for why these exist.</remarks>
        public long DroppedReservedFlags => _connection?.DroppedReservedFlags ?? 0;

        /// <inheritdoc cref="Connection.DroppedNotConnected"/>
        public long DroppedNotConnected => _connection?.DroppedNotConnected ?? 0;

        /// <inheritdoc cref="Connection.DroppedWrongConnectionId"/>
        public long DroppedWrongConnectionId => _connection?.DroppedWrongConnectionId ?? 0;

        /// <inheritdoc cref="Connection.ReliablePacketsReceived"/>
        public long ReliablePacketsReceived => _connection?.ReliablePacketsReceived ?? 0;

        /// <inheritdoc cref="Connection.AckKeepAlivesSent"/>
        public long AckKeepAlivesSent => _connection?.AckKeepAlivesSent ?? 0;

        public event Action<ReadOnlyMemory<byte>>? OnMessage;

        public event Action<ConnectResult>? OnConnected;

        public event Action<DisconnectReason>? OnDisconnected;

        public void Connect(string host, int port, ReadOnlySpan<byte> joinTicket)
        {
            ThrowIfDisposed();
            if (State != ConnectionState.Disconnected) return;
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (port < 1 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));

            IPAddress address = ResolveIPv4(host);
            _peer = new UdpPeer(0, _simulatorConfig);
            EndPoint remote = new IPEndPoint(address, port);
            _peer.Connect(remote);
            _connection = new Connection(
                remote,
                isClient: true,
                pool: _peer.Pool,
                ackBitfieldEnabled: AckBitfieldEnabled);
            _connection.AttachSender(SendRaw);
            _connection.MessageReceived += payload => OnMessage?.Invoke(payload);
            _connection.Connected += result => OnConnected?.Invoke(result);
            _connection.Disconnected += reason => OnDisconnected?.Invoke(reason);
            _peer.PacketReceived += ReceivePacket;
            _connection.BeginConnect(joinTicket, NowMs());
        }

        public void Disconnect()
        {
            if (_connection == null) return;
            _connection.Disconnect(DisconnectReason.LocalRequest, NowMs());
            DisposePeer();
        }

        public void Send(byte channelId, ReadOnlySpan<byte> payload, bool reliable)
        {
            if (_connection == null) return;
            _connection.Send(channelId, payload, reliable, NowMs());
        }

        public void Poll()
        {
            if (_peer == null || _connection == null) return;
            double nowMs = NowMs();
            _peer.Poll(nowMs);
            _connection.Update(nowMs);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Disconnect(DisconnectReason.LocalRequest, NowMs());
            DisposePeer();
        }

        private void ReceivePacket(GspHeader header, ReadOnlyMemory<byte> datagram, EndPoint remote)
        {
            if (_connection == null) return;
            if (!EndpointKey.Equals(_connection.RemoteEndPoint, remote)) return;
            _connection.Receive(header, datagram, NowMs());
        }

        private void SendRaw(byte[] data, int length, EndPoint endpoint, double nowMs)
        {
            if (_peer == null) return;
            _peer.Send(data, length, endpoint, nowMs);
        }

        private void DisposePeer()
        {
            _peer?.Dispose();
            _peer = null;
            _connection?.Dispose();
            _connection = null;
        }

        private static IPAddress ResolveIPv4(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress? parsed))
            {
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                    throw new NotSupportedException("The v1 UDP peer supports IPv4 endpoints only.");
                return parsed;
            }

            IPAddress? ipv4 = Dns.GetHostAddresses(host)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 == null) throw new SocketException((int)SocketError.HostNotFound);
            return ipv4;
        }

        private static double NowMs()
            => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpTransportClient));
        }
    }
}
