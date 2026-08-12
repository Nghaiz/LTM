using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.Transport.Loopback
{
    /// <summary>
    /// An in-memory client/server pair that bypasses the socket entirely, with a
    /// <see cref="NetworkSimulator{TDestination}"/> on the wire between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so Dev A and Dev C are not blocked on the reliability layer
    /// (dev-b-transport/phase-00 § Task 7, risk B6). A can exercise client-side prediction
    /// against 200 ms of latency inside a single Editor process, and C can run a full server
    /// tick loop against a fake client in a unit test, with no socket and no second process.
    /// </para>
    /// <para>
    /// <b>Deviation from the phase-00 sketch, deliberate.</b> The sketch declares
    /// <c>LoopbackTransport : ITransportClient, ITransportServer</c>. Both interfaces declare
    /// an event called <c>OnMessage</c> with different signatures, so one type cannot carry
    /// both without explicit interface implementation and a cast at every subscription. This
    /// class is the <i>wire</i> instead, exposing <see cref="Client"/> and
    /// <see cref="Server"/> endpoints. Callers get the same interfaces with none of the
    /// casting.
    /// </para>
    /// <para>
    /// <b>What this does NOT do: reliability.</b> There is no retransmission here — that is
    /// the phase-01 reliability layer's job. What it does model is the observable contract a
    /// caller depends on: a send on a reliable channel is not subject to simulated loss
    /// (still subject to latency and jitter), and channels 1 and 3 are
    /// unreliable-<i>sequenced</i>, so a packet arriving behind one already delivered is
    /// dropped exactly as protocol-spec.md section 5 requires.
    /// </para>
    /// <para>
    /// <b>The clock is virtual.</b> Nothing here reads the wall clock; tests call
    /// <see cref="Advance"/> and get identical behaviour every run. That is the whole point
    /// of pairing it with a seeded simulator.
    /// </para>
    /// </remarks>
    public sealed class LoopbackTransport : IDisposable
    {
        /// <summary>Simulator destination id for the client-to-server direction.</summary>
        public const int RouteClientToServer = 0;

        /// <summary>Simulator destination id for the server-to-client direction.</summary>
        public const int RouteServerToClient = 1;

        /// <summary>u8 channelId + u16 sequence, stripped before delivery.</summary>
        private const int EnvelopeSize = 3;

        /// <summary>The single connection a loopback pair models.</summary>
        public const ushort ConnectionId = 1;

        /// <summary>
        /// One direction of the wire.
        /// </summary>
        /// <remarks>
        /// There is a simulator pair <i>per direction</i>, not one pair shared by both, and
        /// that is load-bearing: <see cref="NetworkSimulator{T}.Flush"/> releases every packet
        /// whose time has come and returns its buffer to the pool. A single shared simulator
        /// would let a flush of the client-to-server direction consume the server-to-client
        /// packets that happened to be due, deliver them to a callback that discards them by
        /// destination, and drop them for good — a silent, direction-dependent packet loss
        /// that no impairment setting asked for.
        /// </remarks>
        private sealed class Direction
        {
            public Direction(SimulatorConfig lossy, SimulatorConfig lossless, BufferPool pool)
            {
                Lossy    = new NetworkSimulator<int>(lossy, pool);
                Lossless = new NetworkSimulator<int>(lossless, pool);
            }

            public NetworkSimulator<int> Lossy { get; }
            public NetworkSimulator<int> Lossless { get; }
        }

        private readonly BufferPool _pool;
        private readonly SimulatorConfig _config;
        private readonly Direction[] _directions;
        private readonly LoopbackClient _client;
        private readonly LoopbackServer _server;

        // Sequence state for the unreliable-sequenced channels, per route per channel.
        private readonly ushort[] _sendSequence = new ushort[2 * 256];
        private readonly ushort[] _lastDelivered = new ushort[2 * 256];
        private readonly bool[] _hasDelivered = new bool[2 * 256];

        private double _nowMs;
        private bool _disposed;

        public LoopbackTransport(SimulatorConfig? config = null, BufferPool? pool = null)
        {
            _config = config ?? SimulatorConfig.Disabled();
            _pool   = pool ?? new BufferPool(256, ProtocolConstants.MTU_SAFE);

            // A second simulator, identical but incapable of dropping or duplicating, carries
            // the reliable channel. Its seed is offset so the two do not draw the same
            // jitter sequence in lockstep, which would correlate the channels in a way a real
            // network never does.
            SimulatorConfig lossless = _config.Clone();
            lossless.PacketLossPercent = 0f;
            lossless.DuplicatePercent  = 0f;
            lossless.RandomSeed        = unchecked(_config.RandomSeed + 1);

            // Each direction gets its own RNG stream as well as its own queue. Sharing one
            // would correlate upstream and downstream impairments — losing an input packet
            // and the snapshot answering it in the same instant — which is not how two
            // independent paths through a network behave.
            _directions = new Direction[2];
            for (int route = 0; route < _directions.Length; route++)
            {
                SimulatorConfig routeLossy = _config.Clone();
                routeLossy.RandomSeed = unchecked(_config.RandomSeed + route * 1000);

                SimulatorConfig routeLossless = lossless.Clone();
                routeLossless.RandomSeed = unchecked(lossless.RandomSeed + route * 1000);

                _directions[route] = new Direction(routeLossy, routeLossless, _pool);
            }

            _client = new LoopbackClient(this);
            _server = new LoopbackServer(this);
        }

        /// <summary>The client endpoint. Hand this to Dev A's client code.</summary>
        public ITransportClient Client => _client;

        /// <summary>The server endpoint. Hand this to the server tick loop.</summary>
        public ITransportServer Server => _server;

        /// <summary>The virtual clock, in milliseconds since construction.</summary>
        public double NowMs => _nowMs;

        /// <summary>Simulated packets dropped, both directions combined.</summary>
        public long DroppedCount
            => _directions[RouteClientToServer].Lossy.DroppedCount
             + _directions[RouteServerToClient].Lossy.DroppedCount;

        /// <summary>Packets discarded on arrival for being older than one already delivered.</summary>
        public long StaleDroppedCount { get; private set; }

        /// <summary>
        /// Moves the virtual clock forward. Does not deliver anything on its own — call
        /// <c>Poll()</c> on the endpoints afterwards, which is what a real frame does too.
        /// </summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs < 0.0) throw new ArgumentOutOfRangeException(nameof(deltaMs));
            _nowMs += deltaMs;
        }

        /// <summary>Advances the clock and polls both endpoints. The convenience path for tests.</summary>
        public void Step(double deltaMs)
        {
            Advance(deltaMs);
            _server.Poll();
            _client.Poll();
        }

        private static int SlotOf(int route, byte channelId) => route * 256 + channelId;

        private static bool IsSequencedChannel(byte channelId)
            => channelId == (byte)ChannelId.SnapshotSequenced
            || channelId == (byte)ChannelId.InputSequenced;

        private static bool IsReliableChannel(byte channelId)
            => channelId == (byte)ChannelId.ReliableOrdered;

        private void Enqueue(int route, byte channelId, ReadOnlySpan<byte> payload, bool reliable)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LoopbackTransport));

            if (payload.Length > ProtocolConstants.MAX_PAYLOAD)
                throw new ArgumentException(
                    $"Payload is {payload.Length} bytes, the protocol limit is " +
                    $"{ProtocolConstants.MAX_PAYLOAD}.", nameof(payload));

            int slot = SlotOf(route, channelId);
            ushort sequence = _sendSequence[slot]++;

            Span<byte> framed = stackalloc byte[EnvelopeSize + payload.Length];
            framed[0] = channelId;
            Endian.WriteU16LE(framed, 1, sequence);
            payload.CopyTo(framed.Slice(EnvelopeSize));

            Direction direction = _directions[route];
            NetworkSimulator<int> sim =
                (reliable || IsReliableChannel(channelId)) ? direction.Lossless : direction.Lossy;

            // ShouldSend returning true means the simulator is disabled and is not taking
            // ownership, so deliver straight away — a disabled simulator must behave like a
            // zero-latency wire, not like a black hole.
            if (sim.ShouldSend(framed, route, _nowMs)) Deliver(framed, route);
        }

        private void FlushRoute(int route)
        {
            Direction direction = _directions[route];

            direction.Lossless.Flush(_nowMs, DeliverCallback);
            direction.Lossy.Flush(_nowMs, DeliverCallback);
        }

        private void DeliverCallback(byte[] buffer, int length, int route)
            => Deliver(new ReadOnlySpan<byte>(buffer, 0, length), route);

        private void Deliver(ReadOnlySpan<byte> framed, int route)
        {
            if (framed.Length < EnvelopeSize) return;

            byte channelId  = framed[0];
            ushort sequence = Endian.ReadU16LE(framed, 1);
            ReadOnlySpan<byte> payload = framed.Slice(EnvelopeSize);

            if (IsSequencedChannel(channelId))
            {
                int slot = SlotOf(route, channelId);
                if (_hasDelivered[slot] && !SequenceMath.IsNewer(sequence, _lastDelivered[slot]))
                {
                    // protocol-spec.md section 5: on an unreliable-sequenced channel, a packet
                    // older than one already received is worthless and is dropped.
                    StaleDroppedCount++;
                    return;
                }

                _lastDelivered[slot] = sequence;
                _hasDelivered[slot]  = true;
            }

            // The array is rented for the duration of the callback only, matching the
            // ownership contract documented on ITransportClient.OnMessage: the receiver must
            // copy anything it intends to keep.
            byte[] scratch = _pool.Rent();
            try
            {
                payload.CopyTo(scratch);
                var view = new ReadOnlyMemory<byte>(scratch, 0, payload.Length);

                if (route == RouteClientToServer) _server.RaiseMessage(view);
                else                              _client.RaiseMessage(view);
            }
            finally
            {
                _pool.Return(scratch);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int route = 0; route < _directions.Length; route++)
            {
                _directions[route].Lossy.Clear();
                _directions[route].Lossless.Clear();
            }
        }

        // ------------------------------------------------------------------ client endpoint

        private sealed class LoopbackClient : ITransportClient
        {
            private readonly LoopbackTransport _wire;
            private bool _connectPending;

            public LoopbackClient(LoopbackTransport wire) => _wire = wire;

            public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
            public float SmoothedRttMs => _wire._config.LatencyMs * 2f;
            public float PacketLossPercent => _wire._config.PacketLossPercent;
            public TransportStats Stats => _stats;
            private TransportStats _stats;

            public event Action<ReadOnlyMemory<byte>>? OnMessage;
            public event Action<ConnectResult>? OnConnected;
            public event Action<DisconnectReason>? OnDisconnected;

            public void Connect(string host, int port, ReadOnlySpan<byte> joinTicket)
            {
                if (State != ConnectionState.Disconnected) return;
                State = ConnectionState.Connecting;
                _connectPending = true;
                _wire._server.AcceptPending(joinTicket);
            }

            public void Disconnect()
            {
                if (State == ConnectionState.Disconnected) return;
                State = ConnectionState.Disconnected;
                OnDisconnected?.Invoke(DisconnectReason.LocalRequest);
                _wire._server.NotifyClientGone(DisconnectReason.RemoteRequest);
            }

            public void Send(byte channelId, ReadOnlySpan<byte> payload, bool reliable)
            {
                if (State != ConnectionState.Connected) return;
                _stats.PacketsSent++;
                _stats.BytesSent += payload.Length;
                _wire.Enqueue(RouteClientToServer, channelId, payload, reliable);
            }

            public void Poll()
            {
                _wire.FlushRoute(RouteServerToClient);

                if (_connectPending && _wire._server.HasAcceptedClient)
                {
                    _connectPending = false;
                    State = ConnectionState.Connected;
                    OnConnected?.Invoke(new ConnectResult(ConnectionId, 0));
                }
                else if (_connectPending && _wire._server.RejectedTicket)
                {
                    _connectPending = false;
                    State = ConnectionState.Disconnected;
                    OnDisconnected?.Invoke(DisconnectReason.InvalidTicket);
                }

                _stats.SmoothedRttMs = SmoothedRttMs;
            }

            internal void RaiseMessage(ReadOnlyMemory<byte> payload)
            {
                _stats.PacketsReceived++;
                _stats.BytesReceived += payload.Length;
                OnMessage?.Invoke(payload);
            }

            internal void ForceDisconnect(DisconnectReason reason)
            {
                if (State == ConnectionState.Disconnected) return;
                State = ConnectionState.Disconnected;
                OnDisconnected?.Invoke(reason);
            }

            public void Dispose() => _wire.Dispose();
        }

        // ------------------------------------------------------------------ server endpoint

        private sealed class LoopbackServer : ITransportServer
        {
            private readonly LoopbackTransport _wire;
            private readonly List<ushort> _connections = new List<ushort>();
            private bool _running;

            public LoopbackServer(LoopbackTransport wire) => _wire = wire;

            public int ConnectionCount => _connections.Count;
            internal bool HasAcceptedClient => _connections.Count > 0;
            internal bool RejectedTicket { get; private set; }

            public event Action<ushort, ReadOnlyMemory<byte>>? OnMessage;
            public event Func<ReadOnlyMemory<byte>, bool>? OnValidateTicket;
            public event Action<ushort, ConnectionInfo>? OnClientConnected;
            public event Action<ushort, DisconnectReason>? OnClientDisconnected;

            public void Start(int port, int maxConnections) => _running = true;

            public void Stop()
            {
                _running = false;
                if (_connections.Count > 0) NotifyClientGone(DisconnectReason.LocalRequest);
            }

            public void Send(ushort connectionId, byte channelId, ReadOnlySpan<byte> payload, bool reliable)
            {
                if (!_running || !_connections.Contains(connectionId)) return;
                _wire.Enqueue(RouteServerToClient, channelId, payload, reliable);
            }

            public void Broadcast(byte channelId, ReadOnlySpan<byte> payload, bool reliable)
            {
                for (int i = 0; i < _connections.Count; i++)
                    Send(_connections[i], channelId, payload, reliable);
            }

            public void Disconnect(ushort connectionId, DisconnectReason reason)
            {
                if (!_connections.Remove(connectionId)) return;
                OnClientDisconnected?.Invoke(connectionId, reason);
                _wire._client.ForceDisconnect(reason);
            }

            public ConnectionInfo GetInfo(ushort connectionId)
                => new ConnectionInfo(
                    connectionId, "loopback", _wire._config.LatencyMs * 2f,
                    _connections.Contains(connectionId)
                        ? ConnectionState.Connected
                        : ConnectionState.Disconnected);

            public void Poll() => _wire.FlushRoute(RouteClientToServer);

            internal void AcceptPending(ReadOnlySpan<byte> joinTicket)
            {
                if (!_running) { RejectedTicket = true; return; }

                Func<ReadOnlyMemory<byte>, bool>? validate = OnValidateTicket;
                if (validate != null)
                {
                    // The handler takes ReadOnlyMemory, which a span cannot become without a
                    // copy. This runs once per join, not per packet, so the allocation is
                    // deliberate and irrelevant.
                    if (!validate(joinTicket.ToArray())) { RejectedTicket = true; return; }
                }

                RejectedTicket = false;
                if (_connections.Contains(ConnectionId)) return;

                _connections.Add(ConnectionId);
                OnClientConnected?.Invoke(ConnectionId, GetInfo(ConnectionId));
            }

            internal void NotifyClientGone(DisconnectReason reason)
            {
                if (!_connections.Remove(ConnectionId)) return;
                OnClientDisconnected?.Invoke(ConnectionId, reason);
            }

            internal void RaiseMessage(ReadOnlyMemory<byte> payload)
                => OnMessage?.Invoke(ConnectionId, payload);

            public void Dispose() => _wire.Dispose();
        }
    }
}
