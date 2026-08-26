using System;
using System.Net;
using System.Security.Cryptography;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// One client/server GSP connection: lifecycle, packet reliability, channels and
    /// fragmentation. It is polled by its owning transport and never starts a thread.
    /// </summary>
    public sealed class Connection : IDisposable
    {
        private const double HandshakeRetryMs = 250.0;
        private const int MaxHandshakeAttempts = 20;
        private const int LossWindowBucketCount = 5;

        private readonly bool _isClient;
        private readonly BufferPool _pool;
        private readonly ReliabilityLayer _reliability;
        private readonly ChannelSet _channels;
        private readonly FragmentAssembler _fragments;
        private readonly CongestionControl _congestion = new CongestionControl();
        private readonly FlowControl _flow = new FlowControl();
        private readonly Action<ReadOnlyMemory<byte>> _deliverMessage;
        private readonly Action<byte[], int> _resendCallback;
        private readonly long[] _lossReliableSent = new long[LossWindowBucketCount];
        private readonly long[] _lossReliableRetried = new long[LossWindowBucketCount];
        private readonly long[] _lossPacketsReceived = new long[LossWindowBucketCount];
        private readonly long[] _lossPacketsMissing = new long[LossWindowBucketCount];
        private Action<byte[], int, EndPoint, double>? _send;

        private byte[] _joinTicket = Array.Empty<byte>();
        private ulong _clientSalt;
        private ulong _serverSalt;
        private double _lastSendMs;
        private double _lastKeepAliveMs;
        private double _lastReceiveMs;
        private double _lastUpdateMs;
        private double _statsWindowStartMs;
        private long _statsWindowBytesSent;
        private long _statsWindowBytesReceived;
        private double _lossBucketStartMs;
        private int _lossBucketIndex;
        private bool _statsWindowInitialized;
        private bool _lossWindowInitialized;
        private long _lastReliablePacketsSent;
        private long _lastReliablePacketsRetried;
        private long _lastPacketsReceived;
        private long _lastPacketsMissing;
        private int _connectAttempts;
        private bool _disposed;
        private TransportStats _stats;

        public Connection(
            EndPoint remoteEndPoint,
            bool isClient,
            BufferPool? pool = null,
            bool ackBitfieldEnabled = true)
        {
            RemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
            _isClient = isClient;
            _pool = pool ?? new BufferPool(256, ProtocolConstants.MTU_SAFE);
            _reliability = new ReliabilityLayer(_pool)
            {
                AckBitfieldEnabled = ackBitfieldEnabled,
            };
            _channels = new ChannelSet(_pool);
            _fragments = new FragmentAssembler(_pool);
            _deliverMessage = DeliverMessage;
            _resendCallback = Resend;
            State = ConnectionState.Disconnected;
        }

        public ConnectionState State { get; private set; }

        public ushort ConnectionId { get; private set; }

        public EndPoint RemoteEndPoint { get; private set; }

        public float SmoothedRttMs => _reliability.SmoothedRttMs;

        public float JitterMs => _reliability.JitterMs;

        public TransportStats Stats => _stats;

        public CongestionControl Congestion => _congestion;

        /// <summary>
        /// Whether the ack cursor has been seeded yet, i.e. whether any DATA packet has arrived.
        /// </summary>
        /// <remarks>
        /// Exposed because "the handshake must not seed this" is the invariant behind a failure
        /// that is otherwise almost unobservable: seeding it from the server's global control
        /// counter costs nothing on a fresh server — the gap is inside the 32-bit ack bitfield
        /// and repairs itself in a few packets — and costs the client every reliable delivery
        /// for tens of thousands of packets once that counter has been running for a while.
        /// Asserting the symptom needs a server that has already handled hundreds of joins;
        /// asserting the invariant needs one handshake.
        /// </remarks>
        public bool HasSeededAckCursor => _reliability.HasReceivedSequence;

        /// <summary>The sequence this side is currently acking, i.e. the newest it has seen.</summary>
        public ushort AckCursor => _reliability.BuildAck().ack;

        /// <summary>
        /// Periodic keep-alives emitted. Excludes the ack-keep-alive sent on reliable receipt.
        /// </summary>
        /// <remarks>
        /// Observable because keep-alives are the only carrier of flow-control state, and the
        /// failure this counts is a keep-alive that never goes out at all: gating it on the last
        /// send of ANYTHING means a peer streaming at 20 Hz emits none, so the far side's
        /// advertised pressure is never refreshed and a single transient reading pauses reliable
        /// sending permanently. Nothing else about the connection looks wrong when that happens.
        /// </remarks>
        public long PeriodicKeepAlivesSent { get; private set; }

        /// <summary>
        /// The authenticated playerId from the joinTicket, once the handshake has bound it.
        /// </summary>
        /// <remarks>
        /// architecture.md section 9 closes impersonation by binding connectionId to the signed
        /// ticket's playerId. Until this existed the ticket was reduced to a bool and its
        /// playerId never read, so one captured ticket could open as many connections as its
        /// holder liked and every one of them looked like a different player.
        /// </remarks>
        public uint PlayerId { get; internal set; }

        /// <summary>
        /// The sanitized display name from the same signed ticket <see cref="PlayerId"/> came
        /// from. Empty when the ticket carried none, or on a transport with no ticket at all.
        /// </summary>
        /// <remarks>
        /// Set once, on the handshake, beside <see cref="PlayerId"/> — the two are read out of
        /// one <c>JoinTicket.TryReadFields</c> call after the HMAC has verified, and neither is
        /// trustworthy before that. Never null: <c>ConnectionInfo.DisplayName</c> promises the
        /// same, and a nullable hop in the middle would only move the guard.
        /// </remarks>
        public string DisplayName { get; internal set; } = string.Empty;

        /// <summary>
        /// Datagrams discarded because a v1-reserved flag bit was set.
        /// </summary>
        /// <remarks>
        /// The three Dropped* counters exist because <see cref="Receive"/> has three bare
        /// <c>return</c>s that leave no trace of any kind. That is survivable on the server,
        /// which counts its own rejections before ever calling in here; on the CLIENT these
        /// were the whole diagnostic surface, and they were blank. A client that silently
        /// discards every reliable packet and a client that never receives one present
        /// identically — the far side gives up after ten resends either way, and the reason
        /// code it reports (<c>TransportError</c>) is the same. Distinguishing them is the
        /// entire difference between "our parser rejects this" and "the wire lost it", so the
        /// counters are the measurement that has to exist before any theory is worth holding.
        /// </remarks>
        public long DroppedReservedFlags { get; private set; }

        /// <summary>Datagrams discarded because this side is not <c>Connected</c>.</summary>
        /// <remarks>See <see cref="DroppedReservedFlags"/>.</remarks>
        public long DroppedNotConnected { get; private set; }

        /// <summary>Datagrams discarded because the header named a different connection.</summary>
        /// <remarks>See <see cref="DroppedReservedFlags"/>.</remarks>
        public long DroppedWrongConnectionId { get; private set; }

        /// <summary>Reliable datagrams accepted by this side.</summary>
        /// <remarks>
        /// Paired with <see cref="AckKeepAlivesSent"/>: the two must move together, because the
        /// ack-keep-alive is emitted on exactly this event. A gap between them is a send that
        /// failed; a zero in BOTH while the far side is resending is a delivery failure, and
        /// the two diagnoses have nothing in common.
        /// </remarks>
        public long ReliablePacketsReceived { get; private set; }

        /// <summary>Ack-carrying keep-alives emitted on reliable receipt.</summary>
        /// <remarks>See <see cref="ReliablePacketsReceived"/>. Distinct from
        /// <see cref="PeriodicKeepAlivesSent"/>, which counts only the idle timer's.</remarks>
        public long AckKeepAlivesSent { get; private set; }

        public bool CanSendReliable => _flow.CanSendReliable(_reliability.PendingReliableCount)
            && _reliability.CanSendReliable;

        public event Action<ReadOnlyMemory<byte>>? MessageReceived;

        public event Action<ConnectResult>? Connected;

        public event Action<DisconnectReason>? Disconnected;

        /// <summary>Attaches the socket/in-memory output owned by the transport host.</summary>
        public void AttachSender(Action<byte[], int, EndPoint, double> send)
            => _send = send ?? throw new ArgumentNullException(nameof(send));

        /// <summary>Starts the client side of the four-message handshake.</summary>
        public void BeginConnect(ReadOnlySpan<byte> joinTicket, double nowMs)
        {
            ThrowIfDisposed();
            if (!_isClient || State != ConnectionState.Disconnected) return;
            if (joinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
                throw new ArgumentException("A join ticket must be exactly 64 bytes.", nameof(joinTicket));

            _joinTicket = joinTicket.ToArray();
            _clientSalt = CreateSalt();
            _connectAttempts = 0;
            State = ConnectionState.Connecting;
            SendConnectRequest(nowMs);
        }

        /// <summary>Marks a server-side connection as authenticated and ready for payloads.</summary>
        internal void ActivateServer(ushort connectionId, double nowMs)
        {
            if (_isClient) throw new InvalidOperationException("A client connection cannot be activated by the server.");
            ConnectionId = connectionId;
            State = ConnectionState.Connected;
            _lastReceiveMs = nowMs;
            _lastSendMs = nowMs;
            _lastUpdateMs = nowMs;
            ResetMetricWindows(nowMs);
        }

        internal void UpdateEndpoint(EndPoint endpoint)
            => RemoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

        /// <summary>Processes one already validated GSP datagram.</summary>
        internal void Receive(in GspHeader header, ReadOnlyMemory<byte> datagram, double nowMs)
        {
            if (_disposed) return;
            if ((header.Flags & PacketFlags.ReservedMask) != 0)
            {
                DroppedReservedFlags++;
                return;
            }

            if (_isClient && State != ConnectionState.Connected)
            {
                HandleClientHandshake(header, datagram, nowMs);
                return;
            }

            if (State != ConnectionState.Connected)
            {
                DroppedNotConnected++;
                return;
            }

            if (header.ConnectionId != ConnectionId)
            {
                DroppedWrongConnectionId++;
                return;
            }

            _lastReceiveMs = nowMs;
            _stats.PacketsReceived++;
            _stats.BytesReceived += datagram.Length;
            _reliability.ProcessIncomingAck(header.Ack, header.AckBitfield, nowMs);
            _reliability.OnPacketReceived(header.Sequence);

            // There is no standalone ACK datagram in GSP. A prompt keep-alive carries the
            // freshly updated ack window so a quiet receiver does not make the sender give up
            // before the next one-second idle keep-alive.
            if (header.IsReliable)
            {
                ReliablePacketsReceived++;
                Span<byte> ackPayload = stackalloc byte[3];
                WriteFlowControl(ackPayload);
                SendPacket(PacketType.Keepalive, PacketFlags.None, ackPayload, false, nowMs, false);
                AckKeepAlivesSent++;
            }

            ReadOnlyMemory<byte> payload = datagram.Slice(GspHeader.Size, header.PayloadLength);
            switch (header.PacketType)
            {
                case PacketType.Keepalive:
                    if (payload.Length >= 3)
                        _flow.ApplyRemote(new FlowControlInfo(
                            Endian.ReadU16LE(payload.Span, 0), payload.Span[2]));
                    return;

                case PacketType.Disconnect:
                    DisconnectReason reason = payload.Length > 0
                        ? (DisconnectReason)payload.Span[0]
                        : DisconnectReason.RemoteRequest;
                    Fail(reason);
                    return;

                case PacketType.Payload:
                    ProcessPayload(payload);
                    return;

                case PacketType.Fragment:
                    ProcessFragment(payload.Span, nowMs);
                    return;

                default:
                    return;
            }
        }

        /// <summary>Services retries, keep-alive, retransmission and fragment expiry.</summary>
        public void Update(double nowMs)
        {
            ThrowIfDisposed();
            if (_lastUpdateMs == 0.0) _lastUpdateMs = nowMs;
            float deltaSeconds = (float)Math.Max(0.0, nowMs - _lastUpdateMs) / 1000f;
            _lastUpdateMs = nowMs;
            _congestion.Update(deltaSeconds, SmoothedRttMs);
            _fragments.Update(nowMs);

            if (State == ConnectionState.Connecting || State == ConnectionState.Challenged)
            {
                if (nowMs - _lastSendMs >= HandshakeRetryMs)
                {
                    if (++_connectAttempts > MaxHandshakeAttempts)
                    {
                        Fail(DisconnectReason.Timeout);
                        return;
                    }

                    if (State == ConnectionState.Connecting) SendConnectRequest(nowMs);
                    else SendConnectResponse(nowMs);
                }
                return;
            }

            if (State != ConnectionState.Connected) return;

            if (nowMs - _lastReceiveMs > ProtocolConstants.TIMEOUT_MS)
            {
                Fail(DisconnectReason.Timeout);
                return;
            }

            // Gated on the last KEEP-ALIVE, not the last send of anything. _lastSendMs is
            // bumped by every outgoing packet, so on a connection streaming snapshots at 20 Hz
            // the old condition never became true and no keep-alive ever went out. Keep-alives
            // are the only carrier of flow-control state, so the peer's advertised pressure was
            // never refreshed: one transient "pressure > 80" reading latched _pauseNewReliable
            // on and nothing could ever clear it, permanently disabling reliable sends in that
            // direction on exactly the busy connections that need them.
            if (nowMs - _lastKeepAliveMs >= ProtocolConstants.KEEPALIVE_MS)
            {
                _lastKeepAliveMs = nowMs;
                PeriodicKeepAlivesSent++;
                Span<byte> keepAlive = stackalloc byte[3];
                WriteFlowControl(keepAlive);
                SendPacket(PacketType.Keepalive, PacketFlags.None, keepAlive, false, nowMs, false);
            }

            _reliability.Update(nowMs, _resendCallback);

            // A reliable packet that ran out of retransmissions is a hole the ordered channel
            // can never fill: the receiver's next-expected sequence is stuck on it forever, so
            // every spawn, death, hit confirmation and chat message after it is dropped for the
            // rest of the session. Keep-alives carry on, so the 10 s timeout never fires and the
            // connection looks perfectly healthy while delivering nothing. There is no recovery
            // short of a reconnect, so this ends the connection loudly instead of continuing
            // quietly (development-principles.md, "Errors Over Silent Fallbacks").
            if (_reliability.HasAbandonedReliable)
            {
                NetLog.Warn(
                    $"connection {ConnectionId}: a reliable packet was abandoned; the ordered "
                    + "channel cannot recover, disconnecting");
                Fail(DisconnectReason.TransportError);
                return;
            }

            _stats.SmoothedRttMs = SmoothedRttMs;
            _stats.JitterMs = JitterMs;
            _stats.PendingReliableCount = _reliability.PendingReliableCount;
            _stats.PacketsLost = _reliability.PacketsLost;
            UpdateLossWindow(nowMs);
            _stats.PacketLossPercentSent = _windowReliableSent <= 0
                ? 0f
                : _windowReliableRetried * 100f / _windowReliableSent;
            long receivedDenominator = _windowPacketsReceived + _windowPacketsMissing;
            _stats.PacketLossPercentReceived = receivedDenominator <= 0
                ? 0f
                : _windowPacketsMissing * 100f / receivedDenominator;
            _stats.CongestionMode = (int)_congestion.CurrentMode;
            _stats.PendingFragmentGroups = _fragments.PendingGroupCount;
            _stats.BufferPoolRented = _pool.RentedCount;
            UpdateRateStats(nowMs);
        }

        /// <summary>
        /// Sends one logical payload. Large payloads are split into reliable fragments even when
        /// the caller requested unreliable delivery, because an incomplete logical message is
        /// never useful.
        /// </summary>
        public bool Send(byte channelId, ReadOnlySpan<byte> payload, bool reliable, double nowMs)
        {
            ThrowIfDisposed();
            if (State != ConnectionState.Connected) return false;
            if (channelId > (byte)ChannelId.InputSequenced) return false;

            bool ordered = channelId == (byte)ChannelId.ReliableOrdered;
            bool mustBeReliable = reliable || ordered;
            if (mustBeReliable && !CanSendReliable) return false;

            ushort channelSequence = _channels.NextSequence(channelId);

            // ChannelEnvelope.Size, not a bare 3. The envelope is a wire format and it now has
            // a single definition in Ironfront.Net.Protocol that the writer, the reader and the
            // conformance tests all agree on — see protocol-spec.md section 5.1. It lived here
            // as three raw byte pokes, which is how it stayed undocumented for a milestone.
            int envelopeLength = ChannelEnvelope.Size + payload.Length;
            if (envelopeLength <= ProtocolConstants.MAX_PAYLOAD)
            {
                byte[] envelope = _pool.Rent();
                new ChannelEnvelope((ChannelId)channelId, channelSequence).Write(envelope);
                payload.CopyTo(envelope.AsSpan(ChannelEnvelope.Size, payload.Length));
                bool result = SendPacket(
                    PacketType.Payload,
                    mustBeReliable ? PacketFlags.Reliable : PacketFlags.None,
                    envelope.AsSpan(0, envelopeLength),
                    mustBeReliable,
                    nowMs,
                    true,
                    ordered);
                _pool.Return(envelope);
                return result;
            }

            int fragmentCount = Fragmenter.FragmentCount(envelopeLength);
            if (fragmentCount <= 0 || fragmentCount > ProtocolConstants.MAX_FRAGMENTS)
                return false;
            if (_reliability.PendingReliableCount + fragmentCount > FlowControl.MaxUnackedReliable) return false;

            ushort groupId = _nextFragmentGroup++;
            // Fragmentation is deliberately off the hot path. The complete logical message
            // may be larger than one pool buffer; only the individual fragments are pooled.
            byte[] fullEnvelope = new byte[envelopeLength];
            fullEnvelope[0] = channelId;
            Endian.WriteU16LE(fullEnvelope, 1, channelSequence);
            payload.CopyTo(fullEnvelope.AsSpan(3, payload.Length));

            bool allSent = true;
            int capacity = FragmentHeader.PayloadCapacity;
            for (byte index = 0; index < fragmentCount; index++)
            {
                int offset = index * capacity;
                int length = Math.Min(capacity, envelopeLength - offset);
                byte[] fragment = _pool.Rent();
                var fragmentHeader = new FragmentHeader(groupId, index, (byte)fragmentCount);
                fragmentHeader.TryWrite(fragment);
                fullEnvelope.AsSpan(offset, length).CopyTo(fragment.AsSpan(FragmentHeader.Size, length));
                allSent &= SendPacket(
                    PacketType.Fragment,
                    PacketFlags.Reliable | PacketFlags.Fragmented,
                    fragment.AsSpan(0, FragmentHeader.Size + length),
                    true,
                    nowMs,
                    true,
                    ordered);
                _pool.Return(fragment);
            }

            return allSent;
        }

        public void Disconnect(DisconnectReason reason, double nowMs)
        {
            if (_disposed || State == ConnectionState.Disconnected) return;

            if (State == ConnectionState.Connected)
            {
                Span<byte> body = stackalloc byte[1];
                body[0] = (byte)reason;
                for (int i = 0; i < 3; i++)
                    SendPacket(PacketType.Disconnect, PacketFlags.None, body, false, nowMs, false);
            }

            Fail(reason, notify: true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reliability.Clear();
            _channels.Clear();
            _fragments.Clear();
            State = ConnectionState.Disconnected;
        }

        private ushort _nextFragmentGroup;

        private void HandleClientHandshake(in GspHeader header, ReadOnlyMemory<byte> datagram, double nowMs)
        {
            ReadOnlySpan<byte> payload = datagram.Span.Slice(GspHeader.Size, header.PayloadLength);
            switch (header.PacketType)
            {
                case PacketType.ConnectChallenge:
                    if (State != ConnectionState.Connecting && State != ConnectionState.Challenged
                        || payload.Length != ConnectChallengePayload.Size
                        || !ConnectChallengePayload.TryParse(payload, out ConnectChallengePayload challenge))
                        return;
                    _serverSalt = challenge.ServerSalt;
                    // NOT _reliability.OnPacketReceived(header.Sequence). The handshake is
                    // stamped from the server's GLOBAL control counter, which is shared across
                    // every connection and every denied request; the data stream that follows
                    // is stamped from this connection's own counter, starting at 0. Latching
                    // the ack cursor to the control value leaves the client believing it has
                    // already seen sequence N, so every data packet arrives "behind" by more
                    // than the 32-bit bitfield can express and is never acked at all. The
                    // sender then retransmits everything ten times, abandons it, saturates its
                    // unacked window and stops sending reliably — while the connection looks
                    // healthy. Leaving _hasReceived false here lets the FIRST DATA PACKET
                    // initialise the cursor, which is the only value it can correctly be.
                    State = ConnectionState.Challenged;
                    _connectAttempts = 0;
                    SendConnectResponse(nowMs);
                    break;

                case PacketType.ConnectAccepted:
                    if (State != ConnectionState.Challenged
                        || payload.Length != ConnectAcceptedPayload.Size
                        || !ConnectAcceptedPayload.TryParse(payload, out ConnectAcceptedPayload accepted))
                        return;
                    ConnectionId = accepted.ConnectionId;
                    // Same reason as ConnectChallenge above — do not seed the data-stream ack
                    // cursor from the control sequence space.
                    State = ConnectionState.Connected;
                    _lastReceiveMs = nowMs;
                    _lastSendMs = nowMs;
                    _connectAttempts = 0;
                    ResetMetricWindows(nowMs);
                    Connected?.Invoke(new ConnectResult(
                        accepted.ConnectionId,
                        accepted.ServerTick,
                        accepted.MapId,
                        accepted.MyPlayerId));
                    break;

                case PacketType.ConnectDenied:
                    if (payload.Length != ConnectDeniedPayload.Size
                        || !ConnectDeniedPayload.TryParse(payload, out ConnectDeniedPayload denied)) return;
                    Fail(MapDeniedReason(denied.Reason));
                    break;
            }
        }

        private void ProcessPayload(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length < 3) return;
            byte channelId = payload.Span[0];
            ushort sequence = Endian.ReadU16LE(payload.Span, 1);
            // ChannelSet copies only packets that must wait for an earlier ordered sequence.
            // Immediate delivery remains callback-scoped, matching the transport contract.
            ReadOnlyMemory<byte> body = payload.Slice(3);
            _channels.Receive(channelId, sequence, body, _deliverMessage);
        }

        private void DeliverMessage(ReadOnlyMemory<byte> message)
            => MessageReceived?.Invoke(message);

        private void ProcessFragment(ReadOnlySpan<byte> payload, double nowMs)
        {
            if (!FragmentHeader.TryParse(payload, out FragmentHeader fragment)) return;
            if (_fragments.TryReassemble(
                    fragment.GroupId,
                    fragment.Index,
                    fragment.Count,
                    payload.Slice(FragmentHeader.Size),
                    nowMs,
                    out byte[] full,
                    out int length))
            {
                ProcessPayload(new ReadOnlyMemory<byte>(full, 0, length));
            }
        }

        private void SendConnectRequest(double nowMs)
        {
            Span<byte> payload = stackalloc byte[ConnectRequestPayload.Size];
            var request = new ConnectRequestPayload(ProtocolConstants.PROTOCOL_VERSION, _joinTicket, _clientSalt);
            request.Write(payload);
            SendPacket(PacketType.ConnectRequest, PacketFlags.Reliable, payload, false, nowMs, false);
        }

        private void SendConnectResponse(double nowMs)
        {
            Span<byte> payload = stackalloc byte[ConnectResponsePayload.Size];
            // The salt and the ticket are echoed because the server deliberately remembers
            // neither — see HandshakeCookie for why keeping state for an unproved address is
            // the resource-exhaustion hole this replaces.
            var response = new ConnectResponsePayload(
                ConnectResponsePayload.ComputeResponse(_clientSalt, _serverSalt),
                _clientSalt,
                _joinTicket);
            response.Write(payload);
            SendPacket(PacketType.ConnectResponse, PacketFlags.Reliable, payload, false, nowMs, false);
        }

        private bool SendPacket(
            PacketType packetType,
            PacketFlags flags,
            ReadOnlySpan<byte> payload,
            bool reliable,
            double nowMs,
            bool trackReliability,
            bool ordered = false)
        {
            if (_send == null) return false;
            if (ordered) flags |= PacketFlags.Ordered;

            byte[] datagram = _pool.Rent();
            (ushort ack, uint bitfield) = _reliability.BuildAck();
            ushort sequence = _reliability.NextSequence();
            int length = PacketBuilder.Write(
                datagram, packetType, flags, sequence, ack, bitfield, ConnectionId, payload);
            if (length < 0)
            {
                _pool.Return(datagram);
                return false;
            }

            if (trackReliability)
                _reliability.OnPacketSent(sequence, datagram.AsSpan(0, length), reliable, nowMs);

            try
            {
                _send(datagram, length, RemoteEndPoint, nowMs);
                _stats.PacketsSent++;
                _stats.BytesSent += length;
                _lastSendMs = nowMs;
                return true;
            }
            finally
            {
                _pool.Return(datagram);
            }
        }

        private void Resend(byte[] datagram, int length)
        {
            if (_send == null) return;
            _send(datagram, length, RemoteEndPoint, _lastUpdateMs);
            _stats.PacketsResent++;
            _stats.PacketsSent++;
            _stats.BytesSent += length;
        }

        private void UpdateRateStats(double nowMs)
        {
            if (!_statsWindowInitialized)
            {
                _statsWindowStartMs = nowMs;
                _statsWindowBytesSent = _stats.BytesSent;
                _statsWindowBytesReceived = _stats.BytesReceived;
                _statsWindowInitialized = true;
                return;
            }

            double elapsed = nowMs - _statsWindowStartMs;
            if (elapsed < 1000.0) return;

            _stats.BytesPerSecondSent = (float)Math.Max(
                0.0, (_stats.BytesSent - _statsWindowBytesSent) * 1000.0 / elapsed);
            _stats.BytesPerSecondReceived = (float)Math.Max(
                0.0, (_stats.BytesReceived - _statsWindowBytesReceived) * 1000.0 / elapsed);
            _statsWindowStartMs = nowMs;
            _statsWindowBytesSent = _stats.BytesSent;
            _statsWindowBytesReceived = _stats.BytesReceived;
        }

        private long _windowReliableSent;
        private long _windowReliableRetried;
        private long _windowPacketsReceived;
        private long _windowPacketsMissing;

        private void ResetMetricWindows(double nowMs)
        {
            Array.Clear(_lossReliableSent, 0, _lossReliableSent.Length);
            Array.Clear(_lossReliableRetried, 0, _lossReliableRetried.Length);
            Array.Clear(_lossPacketsReceived, 0, _lossPacketsReceived.Length);
            Array.Clear(_lossPacketsMissing, 0, _lossPacketsMissing.Length);
            _lossBucketIndex = 0;
            _lossBucketStartMs = nowMs;
            _lossWindowInitialized = true;
            _lastReliablePacketsSent = _reliability.ReliablePacketsSent;
            _lastReliablePacketsRetried = _reliability.ReliablePacketsRetried;
            _lastPacketsReceived = _stats.PacketsReceived;
            _lastPacketsMissing = _reliability.PacketsMissingEstimated;

            _statsWindowStartMs = nowMs;
            _statsWindowBytesSent = _stats.BytesSent;
            _statsWindowBytesReceived = _stats.BytesReceived;
            _statsWindowInitialized = true;
        }

        private void UpdateLossWindow(double nowMs)
        {
            if (!_lossWindowInitialized)
                ResetMetricWindows(nowMs);

            double elapsed = nowMs - _lossBucketStartMs;
            if (elapsed < 0.0)
            {
                ResetMetricWindows(nowMs);
                elapsed = 0.0;
            }

            int elapsedBuckets = (int)(elapsed / 1000.0);
            if (elapsedBuckets >= _lossReliableSent.Length)
            {
                ResetMetricWindows(nowMs);
            }
            else
            {
                for (int i = 0; i < elapsedBuckets; i++)
                {
                    _lossBucketIndex = (_lossBucketIndex + 1) % _lossReliableSent.Length;
                    _lossReliableSent[_lossBucketIndex] = 0;
                    _lossReliableRetried[_lossBucketIndex] = 0;
                    _lossPacketsReceived[_lossBucketIndex] = 0;
                    _lossPacketsMissing[_lossBucketIndex] = 0;
                    _lossBucketStartMs += 1000.0;
                }
            }

            AddDelta(_lossReliableSent, _reliability.ReliablePacketsSent, ref _lastReliablePacketsSent);
            AddDelta(_lossReliableRetried, _reliability.ReliablePacketsRetried, ref _lastReliablePacketsRetried);
            AddDelta(_lossPacketsReceived, _stats.PacketsReceived, ref _lastPacketsReceived);
            AddDelta(_lossPacketsMissing, _reliability.PacketsMissingEstimated, ref _lastPacketsMissing);

            _windowReliableSent = Sum(_lossReliableSent);
            _windowReliableRetried = Sum(_lossReliableRetried);
            _windowPacketsReceived = Sum(_lossPacketsReceived);
            _windowPacketsMissing = Sum(_lossPacketsMissing);
        }

        private void AddDelta(long[] buckets, long current, ref long previous)
        {
            long delta = current - previous;
            previous = current;
            if (delta > 0) buckets[_lossBucketIndex] += delta;
        }

        private static long Sum(long[] values)
        {
            long total = 0;
            for (int i = 0; i < values.Length; i++) total += values[i];
            return total;
        }

        private void WriteFlowControl(Span<byte> destination)
        {
            Endian.WriteU16LE(destination, 0, (ushort)_reliability.PendingReliableCount);
            int pressure = _reliability.PendingReliableCount * 100 / FlowControl.MaxUnackedReliable;
            destination[2] = (byte)(pressure > 100 ? 100 : pressure);
        }

        private void Fail(DisconnectReason reason, bool notify = true)
        {
            if (State == ConnectionState.Disconnected) return;
            State = ConnectionState.Disconnected;
            _reliability.Clear();
            _channels.Clear();
            _fragments.Clear();
            if (notify) Disconnected?.Invoke(reason);
        }

        private static DisconnectReason MapDeniedReason(ConnectDenyReason reason)
            => reason switch
            {
                ConnectDenyReason.ServerFull => DisconnectReason.ServerFull,
                ConnectDenyReason.ProtocolVersionMismatch => DisconnectReason.ProtocolMismatch,
                ConnectDenyReason.Banned => DisconnectReason.Banned,
                ConnectDenyReason.AlreadyConnected => DisconnectReason.AlreadyConnected,
                _ => DisconnectReason.InvalidTicket,
            };

        private static ulong CreateSalt()
        {
            byte[] bytes = new byte[sizeof(ulong)];
            RandomNumberGenerator.Fill(bytes);
            return Endian.ReadU64LE(bytes, 0);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Connection));
        }
    }
}
