using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-V8 task 6 — the vehicle wire. <c>S_VEHICLE_SPAWN (0x4D)</c> and
    /// <c>S_VEHICLE_DESPAWN (0x4E)</c> shipped with protocol v3 as a codec, a conformance sample
    /// and no sender; these are the tests that a sender exists and emits exactly once.
    /// </summary>
    /// <remarks>
    /// The assertions are deliberately about COUNTS and about the decoded message, not about the
    /// sink returning non-zero. "It gave me an id" is compatible with never framing a byte, and
    /// that is the failure mode a seam with a null default is most likely to have.
    /// </remarks>
    public sealed class VehicleLifecycleWireTests
    {
        // ---------------------------------------------------------------- harness

        /// <summary>Captures framed payloads instead of sending them.</summary>
        private sealed class RecordingSender : IReliablePayloadSender
        {
            public readonly List<byte[]> Payloads = new List<byte[]>();
            public readonly List<byte> Channels = new List<byte>();

            public void BroadcastReliable(ReadOnlySpan<byte> payload, byte channel)
            {
                Payloads.Add(payload.ToArray());
                Channels.Add(channel);
            }
        }

        /// <summary>
        /// A hand-advanced tick, so the id quarantine is testable at all. A real clock would
        /// make "does an id come back too early" a question only a five-second test could ask.
        /// </summary>
        private sealed class Clock
        {
            public uint Tick { get; set; }
            public uint Now() => Tick;
        }

        private static VehicleSpawnReport Report(
            ushort spawnerId = 1, byte typeId = VehicleIds.JEEP, byte seats = 4,
            float x = 10f, float y = 2f, float z = -30f)
            => new VehicleSpawnReport(
                spawnerId, typeId, seats, new Vec3(x, y, z),
                rotationX: 0f, rotationY: 0f, rotationZ: 0f, rotationW: 1f);

        /// <summary>
        /// Pulls the message bodies of one type out of the recorded payloads, through the same
        /// reader a client uses — so a test cannot pass on bytes no decoder would accept.
        /// </summary>
        private static List<ReadOnlyMemory<byte>> BodiesOf(
            RecordingSender sender, ServerMessageType type)
        {
            var bodies = new List<ReadOnlyMemory<byte>>();

            for (int i = 0; i < sender.Payloads.Count; i++)
            {
                var frame = new PayloadFrameReader(sender.Payloads[i]);
                while (frame.TryReadMessage(out byte actual, out ReadOnlySpan<byte> body))
                {
                    if (actual == (byte)type) bodies.Add(body.ToArray());
                }
            }

            return bodies;
        }

        private static void AssertWithin(float expected, float actual, float tolerance)
            => Assert.True(
                Math.Abs(expected - actual) <= tolerance,
                $"expected {expected} within {tolerance}, got {actual}");

        // ---------------------------------------------------------------- spawn

        [Fact]
        public void ASpawnProducesExactlyOneVehicleSpawnMessage()
        {
            var sender = new RecordingSender();
            var clock  = new Clock();
            var sink   = new ServerVehicleLifecycleSink(sender, clock.Now);

            ushort id = sink.OnVehicleSpawned(Report());

            Assert.NotEqual(0, id);
            Assert.Single(BodiesOf(sender, ServerMessageType.VehicleSpawn));
            Assert.Equal(1, sink.SpawnsSent);
        }

        [Fact]
        public void TheSpawnMessageCarriesTheKindDerivedFromTheAuthoredId()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            sink.OnVehicleSpawned(Report(typeId: VehicleIds.HELICOPTER, seats: 2));

            List<ReadOnlyMemory<byte>> bodies = BodiesOf(sender, ServerMessageType.VehicleSpawn);
            Assert.True(VehicleSpawnMessage.TryParse(bodies[0].Span, out VehicleSpawnMessage spawn));

            // The two type fields are independent on the wire and must BOTH be right: the id
            // picks the prefab, the kind picks how the snapshot tail is read.
            Assert.Equal(VehicleIds.HELICOPTER, spawn.NetworkTypeId);
            Assert.Equal(VehicleKind.Helicopter, spawn.Kind);
            Assert.Equal(2, spawn.SeatCount);
        }

        [Fact]
        public void TheSpawnMessageRoundTripsThePadPosition()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            sink.OnVehicleSpawned(Report(x: 123.5f, y: 4.25f, z: -67.75f));

            Assert.True(VehicleSpawnMessage.TryParse(
                BodiesOf(sender, ServerMessageType.VehicleSpawn)[0].Span,
                out VehicleSpawnMessage spawn));

            // Quantized, so this is a tolerance and not an equality — and the tolerance is
            // derived from the quantizer rather than picked, so it stays honest if POS_RANGE
            // ever moves. The failure this guards is not a lost centimetre; it is a spawn that
            // arrives at the origin because a conversion was forgotten.
            const float step = Quantize.POS_RANGE / 65535f;

            AssertWithin(123.5f, Quantize.UnpackPos(spawn.PosX), step);
            AssertWithin(4.25f, Quantize.UnpackPos(spawn.PosY), step);
            AssertWithin(-67.75f, Quantize.UnpackPos(spawn.PosZ), step);
        }

        [Fact]
        public void SpawnAndDespawnGoOnTheReliableChannel()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            ushort id = sink.OnVehicleSpawned(Report());
            sink.OnVehicleDespawned(id, VehicleDespawnReason.Destroyed);

            Assert.Equal(2, sender.Channels.Count);
            Assert.All(
                sender.Channels,
                channel => Assert.Equal((byte)ServerEventWriter.ReliableChannel, channel));
        }

        [Fact]
        public void AnUnauthoredPrefabIsRefusedAndNothingGoesOnTheWire()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            ushort id = sink.OnVehicleSpawned(Report(typeId: VehicleIds.NONE));

            Assert.Equal(0, id);
            Assert.Empty(sender.Payloads);
            Assert.Equal(1, sink.UnauthoredPrefabCount);
        }

        [Fact]
        public void AnIdThisBuildDoesNotKnowIsRefusedRatherThanSentAsSomethingElse()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            ushort id = sink.OnVehicleSpawned(Report(typeId: (byte)(VehicleIds.MAX_ASSIGNED + 1)));

            Assert.Equal(0, id);
            Assert.Empty(sender.Payloads);
            Assert.Equal(1, sink.UnauthoredPrefabCount);
        }

        // ---------------------------------------------------------------- despawn

        [Fact]
        public void ADeathProducesExactlyOneVehicleDespawnMessage()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            ushort id = sink.OnVehicleSpawned(Report());
            sink.OnVehicleDespawned(id, VehicleDespawnReason.Destroyed);

            List<ReadOnlyMemory<byte>> bodies = BodiesOf(sender, ServerMessageType.VehicleDespawn);
            Assert.Single(bodies);

            Assert.True(VehicleDespawnMessage.TryParse(bodies[0].Span, out VehicleDespawnMessage despawn));
            Assert.Equal(id, despawn.VehicleId);
            Assert.Equal(VehicleDespawnReason.Destroyed, despawn.Reason);
        }

        [Fact]
        public void ASecondDespawnForTheSameVehicleSendsNothing()
        {
            // VehicleSpawner reports a death, and a world reset arriving for the same wreck
            // reports it again. A client that removes a vehicle twice removes its replacement.
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            ushort id = sink.OnVehicleSpawned(Report());
            sink.OnVehicleDespawned(id, VehicleDespawnReason.Destroyed);
            sink.OnVehicleDespawned(id, VehicleDespawnReason.WorldReset);

            Assert.Single(BodiesOf(sender, ServerMessageType.VehicleDespawn));
            Assert.Equal(1, sink.DespawnsSent);
        }

        [Fact]
        public void DespawningIdZeroSendsNothing()
        {
            // Id 0 is what an offline build, a client, and an unauthored prefab all hold. It is
            // also the protocol's "no vehicle" — sending it would name nothing.
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            sink.OnVehicleDespawned(0, VehicleDespawnReason.Destroyed);

            Assert.Empty(sender.Payloads);
            Assert.Equal(0, sink.DespawnsSent);
        }

        [Fact]
        public void AWorldResetDespawnsEveryLiveVehicleExactlyOnce()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(sender, new Clock().Now);

            var live = new List<ushort>();
            for (ushort spawner = 1; spawner <= 14; spawner++)
                live.Add(sink.OnVehicleSpawned(Report(spawnerId: spawner)));

            for (int i = 0; i < live.Count; i++)
                sink.OnVehicleDespawned(live[i], VehicleDespawnReason.WorldReset);

            Assert.Equal(14, BodiesOf(sender, ServerMessageType.VehicleDespawn).Count);
            Assert.Equal(14, sink.DespawnsSent);
            Assert.Equal(0, sink.Ids.InUseCount);
        }

        // ---------------------------------------------------------------- the null sink

        [Fact]
        public void TheNullSinkAssignsNoIdAndIsWhatOfflineAndClientBuildsGet()
        {
            // The role promise the rest of phase-V8 made about capture points: the spawner's
            // code path is identical everywhere, and off the server it simply gets 0 back.
            Assert.Equal(0, NullVehicleLifecycleSink.Instance.OnVehicleSpawned(Report()));

            NullVehicleLifecycleSink.Instance.OnVehicleDespawned(7, VehicleDespawnReason.Destroyed);
        }

        // ---------------------------------------------------------------- the id pool

        [Fact]
        public void ARetiredIdIsNotReissuedUntilItsQuarantineExpires()
        {
            var pool = new VehicleIdPool(capacity: 1, quarantineTicks: 150);

            Assert.True(pool.TryAcquire(0, out ushort first));
            pool.Release(first, nowTick: 0);

            Assert.False(pool.TryAcquire(149, out _));

            Assert.True(pool.TryAcquire(150, out ushort second));
            Assert.Equal(first, second);
        }

        [Fact]
        public void TheDefaultPoolHoldsExactlyMaxVehiclesIds()
        {
            // 16 is what the vehicle snapshot body is sized against, and the shipped maps author
            // 14 spawners — so this ceiling is real headroom, not a guess.
            var pool = new VehicleIdPool();

            for (int i = 0; i < ProtocolConstants.MAX_VEHICLES; i++)
                Assert.True(pool.TryAcquire(0, out _));

            Assert.False(pool.TryAcquire(0, out _));
            Assert.Equal(ProtocolConstants.MAX_VEHICLES, pool.InUseCount);
        }

        [Fact]
        public void AnExhaustedPoolRefusesTheSpawnAndCountsIt()
        {
            var sender = new RecordingSender();
            var sink   = new ServerVehicleLifecycleSink(
                sender, new Clock().Now, new VehicleIdPool(capacity: 2));

            Assert.NotEqual(0, sink.OnVehicleSpawned(Report()));
            Assert.NotEqual(0, sink.OnVehicleSpawned(Report()));
            Assert.Equal(0, sink.OnVehicleSpawned(Report()));

            Assert.Equal(1, sink.IdExhaustedCount);
            Assert.Equal(2, BodiesOf(sender, ServerMessageType.VehicleSpawn).Count);
        }

        [Fact]
        public void ReleasingAnIdThatIsNotInUseDoesNotConsumeAQuarantineSlot()
        {
            var pool = new VehicleIdPool(capacity: 1, quarantineTicks: 150);

            Assert.True(pool.TryAcquire(0, out ushort id));
            pool.Release(id, nowTick: 0);
            pool.Release(id, nowTick: 0);

            Assert.Equal(1, pool.QuarantinedCount);
            Assert.True(pool.TryAcquire(150, out _));
        }

        [Fact]
        public void ReleaseAllReturnsLiveAndCoolingIdsForTheNextBind()
        {
            var pool = new VehicleIdPool(capacity: 2, quarantineTicks: 150);

            Assert.True(pool.TryAcquire(0, out ushort live));
            Assert.True(pool.TryAcquire(0, out ushort retired));
            pool.Release(retired, nowTick: 0);

            pool.ReleaseAll();

            Assert.Equal(0, pool.InUseCount);
            Assert.Equal(0, pool.QuarantinedCount);
            Assert.Equal(2, pool.FreeCount);
            Assert.False(pool.IsInUse(live));
        }
    }
}
