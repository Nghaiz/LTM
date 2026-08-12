using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-01 acceptance criteria 2 (full snapshots round-trip), 3 (deltas survive 20%
    /// packet loss) and 4 (deltas save at least 35%).
    /// </summary>
    public sealed class SnapshotAndDeltaTests
    {
        // ------------------------------------------------------------------ criterion 2

        [Fact]
        public void FullSnapshotRoundTripsEveryField()
        {
            WorldSnapshot world = BuildWorld(actorCount: 48, seed: 1);
            world.ServerTick = 100;

            Span<byte> buffer = stackalloc byte[4096];
            int written = SnapshotBuilder.WriteFull(buffer, world, lastProcessedInputTick: 77);
            Assert.True(written > 0);

            var decoder = new DeltaDecoder();
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.Slice(0, written)));

            Assert.Equal(100u, decoder.Current.ServerTick);
            Assert.Equal(77u, decoder.LastProcessedInputTick);
            AssertWorldsIdentical(world, decoder.Current);
        }

        [Fact]
        public void FullSnapshotSizeMatchesTheSpecBudget()
        {
            // protocol-spec.md § 4.3: 20 bytes per actor with every v1 field, plus a 13-byte
            // header. The 64-actor case is 1293 bytes, past the 1184-byte payload limit, so
            // the join snapshot is expected to fragment.
            Assert.Equal(20, SnapshotMessage.EntrySize(SnapshotField.FullNoSeat));
            Assert.Equal(13 + 48 * 20, SnapshotBuilder.FullSizeFor(48));
            Assert.True(SnapshotBuilder.FullSizeFor(64) > ProtocolConstants.MAX_PAYLOAD);
        }

        [Fact]
        public void HealthIsClampedRatherThanWrapped()
        {
            // A medkit overshoot or an overkill hit must not hand the client 250 HP or 0xFF.
            Assert.Equal(0, SnapshotBuilder.ClampHealth(-40f));
            Assert.Equal(100, SnapshotBuilder.ClampHealth(130f));
            Assert.Equal(100, SnapshotBuilder.ClampHealth(100f));
            Assert.Equal(57, SnapshotBuilder.ClampHealth(56.7f));
        }

        // ------------------------------------------------------------------ change masks

        [Fact]
        public void AStationaryActorProducesAnEmptyChangeMask()
        {
            // Trap 4. Entries are stored quantized, so sub-6.25 cm physics jitter cannot set
            // the Position bit. This is the test that would fail if WorldSnapshot ever
            // started holding floats.
            ActorSnapshotEntry first = SnapshotBuilder.Capture(
                7, new Vec3(10f, 0f, 10f), 90f, 0f, Vec3.Zero,
                ActorStateFlags.IsAlive, 100f, 1, 30, 0);

            ActorSnapshotEntry jittered = SnapshotBuilder.Capture(
                7, new Vec3(10.0001f, 0.0001f, 9.9999f), 90.0001f, 0f, Vec3.Zero,
                ActorStateFlags.IsAlive, 100f, 1, 30, 0);

            Assert.Equal(SnapshotField.None, DeltaEncoder.ComputeChangeMask(in first, in jittered));
            Assert.Equal(3, SnapshotMessage.EntrySize(SnapshotField.None));
        }

        [Fact]
        public void RealMovementSetsExactlyThePositionBit()
        {
            ActorSnapshotEntry before = SnapshotBuilder.Capture(
                7, new Vec3(10f, 0f, 10f), 90f, 0f, Vec3.Zero,
                ActorStateFlags.IsAlive, 100f, 1, 30, 0);

            ActorSnapshotEntry after = SnapshotBuilder.Capture(
                7, new Vec3(11f, 0f, 10f), 90f, 0f, Vec3.Zero,
                ActorStateFlags.IsAlive, 100f, 1, 30, 0);

            Assert.Equal(SnapshotField.Position, DeltaEncoder.ComputeChangeMask(in before, in after));
        }

        [Fact]
        public void UnchangedFieldsAreInheritedFromTheBaseline()
        {
            // Trap 5: a delta that omits a field means "unchanged", not "zero".
            ActorSnapshotEntry baseline = SnapshotBuilder.Capture(
                7, new Vec3(10f, 5f, 10f), 90f, 10f, new Vec3(1f, 0f, 2f),
                ActorStateFlags.IsAlive | ActorStateFlags.IsSprinting, 83f, 3, 24, 1);

            var incoming = new ActorSnapshotEntry
            {
                ActorId    = 7,
                ChangeMask = SnapshotField.Health,
                Health     = 40,
            };

            ActorSnapshotEntry merged = DeltaDecoder.ApplyEntry(in baseline, in incoming);

            Assert.Equal(40, merged.Health);
            Assert.Equal(baseline.PosX, merged.PosX);
            Assert.Equal(baseline.PosY, merged.PosY);
            Assert.Equal(baseline.PosZ, merged.PosZ);
            Assert.Equal(baseline.Yaw, merged.Yaw);
            Assert.Equal(baseline.VelX, merged.VelX);
            Assert.Equal(baseline.StateFlags, merged.StateFlags);
            Assert.Equal(baseline.WeaponId, merged.WeaponId);
            Assert.Equal(baseline.Team, merged.Team);
        }

        // ------------------------------------------------------------------ criterion 3

        [Theory]
        [InlineData(42)]
        [InlineData(1337)]
        [InlineData(20260812)]
        [InlineData(7)]
        public void DeltasSurvive20PercentPacketLoss(int seed)
        {
            // The test that catches almost every delta bug. Run with several seeds: one seed
            // proves one loss pattern, not the property.
            var rng     = new Random(seed);
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();

            WorldSnapshot world = BuildWorld(actorCount: 48, seed: seed);
            var buffer = new byte[8192];

            for (uint tick = 1; tick <= 1000; tick++)
            {
                MutateWorld(world, rng);
                world.ServerTick = tick;

                int written = encoder.Write(buffer, world, lastProcessedInputTick: tick);
                Assert.True(written > 0, $"encode failed at tick {tick}");

                if (rng.NextDouble() > 0.20)
                {
                    SnapshotReadResult result = decoder.Read(new ReadOnlySpan<byte>(buffer, 0, written));

                    // UnknownBaseline is a legal outcome: the client acked a tick that has
                    // since aged out. It must self-heal on the next full snapshot, which the
                    // final assertion proves.
                    Assert.True(
                        result == SnapshotReadResult.Applied || result == SnapshotReadResult.UnknownBaseline,
                        $"tick {tick} decoded as {result}");

                    if (result == SnapshotReadResult.Applied) encoder.OnClientAck(tick);
                }
                // The other 20%: no ack, so the server keeps deltaing against an older
                // baseline until it ages out and it falls back to a full snapshot.
            }

            // Deliver one final snapshot unconditionally, so the comparison is not simply
            // measuring whether the last packet happened to be dropped.
            world.ServerTick = 1001;
            int finalBytes = encoder.Write(buffer, world, lastProcessedInputTick: 1001);
            Assert.Equal(
                SnapshotReadResult.Applied,
                decoder.Read(new ReadOnlySpan<byte>(buffer, 0, finalBytes)));

            // Quantized entries, so this is exact equality, not a tolerance. Any baseline
            // drift at all shows up here.
            AssertWorldsIdentical(world, decoder.Current);
        }

        [Fact]
        public void ARecoveredClientIsNeverLeftUndecodable()
        {
            // A long blackout: the client hears nothing for far longer than the baseline
            // history, so the server must fall back to a full snapshot rather than emitting
            // deltas nobody can apply.
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            WorldSnapshot world = BuildWorld(actorCount: 12, seed: 5);
            var rng = new Random(5);
            var buffer = new byte[4096];

            world.ServerTick = 1;
            int n = encoder.Write(buffer, world, 1);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(new ReadOnlySpan<byte>(buffer, 0, n)));
            encoder.OnClientAck(1);

            // 200 ticks into the void.
            for (uint tick = 2; tick <= 200; tick++)
            {
                MutateWorld(world, rng);
                world.ServerTick = tick;
                encoder.Write(buffer, world, tick);
            }

            world.ServerTick = 201;
            n = encoder.Write(buffer, world, 201);

            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(new ReadOnlySpan<byte>(buffer, 0, n)));
            AssertWorldsIdentical(world, decoder.Current);
        }

        [Fact]
        public void AnAckCannotMoveTheBaselineBackwards()
        {
            var encoder = new DeltaEncoder();

            encoder.OnClientAck(500);
            encoder.OnClientAck(400);

            Assert.Equal(500u, encoder.AckedBaselineTick);
        }

        [Fact]
        public void AStaleSnapshotIsDroppedRatherThanApplied()
        {
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            WorldSnapshot world = BuildWorld(actorCount: 4, seed: 3);
            var buffer = new byte[2048];

            world.ServerTick = 10;
            int first = encoder.Write(buffer, world, 10);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(new ReadOnlySpan<byte>(buffer, 0, first)));

            var older = new byte[2048];
            world.ServerTick = 9;
            int second = encoder.Write(older, world, 9);

            Assert.Equal(SnapshotReadResult.Stale, decoder.Read(new ReadOnlySpan<byte>(older, 0, second)));
            Assert.Equal(10u, decoder.Current.ServerTick);
        }

        [Fact]
        public void AMalformedBodyIsRejectedWithoutThrowing()
        {
            var decoder = new DeltaDecoder();

            Assert.Equal(SnapshotReadResult.Malformed, decoder.Read(ReadOnlySpan<byte>.Empty));
            Assert.Equal(SnapshotReadResult.Malformed, decoder.Read(new byte[] { 1, 2, 3 }));

            // A header claiming 40 actors with no bodies behind it.
            var truncated = new byte[SnapshotHeader.Size];
            truncated[12] = 40;
            Assert.Equal(SnapshotReadResult.Malformed, decoder.Read(truncated));
        }

        // ------------------------------------------------------------------ criterion 4

        [Fact]
        public void DeltasSaveAtLeast35PercentAgainstFullSnapshots()
        {
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            var rng     = new Random(2024);

            WorldSnapshot world = BuildWorld(actorCount: 48, seed: 2024);
            var buffer = new byte[8192];

            long deltaBytes = 0;
            long fullBytes  = 0;
            int measured    = 0;

            for (uint tick = 1; tick <= 400; tick++)
            {
                MutateWorld(world, rng);
                world.ServerTick = tick;

                int written = encoder.Write(buffer, world, tick);
                Assert.True(written > 0);

                decoder.Read(new ReadOnlySpan<byte>(buffer, 0, written));
                encoder.OnClientAck(tick);

                // Skip the warm-up, where there is no baseline yet and everything is full by
                // definition. Measuring those in would flatter nothing — it would just make
                // the number meaningless.
                if (tick <= 5) continue;

                deltaBytes += written;
                fullBytes  += SnapshotBuilder.FullSizeFor(world.ActorCount);
                measured++;
            }

            Assert.True(measured > 300);

            double saving = 1.0 - (double)deltaBytes / fullBytes;
            Assert.True(
                saving >= 0.35,
                $"delta saving was {saving:P1} ({deltaBytes} B vs {fullBytes} B full) — " +
                "phase-01 criterion 4 requires at least 35%");
        }

        [Fact]
        public void BandwidthPerClientStaysUnderTheBudget()
        {
            // Phase-01 criterion 8: at most 12 KB/s per client at 48 actors and 20 Hz,
            // before interest management.
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            var rng     = new Random(99);

            WorldSnapshot world = BuildWorld(actorCount: 48, seed: 99);
            var buffer = new byte[8192];

            long bytes = 0;
            const int snapshots = ProtocolConstants.SNAPSHOT_RATE * 10; // 10 seconds

            for (uint tick = 1; tick <= snapshots; tick++)
            {
                MutateWorld(world, rng);
                world.ServerTick = tick;

                int written = encoder.Write(buffer, world, tick);
                decoder.Read(new ReadOnlySpan<byte>(buffer, 0, written));
                encoder.OnClientAck(tick);

                // Count the GSP header and the payload framing too — the budget is bytes on
                // the wire, not bytes of snapshot body.
                bytes += written + ProtocolConstants.GSP_HEADER_SIZE
                       + PayloadFrame.HeaderSize + PayloadFrame.MessageHeaderSize;
            }

            double bytesPerSecond = bytes / 10.0;
            Assert.True(
                bytesPerSecond <= 12 * 1024,
                $"{bytesPerSecond / 1024.0:F2} KB/s per client exceeds the 12 KB/s budget");
        }

        // ------------------------------------------------------------------ helpers

        private static WorldSnapshot BuildWorld(int actorCount, int seed)
        {
            var rng = new Random(seed);
            var world = new WorldSnapshot();

            for (int i = 0; i < actorCount; i++)
            {
                world.Add(SnapshotBuilder.Capture(
                    actorId: (ushort)(i + 1),
                    position: new Vec3(
                        (float)(rng.NextDouble() * 400.0 - 200.0),
                        (float)(rng.NextDouble() * 20.0),
                        (float)(rng.NextDouble() * 400.0 - 200.0)),
                    yawDegrees: (float)(rng.NextDouble() * 360.0),
                    pitchDegrees: (float)(rng.NextDouble() * 60.0 - 30.0),
                    velocity: Vec3.Zero,
                    stateFlags: ActorStateFlags.IsAlive,
                    health: 100f,
                    weaponId: (byte)rng.Next(0, 8),
                    ammoInClip: (byte)rng.Next(0, 31),
                    team: (byte)(i % 2)));
            }

            return world;
        }

        /// <summary>
        /// Moves the world the way a real mid-game tick does.
        /// </summary>
        /// <remarks>
        /// The mix matters for the bandwidth numbers, so it is modelled rather than
        /// randomized uniformly: most actors are running in a straight line (position and
        /// rotation change, quantized velocity does not), a fifth are manoeuvring, and a
        /// fifth are standing still. Health, weapon and team change rarely, which is what
        /// makes deltas worth having.
        /// </remarks>
        private static void MutateWorld(WorldSnapshot world, Random rng)
        {
            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry actor = ref world.Actors[i];

                int behaviour = i % 5;

                if (behaviour == 4) continue; // standing still

                // Running: position advances by roughly one tick of movement.
                actor.PosX = (short)Math.Clamp(actor.PosX + rng.Next(-40, 41), short.MinValue, short.MaxValue);
                actor.PosZ = (short)Math.Clamp(actor.PosZ + rng.Next(-40, 41), short.MinValue, short.MaxValue);
                actor.Yaw  = (ushort)((actor.Yaw + rng.Next(0, 400)) & 0xFFFF);

                if (behaviour == 0)
                {
                    // Manoeuvring: velocity and pitch move too.
                    actor.VelX  = (sbyte)Math.Clamp(actor.VelX + rng.Next(-3, 4), sbyte.MinValue, sbyte.MaxValue);
                    actor.VelZ  = (sbyte)Math.Clamp(actor.VelZ + rng.Next(-3, 4), sbyte.MinValue, sbyte.MaxValue);
                    actor.Pitch = (sbyte)Math.Clamp(actor.Pitch + rng.Next(-2, 3), sbyte.MinValue, sbyte.MaxValue);
                }

                if (rng.NextDouble() < 0.01 && actor.Health > 0)
                    actor.Health = (byte)Math.Max(0, actor.Health - rng.Next(1, 25));

                if (rng.NextDouble() < 0.005)
                    actor.AmmoInClip = (byte)Math.Max(0, actor.AmmoInClip - 1);

                if (rng.NextDouble() < 0.002)
                    actor.StateFlags ^= ActorStateFlags.IsSprinting;
            }
        }

        private static void AssertWorldsIdentical(WorldSnapshot expected, WorldSnapshot actual)
        {
            Assert.Equal(expected.ActorCount, actual.ActorCount);

            for (int i = 0; i < expected.ActorCount; i++)
            {
                ActorSnapshotEntry want = expected.Actors[i];
                Assert.True(
                    actual.TryFind(want.ActorId, out ActorSnapshotEntry got),
                    $"actor {want.ActorId} missing from the client's world");

                Assert.Equal(want.PosX, got.PosX);
                Assert.Equal(want.PosY, got.PosY);
                Assert.Equal(want.PosZ, got.PosZ);
                Assert.Equal(want.Yaw, got.Yaw);
                Assert.Equal(want.Pitch, got.Pitch);
                Assert.Equal(want.VelX, got.VelX);
                Assert.Equal(want.VelY, got.VelY);
                Assert.Equal(want.VelZ, got.VelZ);
                Assert.Equal(want.StateFlags, got.StateFlags);
                Assert.Equal(want.Health, got.Health);
                Assert.Equal(want.WeaponId, got.WeaponId);
                Assert.Equal(want.AmmoInClip, got.AmmoInClip);
                Assert.Equal(want.Team, got.Team);
            }
        }
    }
}
