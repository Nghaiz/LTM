using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
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

        /// <summary>The Combat behaviour's state machine, or null for Idle and Move.</summary>
        private readonly CombatDrill? _drill;

        private readonly VerbLog _verbs = new VerbLog();

        private readonly byte[] _seatBody = new byte[SeatRequestMessage.Size];
        private readonly byte[] _vehicleBody = new byte[VehicleInputMessage.Size];

        /// <summary>
        /// A SECOND payload buffer, for the reliable channel.
        /// </summary>
        /// <remarks>
        /// Not shared with <see cref="_payload"/>, which carries the unreliable input frame.
        /// One <see cref="Poll"/> can legitimately frame both — a seat request rides channel 2
        /// while movement keeps flowing on channel 3 — and <c>UdpTransportClient.Send</c> copies
        /// into its own send buffer, so a shared array would be correct today and would silently
        /// stop being correct the moment either side of that stopped holding.
        /// </remarks>
        private readonly byte[] _reliablePayload = new byte[ProtocolConstants.MTU_SAFE];

        /// <summary>Health this client last decoded, per actor, for spotting a drop.</summary>
        /// <remarks>
        /// <b>A drop, not a value.</b> The verb is "damage happened", and the only evidence a
        /// decoded stream carries for it is a health byte that went DOWN between two snapshots
        /// of the same entity. A threshold on the absolute value would report every actor that
        /// spawned on less than full health as damaged, and would miss a 100 -> 99 graze
        /// entirely.
        /// </remarks>
        private readonly Dictionary<ushort, byte> _actorHealth = new Dictionary<ushort, byte>();

        private readonly Dictionary<ushort, byte> _vehicleHealth = new Dictionary<ushort, byte>();

        /// <summary>Where the vehicle was when this client sat down in it, in metres.</summary>
        private float _seatedOriginX, _seatedOriginZ;
        private bool _hasSeatedOrigin;

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

            _drill = behavior == HarnessBehavior.Combat ? new CombatDrill(index) : null;

            _transport = new UdpTransportClient(own);
            _transport.OnMessage += OnMessage;
            _transport.OnConnected += OnConnected;
            _transport.OnDisconnected += OnDisconnected;

            _router.OnSnapshotApplied += OnSnapshotApplied;
            _router.OnSpawnActor += OnSpawnActor;
            _router.OnVehicleSpawn += OnVehicleSpawn;
            _router.OnSeatChange += OnSeatChange;
            _router.OnDeath += OnDeath;
            _router.OnHitConfirm += OnHitConfirm;
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

        /// <summary>
        /// Vehicle snapshots this client REFUSED, and why.
        /// </summary>
        /// <remarks>
        /// <b>Separates "this client was sent fewer vehicles" from "this client threw them
        /// away".</b> Per-client vehicle counts vary enormously between clients on a clean wire
        /// — 449 against 2,022 in one 8-client run — and that spread moves the agreement
        /// denominator by 7x between otherwise identical runs, which is what made X-40's rate
        /// vary six-fold. Interest management culling a distant vehicle and a decoder stuck on
        /// a baseline it never received produce the same shortfall in the applied count and
        /// mean opposite things, so the applied count alone cannot tell them apart.
        /// </remarks>
        public long VehicleUnknownBaselines => _router.VehicleDecoder.UnknownBaselineCount;

        /// <summary>Vehicle snapshots refused as older than one already applied.</summary>
        public long VehicleStaleSnapshots => _router.VehicleDecoder.StaleCount;

        /// <summary>The actor stream's twin of <see cref="VehicleUnknownBaselines"/>.</summary>
        public long ActorUnknownBaselines => _router.Decoder.UnknownBaselineCount;

        /// <summary>The actor stream's twin of <see cref="VehicleStaleSnapshots"/>.</summary>
        public long ActorStaleSnapshots => _router.Decoder.StaleCount;

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

            if (_drill == null) PushInput(nowMs);
            else PushDrill(nowMs);
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

            SendInputFrame(in frame);
        }

        /// <summary>
        /// Puts one input frame on the wire, with the redundancy the Unity client sends.
        /// </summary>
        /// <remarks>
        /// Extracted from <see cref="PushInput"/> when the Combat behaviour arrived, so the
        /// circle walk and the drill frame their input through ONE writer. Two writers is how
        /// one of them ends up with a different <see cref="FramesPerMessage"/>, a different
        /// channel, or a different <c>reliable</c> flag, and the run that finds out is the one
        /// whose numbers are being compared against the other's.
        /// </remarks>
        private void SendInputFrame(in InputFrame frame)
        {
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
        /// Runs one tick of the Combat drill and puts whatever it asks for on the wire.
        /// </summary>
        /// <remarks>
        /// <b>The world is assembled here and the decision is made there.</b> Everything below
        /// is a read of the shipped decoders' current state — never a payload — and the drill
        /// itself has no route to a byte, which is what keeps
        /// <c>tools/check-harness-no-decoder.ps1</c> honest across both files rather than only
        /// across the one that happens to hold a socket.
        /// </remarks>
        private void PushDrill(double nowMs)
        {
            if (_drill == null) return;

            DrillWorld world = BuildWorld();
            DrillCommand command = _drill.Decide(in world, nowMs);

            if (command.SendActorInput)
            {
                var frame = InputFrame.FromFloats(
                    command.MoveX, command.MoveZ,
                    command.YawDegrees, command.PitchDegrees,
                    command.Buttons);

                SendInputFrame(in frame);
            }

            if (command.SendVehicleInput) SendVehicleInput(in command);
            if (command.Seat != SeatIntent.None) SendSeatRequest(in command);
            if (command.SendRespawn) SendSpawnRequest();
        }

        /// <summary>
        /// The drill's view of the world, in metres, from the shipped decoders.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nearest, computed here, handed over as two bodies.</b> The drill needs one target
        /// and one vehicle; giving it the arrays instead would put a search inside a class whose
        /// whole value is that every rule in it can be exercised by <c>dotnet test</c> with three
        /// literals.
        /// </para>
        /// <para>
        /// <b><c>Quantize.UnpackPos</c>, not a scale factor written here.</b> It is the shipped
        /// inverse of the packer the server used, so the metres this produces are the metres the
        /// server sent to within the 6.25 cm the wire carries. A local <c>/ 16f</c> would be a
        /// second transcription of <c>POS_RANGE</c> free to drift from the first.
        /// </para>
        /// </remarks>
        private DrillWorld BuildWorld()
        {
            WorldSnapshot actors = _router.Decoder.Current;

            var me = default(DrillBody);
            bool alive = false;
            float myX = 0f, myZ = 0f;
            bool haveMe = false;

            for (int i = 0; i < actors.ActorCount; i++)
            {
                ref ActorSnapshotEntry entry = ref actors.Actors[i];
                if (entry.ActorId != LocalActorId || LocalActorId == 0) continue;

                myX = Quantize.UnpackPos(entry.PosX);
                myZ = Quantize.UnpackPos(entry.PosZ);
                me = new DrillBody(
                    entry.ActorId, myX, Quantize.UnpackPos(entry.PosY), myZ);
                alive = (entry.StateFlags & ActorStateFlags.IsAlive) != 0;
                haveMe = true;
                break;
            }

            if (!haveMe) return new DrillWorld(default, default, default, alive: false);

            var nearestActor = default(DrillBody);
            float bestActor = float.MaxValue;

            for (int i = 0; i < actors.ActorCount; i++)
            {
                ref ActorSnapshotEntry entry = ref actors.Actors[i];
                if (entry.ActorId == LocalActorId) continue;

                // A corpse is not a target. Shooting one is not damage — ServerFireResolver
                // rejects it — and it would hold this client's aim on a body that will never
                // fall while a live one stands behind it.
                if ((entry.StateFlags & ActorStateFlags.IsAlive) == 0) continue;

                float x = Quantize.UnpackPos(entry.PosX);
                float z = Quantize.UnpackPos(entry.PosZ);
                float squared = (x - myX) * (x - myX) + (z - myZ) * (z - myZ);
                if (squared >= bestActor) continue;

                bestActor = squared;
                nearestActor = new DrillBody(
                    entry.ActorId, x, Quantize.UnpackPos(entry.PosY), z);
            }

            VehicleWorldSnapshot vehicles = _router.VehicleDecoder.Current;
            var nearestVehicle = default(DrillBody);
            float bestVehicle = float.MaxValue;

            for (int i = 0; i < vehicles.VehicleCount; i++)
            {
                ref VehicleSnapshotEntry entry = ref vehicles.Vehicles[i];

                // A wreck has no seat to ask for. The arbiter answers RejectedVehicleDead, so
                // approaching one costs a walk and a round trip to be told what the flag
                // already said.
                if ((entry.Flags & VehicleStateFlags.Dead) != 0) continue;

                float x = Quantize.UnpackPos(entry.PosX);
                float z = Quantize.UnpackPos(entry.PosZ);
                float squared = (x - myX) * (x - myX) + (z - myZ) * (z - myZ);
                if (squared >= bestVehicle) continue;

                bestVehicle = squared;

                // Seat count is NOT in the snapshot — it rides S_VEHICLE_SPAWN — so it is
                // carried forward from the announcement rather than invented. Zero means "not
                // announced to this client", and CombatDrill treats that as "ask for seat 0",
                // which is the seat every vehicle has.
                _seatCounts.TryGetValue(entry.VehicleId, out byte seats);
                nearestVehicle = new DrillBody(
                    entry.VehicleId, x, Quantize.UnpackPos(entry.PosY), z, seats);
            }

            return new DrillWorld(me, nearestActor, nearestVehicle, alive);
        }

        /// <summary>Frames <c>C_VEHICLE_INPUT</c> on the unreliable-sequenced channel.</summary>
        /// <remarks>
        /// Channel 3 and unreliable, matching <c>ClientVehicleStage.SendVehicleInput</c>: unlike
        /// <c>C_INPUT</c> it carries no frame redundancy, because the next frame's axes supersede
        /// this one's entirely (protocol-spec § 4.10).
        /// </remarks>
        private void SendVehicleInput(in DrillCommand command)
        {
            _inputTick++;

            var message = new VehicleInputMessage(
                _inputTick, command.VehicleId,
                command.Throttle, command.Steer, pitchAxis: 0, auxAxis: 0,
                turretYaw: 0, turretPitch: 0, buttons: 0);

            int bodyLength = message.Write(_vehicleBody);
            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.InputSequenced);
            if (!writer.WriteMessage(
                    ClientMessageType.VehicleInput,
                    new ReadOnlySpan<byte>(_vehicleBody, 0, bodyLength)))
                return;
            if (!writer.TryFinish(out int total)) return;

            _transport.Send(
                (byte)ChannelId.InputSequenced,
                new ReadOnlySpan<byte>(_payload, 0, total),
                reliable: false);

            VehicleInputsSent++;
        }

        /// <summary>Frames <c>C_SEAT_REQUEST</c> on the reliable-ordered channel.</summary>
        /// <remarks>
        /// Reliable on channel 2, for <c>ClientSeatRequester.Send</c>'s reason: a dropped seat
        /// request is a client standing at a vehicle whose door never opens, and unlike vehicle
        /// input there is no next frame carrying the same intent — the request is an edge.
        /// </remarks>
        private void SendSeatRequest(in DrillCommand command)
        {
            var message = new SeatRequestMessage(
                command.SeatVehicleId, command.SeatIndex,
                command.Seat == SeatIntent.Enter ? SeatAction.Enter : SeatAction.Leave);

            int bodyLength = message.Write(_seatBody);
            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_reliablePayload, ChannelId.ReliableOrdered);
            if (!writer.WriteMessage(
                    ClientMessageType.SeatRequest,
                    new ReadOnlySpan<byte>(_seatBody, 0, bodyLength)))
                return;
            if (!writer.TryFinish(out int total)) return;

            _transport.Send(
                (byte)ChannelId.ReliableOrdered,
                new ReadOnlySpan<byte>(_reliablePayload, 0, total),
                reliable: true);
        }

        /// <summary>Frames <c>C_SPAWN_REQUEST</c>. The body carries no fields.</summary>
        private void SendSpawnRequest()
        {
            var writer = new PayloadFrameWriter(_reliablePayload, ChannelId.ReliableOrdered);
            if (!writer.WriteMessage(ClientMessageType.SpawnRequest, ReadOnlySpan<byte>.Empty))
                return;
            if (!writer.TryFinish(out int total)) return;

            _transport.Send(
                (byte)ChannelId.ReliableOrdered,
                new ReadOnlySpan<byte>(_reliablePayload, 0, total),
                reliable: true);
        }

        // ------------------------------------------------------------- verb observation

        /// <summary>
        /// The four verbs this client has seen, with the tick each was first seen at.
        /// </summary>
        /// <remarks>
        /// Populated whatever the behaviour is. An <c>Idle</c> or <c>Move</c> client that
        /// happens to watch somebody else's vehicle burn has still WITNESSED the verb, and
        /// suppressing that would make the log a record of what this client did rather than of
        /// what the server was observed doing — which is what check 11 asks about.
        /// </remarks>
        public VerbLog Verbs => _verbs;

        /// <summary>The actor the server named as this client's own, or 0 before S_SPAWN_ACTOR.</summary>
        public ushort LocalActorId { get; private set; }

        /// <summary><c>C_VEHICLE_INPUT</c> messages framed. Zero after a Combat run is the tell.</summary>
        public long VehicleInputsSent { get; private set; }

        /// <summary>The drill's state machine, or null unless the behaviour is Combat.</summary>
        public CombatDrill? Drill => _drill;

        /// <summary>Seat counts as announced by <c>S_VEHICLE_SPAWN</c>, per vehicle.</summary>
        private readonly Dictionary<ushort, byte> _seatCounts = new Dictionary<ushort, byte>();

        private void OnSpawnActor(SpawnActorMessage message)
        {
            if (message.IsLocalPlayer)
            {
                LocalActorId = message.ActorId;
                _drill?.OnLocalSpawn();
            }

            // Seeded from the announcement rather than left to the first snapshot: the health
            // ladder below reads a DROP, and an actor whose first observed value is its
            // post-damage health would have its first hit invisible.
            _actorHealth[message.ActorId] = message.Health;
        }

        private void OnVehicleSpawn(VehicleSpawnMessage message)
        {
            _seatCounts[message.VehicleId] = message.SeatCount;
        }

        private void OnSeatChange(SeatChangeMessage message)
        {
            _drill?.OnSeatChange(
                message.ActorId, message.VehicleId, message.SeatIndex,
                message.Result, LocalActorId);

            if (message.ActorId != LocalActorId || LocalActorId == 0) return;

            if (message.Result == SeatChangeResult.Entered)
            {
                _hasSeatedOrigin = false;
                return;
            }

            if (message.Result == SeatChangeResult.Left) _hasSeatedOrigin = false;
        }

        private void OnDeath(DeathMessage message)
        {
            _verbs.Record(
                HarnessVerb.Death, Index, _router.Decoder.Current.ServerTick, _nowMs,
                $"S_DEATH victim={message.VictimActorId} killer={message.KillerActorId} "
                + $"cause={message.Cause}");

            if (message.VictimActorId == LocalActorId && LocalActorId != 0)
                _drill?.OnLocalDeath(_nowMs);
        }

        private void OnHitConfirm(HitConfirmMessage message)
        {
            _verbs.Record(
                HarnessVerb.Damage, Index, _router.Decoder.Current.ServerTick, _nowMs,
                $"S_HIT_CONFIRM target={message.TargetActorId} hitbox={message.HitboxType}");
        }

        /// <summary>
        /// Reads the four verbs off the decoded world, once per applied snapshot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three of the four are read from state rather than from an event, and that is not a
        /// second-guessing of the events.</b> <c>S_HIT_CONFIRM</c> reaches only the SHOOTER and
        /// <c>S_DEATH</c> only carries a victim, so a run whose damage was dealt by a bot, by a
        /// fall, or by <c>Vehicle.AutoDamage</c> would show neither. There is no message for a
        /// burn at all — <c>VehicleStateFlags.Burning</c> is a snapshot field, so the snapshot is
        /// the only place it can be seen.
        /// </para>
        /// <para>
        /// <b>Vehicle health of zero is recorded as a burn as well as the flag.</b>
        /// <c>ServerVehicleDamageSink</c> starts the burn clock at zero health and the flag
        /// follows on the next tick, so a client that missed the intermediate snapshot — which
        /// interest management makes ordinary — would see a wreck and never a fire. The evidence
        /// string says which of the two was observed, because they are not the same claim.
        /// </para>
        /// </remarks>
        private void ObserveVerbs()
        {
            WorldSnapshot actors = _router.Decoder.Current;
            uint tick = actors.ServerTick;

            for (int i = 0; i < actors.ActorCount; i++)
            {
                ref ActorSnapshotEntry entry = ref actors.Actors[i];

                if (_actorHealth.TryGetValue(entry.ActorId, out byte was) && entry.Health < was)
                {
                    _verbs.Record(
                        HarnessVerb.Damage, Index, tick, _nowMs,
                        $"actor {entry.ActorId} health {was} -> {entry.Health}");
                }

                _actorHealth[entry.ActorId] = entry.Health;
            }

            VehicleWorldSnapshot vehicles = _router.VehicleDecoder.Current;

            for (int i = 0; i < vehicles.VehicleCount; i++)
            {
                ref VehicleSnapshotEntry entry = ref vehicles.Vehicles[i];

                if (_vehicleHealth.TryGetValue(entry.VehicleId, out byte was) && entry.Health < was)
                {
                    _verbs.Record(
                        HarnessVerb.Damage, Index, tick, _nowMs,
                        $"vehicle {entry.VehicleId} health {was} -> {entry.Health}");
                }

                _vehicleHealth[entry.VehicleId] = entry.Health;

                if ((entry.Flags & VehicleStateFlags.Burning) != 0)
                {
                    _verbs.Record(
                        HarnessVerb.Burn, Index, tick, _nowMs,
                        $"vehicle {entry.VehicleId} flags carry Burning, health {entry.Health}");
                }
                else if (entry.Health == 0 && (entry.Flags & VehicleStateFlags.Dead) == 0)
                {
                    _verbs.Record(
                        HarnessVerb.Burn, Index, tick, _nowMs,
                        $"vehicle {entry.VehicleId} health reached 0 and it is not yet Dead");
                }

                ObserveDrive(in entry, tick);
            }
        }

        /// <summary>
        /// Records <see cref="HarnessVerb.Drive"/> when the seat this client holds has moved.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Moved, not "sat in".</b> A seat grant proves the arbiter answered; it does not
        /// prove a hull went anywhere, and check 11's first verb is <i>drive</i>. So the origin
        /// is latched on the first snapshot after the grant and the verb fires when the decoded
        /// position has travelled further than the quantizer's own step could account for.
        /// </para>
        /// <para>
        /// <b>Seat 0 and at least one vehicle input, because a PASSENGER is not a driver.</b>
        /// Only <c>VehicleInputAuthority.DriverSeatIndex</c> receives a driver input sink, so a
        /// hull that moved while this client sat in seat 2 moved for somebody else's reason.
        /// Measured 2026-08-27 in <c>r5-combat-04</c>: a client seated in a helicopter watched it
        /// climb 139 m and lose 71 health, none of it its own doing.
        /// </para>
        /// <para>
        /// <b>And even seat 0 with input flowing is CORRELATION, not causation, on this build.</b>
        /// Ledger <b>X-46</b>: a player-slot body carries no <c>FpsActorController</c>, so
        /// <c>NetDriverInputSink.Attach</c> returns null and the vehicle never receives what this
        /// client sends. That fact lives in the SERVER's log and no client-side observer can see
        /// it — which is why the evidence string carries the seat and the input count rather than
        /// asserting a drive, and why the phase report grades this verb against the server log
        /// rather than against this line alone.
        /// </para>
        /// </remarks>
        private void ObserveDrive(in VehicleSnapshotEntry entry, uint tick)
        {
            if (_drill == null || _drill.SeatedVehicleId != entry.VehicleId) return;
            if (_drill.SeatedSeatIndex != 0 || VehicleInputsSent == 0) return;

            float x = Quantize.UnpackPos(entry.PosX);
            float z = Quantize.UnpackPos(entry.PosZ);

            if (!_hasSeatedOrigin)
            {
                _seatedOriginX = x;
                _seatedOriginZ = z;
                _hasSeatedOrigin = true;
                return;
            }

            float dx = x - _seatedOriginX;
            float dz = z - _seatedOriginZ;
            float travelled = (float)Math.Sqrt(dx * dx + dz * dz);
            if (travelled < DriveDistanceMetres) return;

            // The seat index and the input count are IN the evidence, because "the vehicle
            // moved" and "this client drove it" are different claims and only the first is
            // observable from here. Ledger X-46: a driver input sink is attached only for seat 0,
            // and only when the body carries a controller it can reach -- so a hull that moved
            // while this client sat in seat 2, or while its inputs reached nothing, moved for
            // some other reason. A line that omitted these would let that read as a drive.
            _verbs.Record(
                HarnessVerb.Drive, Index, tick, _nowMs,
                $"vehicle {entry.VehicleId} moved {travelled:0.0} m while this client held seat "
                + $"{_drill.SeatedSeatIndex} of it, having sent {VehicleInputsSent} vehicle input(s)");
        }

        /// <summary>
        /// How far a driven vehicle must travel before the drive is recorded, in metres.
        /// </summary>
        /// <remarks>
        /// Two metres is thirty-two quantizer steps at the wire's 6.25 cm, so no accumulation of
        /// rounding reaches it, and it is well under one second at any vehicle speed in the game
        /// — a threshold that discriminates a hull that moved from one that was jostled, without
        /// requiring a journey.
        /// </remarks>
        public const float DriveDistanceMetres = 2f;

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
            ObserveVerbs();

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
            _router.OnSpawnActor -= OnSpawnActor;
            _router.OnVehicleSpawn -= OnVehicleSpawn;
            _router.OnSeatChange -= OnSeatChange;
            _router.OnDeath -= OnDeath;
            _router.OnHitConfirm -= OnHitConfirm;
            _transport.OnMessage -= OnMessage;
            _transport.OnConnected -= OnConnected;
            _transport.OnDisconnected -= OnDisconnected;

            _transport.Disconnect();
            _transport.Dispose();
        }
    }
}
