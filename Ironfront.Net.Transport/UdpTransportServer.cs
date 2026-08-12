using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.Transport
{
    /// <summary>Multi-connection server implementation of the frozen transport API.</summary>
    public sealed class UdpTransportServer : ITransportServer
    {
        private const int MaxPendingChallenges = 2048;
        private sealed class PendingChallenge
        {
            public EndPoint Endpoint = null!;
            public ulong ClientSalt;
            public ulong ServerSalt;
            public double CreatedMs;
            public ushort RequestSequence;
        }

        private sealed class AcceptedHandshake
        {
            public EndPoint Endpoint = null!;
            public ulong ClientSalt;
            public ulong ServerSalt;
            public ushort ConnectionId;
            public double CreatedMs;
        }

        private readonly SimulatorConfig? _simulatorConfig;
        private readonly Dictionary<EndpointKey, Connection> _byEndpoint
            = new Dictionary<EndpointKey, Connection>();
        private readonly Dictionary<EndpointKey, PendingChallenge> _pending
            = new Dictionary<EndpointKey, PendingChallenge>();
        private readonly Dictionary<EndpointKey, AcceptedHandshake> _accepted
            = new Dictionary<EndpointKey, AcceptedHandshake>();
        private readonly List<EndpointKey> _expiredChallengeKeys = new List<EndpointKey>(16);
        private readonly RateLimiter _rateLimiter = new RateLimiter();

        private Connection?[] _byId = Array.Empty<Connection?>();
        private Queue<ushort> _freeIds = new Queue<ushort>();
        private UdpPeer? _peer;
        private int _maxConnections;
        private int _connectionCount;
        private ushort _controlSequence;
        private double _nowMs;
        private double _lastRateCleanupMs;
        private bool _running;
        private bool _disposed;

        public UdpTransportServer(SimulatorConfig? simulatorConfig = null)
            => _simulatorConfig = simulatorConfig;

        public int ConnectionCount => _connectionCount;

        public long PacketsFromUnknown { get; private set; }

        public long PacketsWithBadConnectionId { get; private set; }

        public long RateLimitedRequests => _rateLimiter.RejectedCount;

        /// <summary>The OS-assigned UDP port; useful when binding port 0 in tests.</summary>
        public int Port => _peer?.Port ?? 0;

        public event Action<ushort, ReadOnlyMemory<byte>>? OnMessage;

        public event Func<ReadOnlyMemory<byte>, bool>? OnValidateTicket;

        public event Action<ushort, ConnectionInfo>? OnClientConnected;

        public event Action<ushort, DisconnectReason>? OnClientDisconnected;

        public void Start(int port, int maxConnections)
        {
            ThrowIfDisposed();
            if (_running) return;
            if (port < 0 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
            if (maxConnections < 1 || maxConnections > ushort.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(maxConnections));

            _maxConnections = maxConnections;
            _byId = new Connection?[maxConnections + 1];
            _freeIds = new Queue<ushort>(maxConnections);
            for (ushort id = 1; id <= maxConnections; id++) _freeIds.Enqueue(id);

            _peer = new UdpPeer(port, _simulatorConfig);
            _peer.PacketReceived += ReceivePacket;
            _running = true;
            _nowMs = NowMs();
        }

        public void Stop()
        {
            if (!_running) return;
            for (ushort id = 1; id < _byId.Length; id++)
            {
                Connection? connection = _byId[id];
                connection?.Disconnect(DisconnectReason.LocalRequest, _nowMs);
            }

            _pending.Clear();
            _accepted.Clear();
            _peer?.Dispose();
            _peer = null;
            _byEndpoint.Clear();
            _running = false;
            _connectionCount = 0;
        }

        public void Send(ushort connectionId, byte channelId, ReadOnlySpan<byte> payload, bool reliable)
        {
            if (!_running || !TryGetConnection(connectionId, out Connection? connection) || connection == null) return;
            connection.Send(channelId, payload, reliable, _nowMs);
        }

        public void Broadcast(byte channelId, ReadOnlySpan<byte> payload, bool reliable)
        {
            if (!_running) return;
            for (ushort id = 1; id < _byId.Length; id++)
            {
                Connection? connection = _byId[id];
                connection?.Send(channelId, payload, reliable, _nowMs);
            }
        }

        public void Disconnect(ushort connectionId, DisconnectReason reason)
        {
            if (TryGetConnection(connectionId, out Connection? connection) && connection != null)
                connection.Disconnect(reason, _nowMs);
        }

        public ConnectionInfo GetInfo(ushort connectionId)
        {
            if (!TryGetConnection(connectionId, out Connection? connection))
                return default;
            return CreateInfo(connection!);
        }

        public void Poll()
        {
            if (!_running || _peer == null) return;
            _nowMs = NowMs();
            _peer.Poll(_nowMs);

            for (ushort id = 1; id < _byId.Length; id++)
                _byId[id]?.Update(_nowMs);

            if (_nowMs - _lastRateCleanupMs >= 10_000.0)
            {
                _rateLimiter.Cleanup(_nowMs);
                CleanupChallenges();
                _lastRateCleanupMs = _nowMs;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private void ReceivePacket(GspHeader header, ReadOnlyMemory<byte> datagram, EndPoint remote)
        {
            if ((header.Flags & PacketFlags.ReservedMask) != 0) return;
            if (!EndpointKey.TryCreate(remote, out EndpointKey key)) return;
            ReadOnlySpan<byte> payload = datagram.Span.Slice(GspHeader.Size, header.PayloadLength);

            if (header.PacketType == PacketType.ConnectRequest)
            {
                HandleConnectRequest(header, payload, remote, key);
                return;
            }

            if (header.PacketType == PacketType.ConnectResponse)
            {
                HandleConnectResponse(header, payload, key);
                return;
            }

            if (!_byEndpoint.TryGetValue(key, out Connection? connection))
            {
                PacketsFromUnknown++;
                return;
            }
            if (header.ConnectionId != connection.ConnectionId)
            {
                PacketsWithBadConnectionId++;
                return;
            }
            // Once any authenticated packet arrives, the client has observed the accepted
            // state (or is already fully connected); no further handshake replay is needed.
            _accepted.Remove(key);
            connection.Receive(header, datagram, _nowMs);
        }

        private void HandleConnectRequest(
            in GspHeader header,
            ReadOnlySpan<byte> payload,
            EndPoint remote,
            EndpointKey key)
        {
            if (!_rateLimiter.Allow(key.Address, _nowMs)) return;
            if (payload.Length != ConnectRequestPayload.Size
                || !ConnectRequestPayload.TryParse(payload, out ConnectRequestPayload request)) return;

            if (request.ProtocolVersion != ProtocolConstants.PROTOCOL_VERSION)
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.ProtocolVersionMismatch);
                return;
            }

            if (_byEndpoint.ContainsKey(key)) return;

            if (_pending.TryGetValue(key, out PendingChallenge? existing))
            {
                if (existing.ClientSalt == request.ClientSalt)
                    SendChallenge(existing, header.Sequence);
                return;
            }

            if (_connectionCount >= _maxConnections || _freeIds.Count == 0)
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.ServerFull);
                return;
            }

            if (!ValidateTicket(request.JoinTicket))
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.InvalidTicket);
                return;
            }

            if (_pending.Count >= MaxPendingChallenges) EvictOldestChallenge();

            var pending = new PendingChallenge
            {
                Endpoint = CloneEndpoint(remote),
                ClientSalt = request.ClientSalt,
                ServerSalt = CreateSalt(),
                CreatedMs = _nowMs,
                RequestSequence = header.Sequence,
            };
            _pending[key] = pending;
            SendChallenge(pending, header.Sequence);
        }

        private bool ValidateTicket(byte[] ticket)
        {
            Delegate[] validators = OnValidateTicket?.GetInvocationList()
                ?? Array.Empty<Delegate>();
            if (validators.Length == 0) return false;

            ReadOnlyMemory<byte> ticketMemory = new ReadOnlyMemory<byte>(ticket);
            for (int i = 0; i < validators.Length; i++)
            {
                if (validators[i] is not Func<ReadOnlyMemory<byte>, bool> validator
                    || !validator(ticketMemory))
                    return false;
            }

            return true;
        }

        private void HandleConnectResponse(in GspHeader header, ReadOnlySpan<byte> payload, EndpointKey key)
        {
            if (payload.Length != ConnectResponsePayload.Size
                || !ConnectResponsePayload.TryParse(payload, out ConnectResponsePayload response)) return;

            // CONNECT_ACCEPTED is not itself tracked by the per-connection reliability layer:
            // the connection id does not exist until that packet is sent. Keep a small,
            // endpoint-bound replay record so a lost accepted packet can be recovered when the
            // client retries CONNECT_RESPONSE.
            if (!_pending.TryGetValue(key, out PendingChallenge? pending))
            {
                if (!_accepted.TryGetValue(key, out AcceptedHandshake? accepted)
                    || _byId[accepted.ConnectionId] == null
                    || response.ChallengeResponse
                        != ConnectResponsePayload.ComputeResponse(
                            accepted.ClientSalt, accepted.ServerSalt))
                    return;

                SendAccepted(accepted.Endpoint, header.Sequence, accepted.ConnectionId);
                return;
            }

            ulong expected = ConnectResponsePayload.ComputeResponse(
                pending.ClientSalt, pending.ServerSalt);
            if (response.ChallengeResponse != expected) return;

            _pending.Remove(key);
            if (_freeIds.Count == 0) return;
            ushort connectionId = _freeIds.Dequeue();
            Connection connection = new Connection(pending.Endpoint, isClient: false, _peer!.Pool);
            connection.AttachSender(SendRaw);
            connection.ActivateServer(connectionId, _nowMs);
            connection.MessageReceived += payload => OnMessage?.Invoke(connectionId, payload);
            connection.Disconnected += reason => RemoveConnection(connectionId, reason);
            _byId[connectionId] = connection;
            _byEndpoint[key] = connection;
            _connectionCount++;

            _accepted[key] = new AcceptedHandshake
            {
                Endpoint = pending.Endpoint,
                ClientSalt = pending.ClientSalt,
                ServerSalt = pending.ServerSalt,
                ConnectionId = connectionId,
                CreatedMs = _nowMs,
            };
            SendAccepted(pending.Endpoint, header.Sequence, connectionId);
            OnClientConnected?.Invoke(connectionId, CreateInfo(connection));
        }

        private void RemoveConnection(ushort connectionId, DisconnectReason reason)
        {
            if (connectionId >= _byId.Length) return;
            Connection? connection = _byId[connectionId];
            if (connection == null) return;

            if (EndpointKey.TryCreate(connection.RemoteEndPoint, out EndpointKey key))
            {
                _byEndpoint.Remove(key);
                _accepted.Remove(key);
            }
            _byId[connectionId] = null;
            _freeIds.Enqueue(connectionId);
            _connectionCount--;
            OnClientDisconnected?.Invoke(connectionId, reason);
        }

        private void SendChallenge(PendingChallenge pending, ushort requestSequence)
        {
            Span<byte> payload = stackalloc byte[ConnectChallengePayload.Size];
            new ConnectChallengePayload(pending.ServerSalt).Write(payload);
            SendControl(
                pending.Endpoint,
                PacketType.ConnectChallenge,
                PacketFlags.Reliable,
                requestSequence,
                0,
                payload);
        }

        private void SendDenied(EndPoint endpoint, ushort requestSequence, ConnectDenyReason reason)
        {
            Span<byte> payload = stackalloc byte[ConnectDeniedPayload.Size];
            new ConnectDeniedPayload(reason).Write(payload);
            SendControl(endpoint, PacketType.ConnectDenied, PacketFlags.None, requestSequence, 0, payload);
        }

        private void SendAccepted(EndPoint endpoint, ushort responseSequence, ushort connectionId)
        {
            Span<byte> payload = stackalloc byte[ConnectAcceptedPayload.Size];
            new ConnectAcceptedPayload(connectionId, 0, 0, 0).Write(payload);
            SendControl(
                endpoint,
                PacketType.ConnectAccepted,
                PacketFlags.Reliable,
                responseSequence,
                connectionId,
                payload);
        }

        private void SendControl(
            EndPoint endpoint,
            PacketType packetType,
            PacketFlags flags,
            ushort ack,
            ushort connectionId,
            ReadOnlySpan<byte> payload)
        {
            if (_peer == null) return;
            byte[] datagram = _peer.Pool.Rent();
            int length = PacketBuilder.Write(
                datagram,
                packetType,
                flags,
                _controlSequence++,
                ack,
                0,
                connectionId,
                payload);
            if (length >= 0)
            {
                try { _peer.Send(datagram, length, endpoint, _nowMs); }
                finally { _peer.Pool.Return(datagram); }
            }
            else
            {
                _peer.Pool.Return(datagram);
            }
        }

        private void SendRaw(byte[] data, int length, EndPoint endpoint, double nowMs)
            => _peer?.Send(data, length, endpoint, nowMs);

        private void CleanupChallenges()
        {
            _expiredChallengeKeys.Clear();
            foreach (KeyValuePair<EndpointKey, PendingChallenge> pair in _pending)
            {
                if (_nowMs - pair.Value.CreatedMs <= ProtocolConstants.TIMEOUT_MS) continue;
                _expiredChallengeKeys.Add(pair.Key);
            }
            foreach (KeyValuePair<EndpointKey, AcceptedHandshake> pair in _accepted)
            {
                if (_nowMs - pair.Value.CreatedMs <= ProtocolConstants.TIMEOUT_MS) continue;
                _expiredChallengeKeys.Add(pair.Key);
            }
            for (int i = 0; i < _expiredChallengeKeys.Count; i++)
            {
                _pending.Remove(_expiredChallengeKeys[i]);
                _accepted.Remove(_expiredChallengeKeys[i]);
            }
        }

        private void EvictOldestChallenge()
        {
            EndpointKey oldestKey = default;
            double oldest = double.MaxValue;
            bool found = false;
            foreach (KeyValuePair<EndpointKey, PendingChallenge> pair in _pending)
            {
                if (pair.Value.CreatedMs >= oldest) continue;
                oldest = pair.Value.CreatedMs;
                oldestKey = pair.Key;
                found = true;
            }
            if (found) _pending.Remove(oldestKey);
        }

        private bool TryGetConnection(ushort id, out Connection? connection)
        {
            connection = id < _byId.Length ? _byId[id] : null;
            return connection != null;
        }

        private static ConnectionInfo CreateInfo(Connection connection)
            => new ConnectionInfo(
                connection.ConnectionId,
                connection.RemoteEndPoint.ToString() ?? string.Empty,
                connection.SmoothedRttMs,
                connection.State);

        private static EndPoint CloneEndpoint(EndPoint endpoint)
        {
            if (endpoint is ReusableIpv4EndPoint reusable) return reusable.ToIPEndPoint();
            if (endpoint is IPEndPoint ip) return new IPEndPoint(ip.Address, ip.Port);
            throw new NotSupportedException("Only IPv4 endpoints are supported.");
        }

        private static ulong CreateSalt()
        {
            byte[] bytes = new byte[sizeof(ulong)];
            RandomNumberGenerator.Fill(bytes);
            return Endian.ReadU64LE(bytes, 0);
        }

        private static double NowMs()
            => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpTransportServer));
        }
    }
}
