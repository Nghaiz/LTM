using System;
using System.Globalization;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Movement;
using Xunit;
using Xunit.Abstractions;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Produces the numbers phase-01 § 5 "Required measurements" asks for, so the report
    /// table is reproducible rather than transcribed by hand.
    /// </summary>
    /// <remarks>
    /// Run it and read the output:
    /// <code>
    /// dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~MeasurementReport \
    ///     --logger "console;verbosity=detailed"
    /// </code>
    /// The assertions here are the same budgets the other suites enforce; the value of this
    /// file is the printed table, not the pass/fail.
    /// </remarks>
    public sealed class MeasurementReportTests
    {
        private readonly ITestOutputHelper _output;

        public MeasurementReportTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void PrintTheSnapshotAndBandwidthTable()
        {
            const int actorCount = 48;
            const int snapshots  = ProtocolConstants.SNAPSHOT_RATE * 30; // 30 seconds

            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            var rng     = new Random(20260812);
            var world   = BuildWorld(actorCount, 20260812);
            var buffer  = new byte[8192];

            long deltaBytes = 0;
            long wireBytes  = 0;
            int measured    = 0;
            int smallest    = int.MaxValue;
            int largest     = 0;

            for (uint tick = 1; tick <= snapshots; tick++)
            {
                Mutate(world, rng);
                world.ServerTick = tick;

                int written = encoder.Write(buffer, world, tick);
                Assert.True(written > 0);

                decoder.Read(new ReadOnlySpan<byte>(buffer, 0, written));
                encoder.OnClientAck(tick);

                if (tick <= 5) continue; // warm-up: no baseline exists yet

                deltaBytes += written;
                wireBytes  += written + ProtocolConstants.GSP_HEADER_SIZE
                            + PayloadFrame.HeaderSize + PayloadFrame.MessageHeaderSize;
                measured++;

                if (written < smallest) smallest = written;
                if (written > largest) largest = written;
            }

            int fullSize      = SnapshotBuilder.FullSizeFor(actorCount);
            double meanDelta  = (double)deltaBytes / measured;
            double saving     = 1.0 - meanDelta / fullSize;
            double seconds    = (double)measured / ProtocolConstants.SNAPSHOT_RATE;
            double kbPerSec   = wireBytes / seconds / 1024.0;

            Write("Phase-01 section 5 — required measurements");
            Write("");
            Write($"| Metric | Conditions | Value |");
            Write($"|---|---|---|");
            Write($"| Full snapshot size | {actorCount} actors | {fullSize} B |");
            Write($"| Full snapshot size | 64 actors (join, fragments) | {SnapshotBuilder.FullSizeFor(64)} B |");
            Write($"| Mean delta size | {actorCount} actors, mid-game | {meanDelta:F1} B |");
            Write($"| Smallest / largest delta | | {smallest} B / {largest} B |");
            Write($"| Delta saving ratio | vs full | {saving:P1} |");
            Write($"| Bandwidth per client | {actorCount} actors, {ProtocolConstants.SNAPSHOT_RATE} Hz, incl. GSP+framing | {kbPerSec:F2} KB/s |");
            Write($"| Snapshots measured | after 5-tick warm-up | {measured} over {seconds:F0} s |");
            Write($"| Full snapshots sent | baseline missing or aged out | {encoder.FullSnapshotCount} |");
            Write($"| Delta snapshots sent | | {encoder.DeltaSnapshotCount} |");
            Write("");
            Write($"Per-actor cost: full {SnapshotMessage.EntrySize(SnapshotField.FullNoSeat)} B, "
                + $"position+rotation delta {SnapshotMessage.EntrySize(SnapshotField.Position | SnapshotField.Rotation)} B, "
                + $"unchanged {SnapshotMessage.EntrySize(SnapshotField.None)} B.");

            Assert.True(saving >= 0.35);
            Assert.True(kbPerSec <= 12.0);
        }

        [Fact]
        public void PrintTheMovementConstantTable()
        {
            Write("Movement constants, read out of the shipped project (docs/movement-analysis.md)");
            Write("");
            Write($"| Constant | Value | Source |");
            Write($"|---|---|---|");
            Write($"| WalkSpeed | {F(MovementCore.WalkSpeed)} m/s | prefab m_WalkSpeed |");
            Write($"| RunSpeed | {F(MovementCore.RunSpeed)} m/s | prefab m_RunSpeed |");
            Write($"| JumpSpeed | {F(MovementCore.JumpSpeed)} m/s | prefab m_JumpSpeed |");
            Write($"| StickToGroundForce | {F(MovementCore.StickToGroundForce)} | prefab m_StickToGroundForce |");
            Write($"| GravityMultiplier | {F(MovementCore.GravityMultiplier)} | prefab m_GravityMultiplier |");
            Write($"| Effective gravity | {F(MovementCore.Gravity)} m/s^2 | DynamicsManager x multiplier |");
            Write($"| StandHeight / CrouchHeight | {F(MovementCore.StandHeight)} / {F(MovementCore.CrouchHeight)} m | CharacterController |");
            Write("");
            Write("There is deliberately no crouch speed — the shipped game does not have one.");
        }

        [Fact]
        public void PrintTheQuantizationErrorTable()
        {
            float worstPosition = 0f;
            for (float v = Quantize.POS_MIN; v <= Quantize.POS_MAX; v += 0.37f)
            {
                float error = Math.Abs(Quantize.UnpackPos(Quantize.PackPos(v)) - v);
                if (error > worstPosition) worstPosition = error;
            }

            float worstYaw = 0f;
            for (float d = 0f; d < 360f; d += 0.13f)
            {
                float error = Math.Abs(Quantize.UnpackYaw(Quantize.PackYaw(d)) - d);
                if (error > worstYaw) worstYaw = error;
            }

            Write("Quantization error, swept across the full range");
            Write("");
            Write($"| Field | Worst observed error | Budget |");
            Write($"|---|---|---|");
            Write($"| Position | {worstPosition:F5} m | < 0.07 m |");
            Write($"| Yaw | {worstYaw:F5} deg | < 0.01 deg |");

            Assert.True(worstPosition < 0.07f);
            Assert.True(worstYaw < 0.01f);
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private void Write(string line) => _output.WriteLine(line);

        private static WorldSnapshot BuildWorld(int actorCount, int seed)
        {
            var rng = new Random(seed);
            var world = new WorldSnapshot();

            for (int i = 0; i < actorCount; i++)
            {
                world.Add(SnapshotBuilder.Capture(
                    (ushort)(i + 1),
                    new Vec3(
                        (float)(rng.NextDouble() * 400.0 - 200.0),
                        (float)(rng.NextDouble() * 20.0),
                        (float)(rng.NextDouble() * 400.0 - 200.0)),
                    (float)(rng.NextDouble() * 360.0),
                    (float)(rng.NextDouble() * 60.0 - 30.0),
                    Vec3.Zero,
                    ActorStateFlags.IsAlive,
                    100f,
                    (byte)rng.Next(0, 8),
                    (byte)rng.Next(0, 31),
                    (byte)(i % 2)));
            }

            return world;
        }

        private static void Mutate(WorldSnapshot world, Random rng)
        {
            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry actor = ref world.Actors[i];
                int behaviour = i % 5;
                if (behaviour == 4) continue;

                actor.PosX = (short)Math.Clamp(actor.PosX + rng.Next(-40, 41), short.MinValue, short.MaxValue);
                actor.PosZ = (short)Math.Clamp(actor.PosZ + rng.Next(-40, 41), short.MinValue, short.MaxValue);
                actor.Yaw  = (ushort)((actor.Yaw + rng.Next(0, 400)) & 0xFFFF);

                if (behaviour == 0)
                {
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
    }
}
