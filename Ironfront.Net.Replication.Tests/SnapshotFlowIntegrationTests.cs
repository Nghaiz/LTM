using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Loopback;
using Ironfront.Net.Transport.Simulation;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-01 Task 5 step 1: the server, the transport track's LoopbackTransport and a fake client, wired
    /// end to end — input goes up, snapshots come down, the client's world converges on the
    /// server's, over an impaired network.
    /// </summary>
    /// <remarks>
    /// This is the closest thing to the two-Unity-client milestone that can exist outside the
    /// Editor. Everything the real client does with a snapshot happens here except drawing
    /// it: real framing, real quantization, real delta baselines, real packet loss.
    /// </remarks>
    public sealed class SnapshotFlowIntegrationTests
    {
        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        [Theory]
        [InlineData("lan")]
        [InlineData("typical")]
        [InlineData("bad")]
        public void InputFlowsUpAndSnapshotsFlowDownOverAnImpairedNetwork(string preset)
        {
            SimulatorConfig config = SimulatorConfig.FromPresetName(preset);
            config.RandomSeed = 2026;

            var harness = new Harness(config, actorCount: 24);
            harness.Run(ticks: 900);

            // 1. The player actually moved under server authority.
            Assert.True(
                harness.PlayerSession.State.Position.Z > 5f,
                $"the player only reached z={harness.PlayerSession.State.Position.Z:F2} — input is not arriving");

            // 2. Input arrived and was applied, with the redundant copies discarded.
            Assert.True(harness.PlayerSession.LastProcessedInputTick > 0);
            Assert.Equal(0, harness.PlayerSession.SpeedViolations);

            // 3. Snapshots arrived and the delta loop engaged rather than sending full every
            //    time, which is what proves the ack path works end to end.
            Assert.True(harness.ClientDecoder.AppliedCount > 100);
            Assert.True(
                harness.PlayerSession.Encoder.DeltaSnapshotCount > harness.PlayerSession.Encoder.FullSnapshotCount,
                "the server fell back to full snapshots more often than it sent deltas");

            // Named separately from the delta count so a failure says WHICH half broke. The
            // delta assertion above already cannot pass with a dead sender, but it reports
            // "the server fell back to full snapshots", which points at the encoder and not at
            // the client that stopped acking.
            Assert.True(
                harness.ClientAck.AcksSent > 0,
                "the client's BaselineAckPolicy produced no acks at all");

            // 4. The client's world matches the server's, exactly, at the quantized level.
            harness.FlushUntilConverged();
            harness.AssertClientMatchesServer();
        }

        [Fact]
        public void TheServerFallsBackToFullSnapshotsWhenAcksStopArriving()
        {
            // Not a total blackout — snapshots keep flowing, the acks do not. That is the
            // asymmetric failure the baseline scheme has to survive: the server's baseline
            // ages out, so it must notice and resend a full snapshot rather than keep
            // emitting deltas against a tick it can no longer prove the client holds.
            SimulatorConfig config = SimulatorConfig.Lan();
            config.RandomSeed = 3;

            var harness = new Harness(config, actorCount: 8);
            harness.Run(ticks: 120);
            harness.AssertClientMatchesServer();

            // Pull the plug for far longer than the baseline history.
            harness.NetworkDown = true;
            harness.Run(ticks: 300);
            harness.NetworkDown = false;

            harness.Run(ticks: 120);
            harness.FlushUntilConverged();
            harness.AssertClientMatchesServer();
        }

        [Fact]
        public void ASpeedHackingClientIsClampedByTheServer()
        {
            // The fake client sends the maximum on both axes every tick, which is only
            // producible by a modified client. The server must move it no faster than an
            // honest sprint.
            SimulatorConfig config = SimulatorConfig.Lan();
            config.RandomSeed = 8;

            var honest = new Harness(config, actorCount: 1) { CheatDiagonal = false };
            var cheat  = new Harness(config, actorCount: 1) { CheatDiagonal = true };

            honest.Run(ticks: 300);
            cheat.Run(ticks: 300);

            float honestDistance = honest.PlayerSession.State.Position.Magnitude;
            float cheatDistance  = cheat.PlayerSession.State.Position.Magnitude;

            Assert.True(
                cheatDistance <= honestDistance * 1.05f,
                $"the cheating client covered {cheatDistance:F1} m against an honest {honestDistance:F1} m");
        }

        // ==================================================================== harness

        private sealed class Harness
        {
            private readonly LoopbackTransport _wire;
            private readonly ServerTickScheduler _scheduler = new ServerTickScheduler();
            private readonly WorldSnapshot _world = new WorldSnapshot();
            private readonly ServerMessageRouter _router = new ServerMessageRouter();

            // Sized to the body budget, not to MAX_PAYLOAD. A body allowed to fill the whole
            // datagram leaves no room for the 6 bytes of framing around it, and the write would
            // fail at the last step with the snapshot already filed as a baseline candidate.
            private readonly byte[] _snapshotBody = new byte[ServerPayloadWriter.MaxSnapshotBodySize];
            private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            private readonly byte[] _inputBody = new byte[64];
            private readonly InputFrame[] _recentInput = new InputFrame[ProtocolConstants.INPUT_REDUNDANCY];

            private readonly int _actorCount;
            private uint _clientInputTick;

            public Harness(SimulatorConfig config, int actorCount)
            {
                _actorCount = actorCount;
                _wire = new LoopbackTransport(config);

                PlayerSession = new ClientSession(LoopbackTransport.ConnectionId, actorId: 1);
                PlayerSession.State = MoveState.AtRest(Vec3.Zero, grounded: true);

                ClientDecoder = new DeltaDecoder();

                // The SHIPPED sender, not a copy of it. This harness used to hand-roll the ack
                // beside the one the Unity client would eventually send, which meant the whole
                // delta path below was exercised against a second implementation that could
                // drift from the real one without anything noticing — and for four phases there
                // was no real one to drift from (debt row X-3). Driving BaselineAckPolicy here
                // makes this suite a full-loop test of the actual client behaviour.
                ClientAck = new BaselineAckPolicy();

                _wire.Server.OnMessage += OnServerMessage;
                _wire.Client.OnMessage += OnClientMessage;

                _wire.Server.Start(0, 4);
                _wire.Client.Connect("loopback", 0, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
                _wire.Step(1.0);

                for (int i = 0; i < actorCount; i++)
                {
                    _world.Add(SnapshotBuilder.Capture(
                        actorId: (ushort)(i + 1),
                        position: new Vec3(i * 3f, 0f, 0f),
                        yawDegrees: 0f,
                        pitchDegrees: 0f,
                        velocity: Vec3.Zero,
                        stateFlags: ActorStateFlags.IsAlive,
                        health: 100f,
                        weaponId: 1,
                        ammoInClip: 30,
                        team: (byte)(i % 2)));
                }
            }

            public ClientSession PlayerSession { get; }
            public DeltaDecoder ClientDecoder { get; }

            /// <summary>The shipped client-side ack sender this harness drives.</summary>
            public BaselineAckPolicy ClientAck { get; }

            /// <summary>Drops everything the client tries to send, simulating a dead link.</summary>
            public bool NetworkDown { get; set; }

            /// <summary>Makes the fake client send an out-of-range diagonal every tick.</summary>
            public bool CheatDiagonal { get; set; }

            public void Run(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    SendClientInput();

                    _scheduler.BeginTick();
                    ApplyServerTick();

                    if (_scheduler.ShouldSendSnapshot()) SendSnapshot();

                    _wire.Step(_scheduler.MsPerTick);
                }
            }

            /// <summary>
            /// Runs a few quiet ticks with a clean link so anything still in flight lands.
            /// Comparing worlds while packets are queued would be measuring the simulator's
            /// latency, not the encoder's correctness.
            /// </summary>
            public void FlushUntilConverged()
            {
                bool wasDown = NetworkDown;
                NetworkDown = false;

                for (int i = 0; i < 40; i++)
                {
                    _scheduler.BeginTick();
                    SendSnapshot();
                    _wire.Step(_scheduler.MsPerTick);
                }

                NetworkDown = wasDown;
            }

            private void SendClientInput()
            {
                if (NetworkDown) return;

                _clientInputTick++;

                float moveX = CheatDiagonal ? 1f : 0f;
                float moveZ = 1f;

                InputFrame frame = CheatDiagonal
                    // Bypasses Quantize's clamp deliberately: a modified client writes the
                    // raw i8 maximum on both axes, which is the exploit being tested.
                    ? new InputFrame(127, 127, Quantize.PackYaw(0f), 0, InputButtons.Sprint)
                    : InputFrame.FromFloats(moveX, moveZ, 0f, 0f, InputButtons.Sprint);

                // Redundancy: repeat the 3 most recent frames (spec § 4.2).
                for (int i = _recentInput.Length - 1; i > 0; i--) _recentInput[i] = _recentInput[i - 1];
                _recentInput[0] = frame;

                int count = (int)Math.Min(_clientInputTick, (uint)_recentInput.Length);
                uint startTick = _clientInputTick - (uint)count + 1;

                Span<InputFrame> frames = stackalloc InputFrame[ProtocolConstants.INPUT_REDUNDANCY];
                for (int i = 0; i < count; i++) frames[i] = _recentInput[count - 1 - i];

                int bodyLength = ClientInputMessage.Write(_inputBody, startTick, frames.Slice(0, count));
                if (bodyLength < 0) return;

                var writer = new PayloadFrameWriter(_payload, ChannelId.InputSequenced);
                writer.WriteMessage(ClientMessageType.Input, new ReadOnlySpan<byte>(_inputBody, 0, bodyLength));
                if (!writer.TryFinish(out int total)) return;

                _wire.Client.Send(
                    (byte)ChannelId.InputSequenced, new ReadOnlySpan<byte>(_payload, 0, total), reliable: false);
            }

            private void ApplyServerTick()
            {
                InputAuthority.ApplyPendingInput(
                    PlayerSession, Dt, motion => PlayerSession.State.Position + motion);

                // Mirror the authoritative player state into the replicated world.
                int index = _world.IndexOf(PlayerSession.ActorId);
                if (index < 0) return;

                _world.Actors[index] = SnapshotBuilder.Capture(
                    PlayerSession.ActorId,
                    PlayerSession.State.Position,
                    yawDegrees: 0f,
                    pitchDegrees: 0f,
                    velocity: PlayerSession.State.Velocity,
                    stateFlags: ActorStateFlags.IsAlive | ActorStateFlags.IsSprinting,
                    health: 100f,
                    weaponId: 1,
                    ammoInClip: 30,
                    team: 0);
            }

            private void SendSnapshot()
            {
                _world.ServerTick = _scheduler.CurrentTick;

                // The same call the Unity ServerTickLoop makes, so this scenario exercises the
                // shipped encode-and-frame path rather than a copy of it that could drift.
                int total = ServerPayloadWriter.WriteSnapshot(
                    _payload,
                    _snapshotBody,
                    PlayerSession.Encoder,
                    _world,
                    PlayerSession.LastProcessedInputTick);

                if (total < 0) return;

                _wire.Server.Send(
                    LoopbackTransport.ConnectionId,
                    (byte)ChannelId.SnapshotSequenced,
                    new ReadOnlySpan<byte>(_payload, 0, total),
                    reliable: false);
            }

            private void OnServerMessage(ushort connectionId, ReadOnlyMemory<byte> payload)
                => _router.Route(payload.Span, PlayerSession);

            private void OnClientMessage(ReadOnlyMemory<byte> payload)
            {
                var reader = new PayloadFrameReader(payload.Span);

                while (reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body))
                {
                    if ((ServerMessageType)msgType != ServerMessageType.Snapshot) continue;

                    if (ClientDecoder.Read(body) != SnapshotReadResult.Applied) continue;
                    if (NetworkDown) continue;

                    if (!ClientAck.TryBuildAck(ClientDecoder.AckTick, out ReadOnlySpan<byte> ack))
                        continue;

                    _wire.Client.Send((byte)BaselineAckPolicy.Channel, ack, reliable: true);
                }
            }

            public void AssertClientMatchesServer()
            {
                WorldSnapshot client = ClientDecoder.Current;

                Assert.Equal(_actorCount, client.ActorCount);

                for (int i = 0; i < _world.ActorCount; i++)
                {
                    ActorSnapshotEntry want = _world.Actors[i];
                    Assert.True(
                        client.TryFind(want.ActorId, out ActorSnapshotEntry got),
                        $"actor {want.ActorId} is missing from the client's world");

                    Assert.Equal(want.PosX, got.PosX);
                    Assert.Equal(want.PosY, got.PosY);
                    Assert.Equal(want.PosZ, got.PosZ);
                    Assert.Equal(want.Yaw, got.Yaw);
                    Assert.Equal(want.Health, got.Health);
                    Assert.Equal(want.StateFlags, got.StateFlags);
                }
            }
        }
    }
}
