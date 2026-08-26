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
        /// <summary>
        /// Packets drained from the socket per <see cref="Poll"/>.
        /// </summary>
        /// <remarks>
        /// The drain loop used to run until the socket was empty, which under a sustained flood
        /// is "until the attacker stops" — the whole game tick lives inside that loop, so a
        /// server being flooded stops simulating rather than merely dropping packets. Anything
        /// still queued is read on the next tick, which at 30 Hz is 33 ms away; a real client at
        /// 30 Hz sends one packet per tick, so 1024 is roughly sixty times a full server's
        /// legitimate burst.
        /// </remarks>
        public const int MaxPacketsPerPoll = 1024;

        private sealed class AcceptedHandshake
        {
            public EndPoint Endpoint = null!;
            public ulong ClientSalt;
            public ulong ServerSalt;
            public ushort ConnectionId;
            public uint PlayerId;
            public double CreatedMs;
        }

        private readonly SimulatorConfig? _simulatorConfig;
        private readonly Dictionary<EndpointKey, Connection> _byEndpoint
            = new Dictionary<EndpointKey, Connection>();
        private readonly HandshakeCookie _cookie = new HandshakeCookie();

        /// <summary>
        /// Which connection currently holds each authenticated playerId.
        /// </summary>
        /// <remarks>
        /// architecture.md section 9 lists impersonation as something server authority closes
        /// "for free" by binding connectionId to the playerId in the signed ticket. It was not
        /// closed: the ticket was reduced to a bool by the validator and its playerId never read,
        /// so one captured ticket opened as many connections as the holder liked, each a
        /// distinct player as far as the rest of the stack could tell. This is that binding.
        /// </remarks>
        private readonly Dictionary<uint, ushort> _playerIdToConnection
            = new Dictionary<uint, ushort>();
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

        /// <summary>
        /// Controls the ACK history for connections created by <see cref="Start"/>. Keep enabled
        /// except for the Phase 4 ACK-bitfield comparison run.
        /// </summary>
        public bool AckBitfieldEnabled { get; set; } = true;

        /// <summary>Server simulation tick copied into CONNECT_ACCEPTED.</summary>
        public uint ServerTick { get; set; }

        /// <summary>Map identifier copied into CONNECT_ACCEPTED.</summary>
        public ushort MapId { get; set; }

        public long PacketsFromUnknown { get; private set; }

        public long PacketsWithBadConnectionId { get; private set; }

        public long RateLimitedRequests => _rateLimiter.RejectedCount;

        /// <summary>
        /// Handshakes denied because the ticket's playerId was already connected.
        /// </summary>
        /// <remarks>
        /// A non-zero value here is either a client reconnecting before the server noticed the
        /// old socket was gone, or somebody using a ticket that is not theirs. Both are worth
        /// seeing; neither is an error.
        /// </remarks>
        public long TotalRejectedByPlayerIdBinding
            => System.Threading.Interlocked.Read(ref _totalRejectedByPlayerIdBinding);

        private long _totalRejectedByPlayerIdBinding;

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

            _playerIdToConnection.Clear();
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
                HandleConnectResponse(header, payload, remote, key);
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

            if (_byEndpoint.ContainsKey(key))
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.AlreadyConnected);
                return;
            }

            if (_connectionCount >= _maxConnections || _freeIds.Count == 0)
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.ServerFull);
                return;
            }

            // NOTHING is stored, and the ticket is deliberately NOT verified here.
            //
            // Until the challenge round trip completes, the source address is a claim. Every
            // byte of state and every HMAC spent on it is spent on behalf of an address that may
            // not exist, and an attacker with a list of forged sources can spend it faster than
            // the server can reclaim it — which is exactly what the old pending-challenge table
            // did, right up to evicting real clients mid-handshake once it filled. The ticket is
            // verified in HandleConnectResponse, where the address has been proved.
            //
            // The reply goes to the claimed address, so a spoofed request only ever sends one
            // datagram to a third party, rate-limited per source. See HandshakeCookie.
            ulong serverSalt = _cookie.Derive(key.Address, key.Port, request.ClientSalt, _nowMs);
            SendChallenge(remote, serverSalt, header.Sequence);
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

        private void HandleConnectResponse(
            in GspHeader header, ReadOnlySpan<byte> payload, EndPoint remote, EndpointKey key)
        {
            if (payload.Length != ConnectResponsePayload.Size
                || !ConnectResponsePayload.TryParse(payload, out ConnectResponsePayload response)) return;

            // CONNECT_ACCEPTED is not itself tracked by the per-connection reliability layer:
            // the connection id does not exist until that packet is sent. Keep a small,
            // endpoint-bound replay record so a lost accepted packet can be recovered when the
            // client retries CONNECT_RESPONSE.
            if (_accepted.TryGetValue(key, out AcceptedHandshake? accepted)
                && _byId[accepted.ConnectionId] != null
                && response.ChallengeResponse
                    == ConnectResponsePayload.ComputeResponse(
                        accepted.ClientSalt, accepted.ServerSalt))
            {
                SendAccepted(accepted.Endpoint, header.Sequence, accepted.ConnectionId, accepted.PlayerId);
                return;
            }

            // The address is only proved once this verifies: the client could not have produced
            // the answer without receiving the challenge we sent TO that address.
            if (!_cookie.Verify(
                    key.Address, key.Port, response.ClientSalt, response.ChallengeResponse, _nowMs))
                return;

            // Now, and only now, is it worth doing real work for this peer.
            if (!ValidateTicket(response.JoinTicket))
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.InvalidTicket);
                return;
            }

            // Bind the ticket's playerId, so one captured ticket cannot become many players.
            //
            // The display name comes out of the SAME parse, and until ledger X-36 it was
            // discarded here with an `out string _` — which is the whole reason the killfeed
            // rendered "#5001". It is safe to read now and only now: Verify ran above, so these
            // bytes are the master's rather than the caller's. Sanitized on the spot because
            // this is the ingress; every later reader would otherwise have to remember.
            if (!JoinTicket.TryReadFields(
                    response.JoinTicket,
                    out uint playerId, out ushort _, out ushort _, out long _,
                    out string ticketDisplayName))
            {
                SendDenied(remote, header.Sequence, ConnectDenyReason.InvalidTicket);
                return;
            }

            // playerId 0 is reserved for "no identity" and is never bound. Real tickets are
            // issued by the master server with a non-zero playerId; 0 is what an anonymous or
            // development-stub ticket carries, and binding it would mean the FIRST such client
            // locked out every other one — sixteen players sharing one unauthenticated slot.
            // The binding engages the moment real tickets do, which is where it matters.
            if (playerId != 0
                && _playerIdToConnection.TryGetValue(playerId, out ushort heldBy)
                && _byId[heldBy] != null)
            {
                // Already playing. Denied rather than silently dropped so the second client sees
                // why, and rather than kicking the first so a stolen ticket cannot evict its
                // rightful owner.
                System.Threading.Interlocked.Increment(ref _totalRejectedByPlayerIdBinding);
                SendDenied(remote, header.Sequence, ConnectDenyReason.AlreadyConnected);
                return;
            }

            ulong serverSalt = _cookie.Derive(key.Address, key.Port, response.ClientSalt, _nowMs);
            if (_freeIds.Count == 0) return;
            ushort connectionId = _freeIds.Dequeue();
            if (playerId != 0) _playerIdToConnection[playerId] = connectionId;
            Connection connection = new Connection(
                CloneEndpoint(remote),
                isClient: false,
                pool: _peer!.Pool,
                ackBitfieldEnabled: AckBitfieldEnabled);
            connection.PlayerId = playerId;
            connection.DisplayName = PlayerNameSanitizer.Sanitize(ticketDisplayName);
            connection.AttachSender(SendRaw);
            connection.ActivateServer(connectionId, _nowMs);
            connection.MessageReceived += payload => OnMessage?.Invoke(connectionId, payload);
            connection.Disconnected += reason => RemoveConnection(connectionId, reason);
            _byId[connectionId] = connection;
            _byEndpoint[key] = connection;
            _connectionCount++;

            _accepted[key] = new AcceptedHandshake
            {
                Endpoint = connection.RemoteEndPoint,
                ClientSalt = response.ClientSalt,
                ServerSalt = serverSalt,
                ConnectionId = connectionId,
                PlayerId = playerId,
                CreatedMs = _nowMs,
            };
            SendAccepted(connection.RemoteEndPoint, header.Sequence, connectionId, connection.PlayerId);
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
            // Release the playerId binding, or that player can never reconnect: the slot
            // would report "already connected" against a connection that no longer exists.
            if (connection.PlayerId != 0
                && _playerIdToConnection.TryGetValue(connection.PlayerId, out ushort boundTo)
                && boundTo == connectionId)
                _playerIdToConnection.Remove(connection.PlayerId);

            _byId[connectionId] = null;
            _freeIds.Enqueue(connectionId);
            _connectionCount--;
            OnClientDisconnected?.Invoke(connectionId, reason);
        }

        private void SendChallenge(EndPoint endpoint, ulong serverSalt, ushort requestSequence)
        {
            Span<byte> payload = stackalloc byte[ConnectChallengePayload.Size];
            new ConnectChallengePayload(serverSalt).Write(payload);
            SendControl(
                endpoint,
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

        private void SendAccepted(
            EndPoint endpoint, ushort responseSequence, ushort connectionId, uint playerId)
        {
            Span<byte> payload = stackalloc byte[ConnectAcceptedPayload.Size];
            new ConnectAcceptedPayload(connectionId, ServerTick, MapId, playerId).Write(payload);
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

        /// <summary>
        /// Expires the accepted-handshake replay records. Nothing else needs sweeping.
        /// </summary>
        /// <remarks>
        /// There is no pending-challenge table to expire any more, and no eviction policy to go
        /// with it. That is the point of the cookie: an unproved address leaves nothing behind,
        /// so there is nothing for a flood to fill and nothing for an eviction policy to throw
        /// out the wrong entry from. <see cref="_accepted"/> only ever holds addresses that
        /// completed a handshake, so it is bounded by the connection count.
        /// </remarks>
        private void CleanupChallenges()
        {
            _expiredChallengeKeys.Clear();
            foreach (KeyValuePair<EndpointKey, AcceptedHandshake> pair in _accepted)
            {
                if (_nowMs - pair.Value.CreatedMs <= ProtocolConstants.TIMEOUT_MS) continue;
                _expiredChallengeKeys.Add(pair.Key);
            }
            for (int i = 0; i < _expiredChallengeKeys.Count; i++)
                _accepted.Remove(_expiredChallengeKeys[i]);
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
                connection.State,
                connection.PlayerId,
                connection.Stats,
                connection.DisplayName);

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
