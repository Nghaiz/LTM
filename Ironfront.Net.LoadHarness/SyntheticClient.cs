using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;
using Ironfront.Tools.LoadTest;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>
    /// One synthetic player: a real <see cref="UdpTransportClient"/>, the shipped
    /// <see cref="ClientMessageRouter"/>, and a deterministic input programme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything on the receive path is shipped code.</b> Bytes go
    /// <c>UdpTransportClient</c> → <c>ClientMessageRouter.Route</c> → <c>DeltaDecoder</c> /
    /// <c>VehicleDeltaDecoder</c>, which is the identical path the Unity client takes — see
    /// <c>NetClientBootstrap.OnMessage</c>, which is three lines and one of them is a log. This
    /// class adds a scripted sender and a recorder, and nothing that interprets a payload.
    /// </para>
    /// <para>
    /// <b>Single-threaded by design.</b> <see cref="Poll"/> is driven from the run loop, so
    /// every client advances on one thread and a captured sample is never mid-write. Sixty-four
    /// clients at 30 Hz is a few thousand syscalls a second — the harness is not what will
    /// saturate first, and a thread per client would have made the capture racy for no gain.
    /// </para>
    /// </remarks>
    public sealed class SyntheticClient : IDisposable
    {
        /// <summary>Input frames carried per message, as the Unity client sends them.</summary>
        /// <remarks>
        /// The redundancy IS the reliability on an unreliable channel: the same frame rides
        /// several messages, so a dropped datagram costs nothing.
        /// <c>ClientInputMessage.MaxFrames</c> is the ceiling the codec enforces.
        /// </remarks>
        private const int FramesPerMessage = 4;

        private readonly UdpTransportClient _transport;
        private readonly ClientMessageRouter _router = new ClientMessageRouter();
        private readonly StateCapture _capture = new StateCapture();

        // The Unity client's own policy, compiled into this project by a <Compile Include> link
        // rather than reimplemented -- see the csproj. Per client, because each holds its own
        // decoder and therefore its own baseline.
        private readonly Ironfront.Net.Unity.Client.BaselineAckPolicy _baselineAck =
            new Ironfront.Net.Unity.Client.BaselineAckPolicy();
        private readonly LatencyRecorder _snapshotIntervalMs = new LatencyRecorder();

        /// <summary>Per-opcode byte attribution for phase 4's bandwidth decomposition.</summary>
        private readonly WireByteTally _wire = new WireByteTally();
        private readonly List<InputFrame> _pending = new List<InputFrame>(FramesPerMessage);
        private readonly InputFrame[] _scratch = new InputFrame[FramesPerMessage];
        private readonly byte[] _body = new byte[ClientInputMessage.SizeFor(FramesPerMessage)];
        private readonly byte[] _payload = new byte[ProtocolConstants.MTU_SAFE];

        private readonly HarnessBehavior _behavior;
        private readonly double _inputIntervalMs;

        /// <summary>
        /// Where on the circle this client starts, in radians, drawn once from its own seed.
        /// </summary>
        /// <remarks>
        /// Without it every client walks in lockstep, which is a worse load than one client:
        /// the interest sets move together, so the server's per-viewer work correlates
        /// perfectly and the shed budget is either fine for everyone or blown for everyone.
        /// Seeded rather than random so the run still replays.
        /// </remarks>
        private readonly double _phaseOffset;

        private double _nextInputAtMs;
        private double _lastSnapshotAtMs = -1.0;
        private uint _inputTick;
        private uint _oldestPendingTick;
        private bool _disposed;

        public SyntheticClient(
            int index, HarnessBehavior behavior, int inputHz, SimulatorConfig simulator)
        {
            Index = index;
            _behavior = behavior;
            _inputIntervalMs = 1000.0 / inputHz;

            // Each client gets its own simulator stream, seeded off the run seed plus its own
            // index. One shared seed would give every client an IDENTICAL loss pattern, which
            // is not 5% loss on five clients — it is one impairment applied five times, and a
            // convergence check would pass or fail in lockstep for a reason nobody could see.
            SimulatorConfig own = simulator.Clone();
            own.RandomSeed = unchecked(simulator.RandomSeed + index * 7919);
            _phaseOffset = new Random(own.RandomSeed).NextDouble() * Math.PI * 2.0;

            _transport = new UdpTransportClient(own);
            _transport.OnMessage += OnMessage;
            _transport.OnConnected += OnConnected;
            _transport.OnDisconnected += OnDisconnected;

            _router.OnSnapshotApplied += OnSnapshotApplied;
        }

        /// <summary>Zero-based index within the run, and the client's identity in the report.</summary>
        public int Index { get; }

        /// <summary>
        /// How many baseline acks this client has sent, for the report.
        /// </summary>
        /// <remarks>
        /// Reported beside the delta counts rather than inferred from them: a run showing no
        /// deltas needs to say WHICH half broke — a dead sender here, or an encoder that
        /// received the acks and ignored them.
        /// </remarks>
        public long AcksSent => _baselineAck.AcksSent;

        public ConnectionState State => _transport.State;

        public bool IsConnected => _transport.State == ConnectionState.Connected;

        /// <summary>Connection id the server assigned, or 0 before the handshake completes.</summary>
        public ushort ConnectionId { get; private set; }

        /// <summary>Server tick reported by the handshake.</summary>
        public uint ConnectedAtServerTick { get; private set; }

        /// <summary>Why the link ended, when it did.</summary>
        public DisconnectReason? DisconnectedBecause { get; private set; }

        public TransportStats Stats => _transport.Stats;

        public ClientMessageRouter Router => _router;

        public StateCapture Capture => _capture;

        /// <summary>Which message types this client's received bytes went to.</summary>
        public WireByteTally Wire => _wire;

        /// <summary>Gaps between applied snapshots, in milliseconds.</summary>
        /// <remarks>
        /// The nearest honest thing to "is the stream smooth" that a synthetic client can
        /// measure. It is NOT input lag: nothing here renders, so check 8 stays a human verdict
        /// against lane B's frames, per the phase's honesty clause.
        /// </remarks>
        public LatencyRecorder SnapshotIntervalMs => _snapshotIntervalMs;

        /// <summary>Payloads the router could not classify.</summary>
        public long MalformedMessages => _router.MalformedMessages;

        public long UnknownMessages => _router.UnknownMessages;

        public long SnapshotsApplied => _router.SnapshotsApplied;

        public long VehicleSnapshotsApplied => _router.VehicleSnapshotsApplied;

        /// <summary>Harness clock at the current <see cref="Poll"/>, for the capture stamp.</summary>
        private double _nowMs;

        /// <summary>Dials the server with the ticket the run minted for this client.</summary>
        /// <remarks>
        /// <b>The ticket is the caller's to choose, and that is the point.</b> An earlier
        /// version hardcoded 64 zero bytes — what <c>PendingJoin.CreateUnsignedTicket</c> hands
        /// the Unity client — and every client was refused with <c>InvalidTicket</c> against a
        /// server that had a shared secret configured, which is every server worth measuring.
        /// A harness that can only join a server with its security switched off is not
        /// measuring the server anybody runs.
        /// </remarks>
        public void Connect(string host, int port, ReadOnlySpan<byte> joinTicket)
        {
            // Length is not negotiable: Connection.BeginConnect throws on anything that is not
            // exactly JOIN_TICKET_SIZE, before a packet leaves — a different failure from a
            // rejected ticket, and one the Unity client has already been bitten by.
            _transport.Connect(host, port, joinTicket);
        }

        /// <summary>Services the socket and sends this client's scripted input.</summary>
        public void Poll(double nowMs)
        {
            _nowMs = nowMs;
            _transport.Poll();

            if (_behavior == HarnessBehavior.Idle || !IsConnected) return;
            if (nowMs < _nextInputAtMs) return;

            _nextInputAtMs = nowMs + _inputIntervalMs;
            PushInput(nowMs);
        }

        /// <summary>
        /// A deterministic circle walk with a sweeping aim.
        /// </summary>
        /// <remarks>
        /// Movement rather than a fixed pose because a world where nothing moves is the one
        /// case delta encoding is best at: every change mask comes back empty, bandwidth
        /// collapses, and the run reports an excellent number that describes no game anybody
        /// plays. The circle is derived from the client's own seeded phase, so two runs at one
        /// seed produce identical input.
        /// </remarks>
        private void PushInput(double nowMs)
        {
            double phase = _phaseOffset + nowMs / 1000.0;
            float yaw = (float)((_phaseOffset * 57.2957795 + nowMs / 40.0) % 360.0);

            var frame = InputFrame.FromFloats(
                (float)Math.Cos(phase),
                (float)Math.Sin(phase),
                yaw,
                pitchDegrees: 0f,
                InputButtons.None);

            _inputTick++;
            if (_pending.Count == 0) _oldestPendingTick = _inputTick;
            _pending.Add(frame);

            if (_pending.Count > FramesPerMessage)
            {
                _pending.RemoveAt(0);
                _oldestPendingTick = unchecked(_inputTick - (uint)(FramesPerMessage - 1));
            }

            for (int i = 0; i < _pending.Count; i++) _scratch[i] = _pending[i];

            int bodyLength = ClientInputMessage.Write(
                _body, _oldestPendingTick, new ReadOnlySpan<InputFrame>(_scratch, 0, _pending.Count));
            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.InputSequenced);
            if (!writer.WriteMessage(
                    ClientMessageType.Input, new ReadOnlySpan<byte>(_body, 0, bodyLength)))
                return;
            if (!writer.TryFinish(out int total)) return;

            _transport.Send(
                (byte)ChannelId.InputSequenced,
                new ReadOnlySpan<byte>(_payload, 0, total),
                reliable: false);
        }

        /// <summary>
        /// Routes the batch through the shipped client path, then attributes its bytes.
        /// </summary>
        /// <remarks>
        /// <b>Route first, tally second.</b> The tally must never be able to change what the
        /// router sees or when it sees it — a measurement that perturbs the thing measured is
        /// the one failure this whole harness exists to avoid. Both reads are over the same
        /// span and neither mutates it, so the order is a statement of precedence rather than
        /// a correctness requirement.
        /// </remarks>
        private void OnMessage(ReadOnlyMemory<byte> payload)
        {
            _router.Route(payload.Span);
            _wire.Observe(payload.Span);
        }

        /// <summary>
        /// Records the cadence, captures the state, and acknowledges the baseline.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without the ack, every byte this harness measures is a full snapshot.</b>
        /// <c>DeltaEncoder.TryFindBaseline</c> returns false while the server's
        /// <c>_ackedBaselineTick</c> is 0, so a client that never acks is served FULL snapshots
        /// forever — correct, and large. That was true of every client until phase 3C gave the
        /// Unity side a sender; leaving the harness silent would have made lane A's bandwidth
        /// figures a measurement of a case nothing is in any more, and phase 4 consumes those
        /// figures for ledger rows B-16 and B-17.
        /// </para>
        /// <para>
        /// <b>The shipped policy, linked, not a second one.</b> See this project's csproj: 3C
        /// found the integration suite hand-rolling its own ack beside the real client's, and a
        /// harness that did the same would measure a copy free to drift from what ships.
        /// </para>
        /// <para>
        /// <b><c>Decoder.AckTick</c>, not <c>serverTick</c> and not
        /// <c>lastProcessedInputTick</c>.</b> The latter is the server's opinion of this
        /// client's INPUT clock and names a tick from an unrelated sequence; the ack has to name
        /// the snapshot state actually decoded. <c>NetClientBootstrap.OnSnapshotApplied</c>
        /// makes the same call for the same reason.
        /// </para>
        /// </remarks>
        private void OnSnapshotApplied(uint serverTick, uint lastProcessedInputTick)
        {
            if (_lastSnapshotAtMs >= 0.0) _snapshotIntervalMs.Record(_nowMs - _lastSnapshotAtMs);
            _lastSnapshotAtMs = _nowMs;

            _capture.Capture(_router, _nowMs);

            if (!_baselineAck.TryBuildAck(_router.Decoder.AckTick, out ReadOnlySpan<byte> ack))
                return;

            _transport.Send((byte)Ironfront.Net.Unity.Client.BaselineAckPolicy.Channel, ack, reliable: true);
        }

        private void OnConnected(ConnectResult result)
        {
            ConnectionId = result.ConnectionId;
            ConnectedAtServerTick = result.ServerTick;
        }

        private void OnDisconnected(DisconnectReason reason) => DisconnectedBecause = reason;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _router.OnSnapshotApplied -= OnSnapshotApplied;
            _transport.OnMessage -= OnMessage;
            _transport.OnConnected -= OnConnected;
            _transport.OnDisconnected -= OnDisconnected;

            _transport.Disconnect();
            _transport.Dispose();
        }
    }
}
