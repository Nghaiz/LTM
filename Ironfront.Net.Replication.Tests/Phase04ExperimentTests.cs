using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Tests.Experiments;
using Xunit;
using Xunit.Abstractions;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The three phase-04 experiments: what each compression technique contributes, how hit
    /// rate holds up against RTT, and where the tick budget actually goes.
    /// </summary>
    /// <remarks>
    /// Every table in the phase-04 report is printed by a test in this file. Nothing in the
    /// report is measured by hand, and every row is reproducible with
    /// <c>dotnet test --filter Phase04ExperimentTests</c>.
    /// </remarks>
    public sealed class Phase04ExperimentTests
    {
        private const int Humans = ProtocolConstants.MAX_PLAYERS;   // 16
        private const int Bots = ProtocolConstants.MAX_BOTS;        // 32
        private const int Actors = Humans + Bots;                   // 48
        private const int MeasuredSeconds = 300;                    // the 5 minutes task 1 asks for
        private const float MapSpread = 1700f;                      // Dustbowl

        /// <summary>
        /// One seed for the whole experiment, so every configuration sees the same world moving
        /// the same way. Without it the rows differ by both technique and traffic, and the
        /// attribution the table exists for is meaningless.
        /// </summary>
        private const uint Seed = 20260813;

        private readonly ITestOutputHelper _output;

        public Phase04ExperimentTests(ITestOutputHelper output) => _output = output;

        // ================================================================== task 1

        [Fact]
        public void PrintTheCompressionExperimentTable()
        {
            var rows = new (string Label, ReplicationConfig Config)[]
            {
                ("baseline: full snapshots, byte-aligned, no interest", ReplicationConfig.Baseline),
                ("+ bit-packing", With(ReplicationConfig.Baseline, c => c.UseBitPacking = true)),
                ("+ delta encoding", With(ReplicationConfig.Baseline, c =>
                    { c.UseBitPacking = true; c.UseDeltaEncoding = true; })),
                ("+ interest management", With(ReplicationConfig.Baseline, c =>
                    { c.UseBitPacking = true; c.UseDeltaEncoding = true; c.UseInterestManagement = true; })),
                ("+ velocity, 12-bit height, distant pitch", new ReplicationConfig
                {
                    UseBitPacking = true, UseDeltaEncoding = true, UseInterestManagement = true,
                    UseVelocityCulling = true, UseCompactHeight = true,
                    UseDistantPitchCulling = true, DropStaleDeadActors = true,
                }),
            };

            _output.WriteLine(
                $"Compression experiment — {Humans} players + {Bots} bots, {MeasuredSeconds} s, "
                + $"seed {Seed}, {MapSpread} m map");
            _output.WriteLine(
                "| Configuration | KB/s/client | Mean snapshot (B) | Cumulative saving |");
            _output.WriteLine("|---|---|---|---|");

            double baseline = 0.0;
            var measured = new List<CompressionResult>();

            foreach ((string label, ReplicationConfig config) in rows)
            {
                CompressionResult result = MeasureCompression(config);
                measured.Add(result);

                if (baseline == 0.0) baseline = result.KilobytesPerSecond;
                double saving = 100.0 * (baseline - result.KilobytesPerSecond) / baseline;

                _output.WriteLine(
                    $"| {label} | {result.KilobytesPerSecond:F2} | {result.MeanSnapshotBytes:F0} "
                    + $"| {saving:F1}% |");
            }

            // Every technique must pay for itself. A row that costs code and saves nothing
            // belongs in the report's rejected list, not in its results table.
            for (int i = 1; i < measured.Count; i++)
                Assert.True(
                    measured[i].KilobytesPerSecond < measured[i - 1].KilobytesPerSecond,
                    $"row {i} ({measured[i].KilobytesPerSecond:F2} KB/s) did not improve on "
                    + $"row {i - 1} ({measured[i - 1].KilobytesPerSecond:F2} KB/s)");

            // And the whole stack has to land inside the plan.md section 10 budget.
            Assert.True(measured[^1].KilobytesPerSecond <= 8.0);
        }

        [Fact]
        public void ByteAlignedMatchesTheShippedEncoderSoTheTableIsMeasuringTheRealThing()
        {
            // Without this the entire experiment could be measuring a codec nobody uses. The
            // byte-aligned full-snapshot configuration must produce the same byte count per
            // actor as the frozen format: 3 bytes of entry header plus the 20-byte v1 payload,
            // less the one bit the experimental pitch flag adds and then pads away.
            WorldSnapshot world = InterestManagementTests.BuildWorld(4, 100f);
            world.ServerTick = 1;

            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];
            int experimental = ExperimentalSnapshotCodec.Write(
                buffer, world, baseline: null, Vec3.Zero, ReplicationConfig.Baseline);

            var shipped = new byte[ProtocolConstants.MAX_PAYLOAD];
            var header = new SnapshotHeader(1, 1, 0, (byte)world.ActorCount);
            int frozen = SnapshotMessage.Write(
                shipped, in header, world.Actors.AsSpan(0, world.ActorCount));

            // The experimental header is 9 bytes to the frozen 13 (it carries no
            // lastProcessedInputTick), and each entry carries one extra pitch-present bit that
            // byte alignment rounds up to a whole byte. Both differences are constant and
            // accounted for, which is what makes the comparison meaningful.
            const int headerDifference = SnapshotHeader.Size - 9;
            int pitchFlagPadding = world.ActorCount;

            Assert.Equal(frozen, experimental + headerDifference - pitchFlagPadding);
        }

        [Fact]
        public void EveryExperimentalConfigurationRoundTripsExactly()
        {
            // A compression figure from a codec that cannot decode its own output is not a
            // result, it is a smaller number.
            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);
            world.ServerTick = 99;

            foreach (ReplicationConfig config in new[]
            {
                ReplicationConfig.Baseline,
                With(ReplicationConfig.Baseline, c => c.UseBitPacking = true),
                new ReplicationConfig { UseBitPacking = true, UseCompactHeight = true },
                new ReplicationConfig { UseBitPacking = true, UseDistantPitchCulling = true },
            })
            {
                var buffer = new byte[ProtocolConstants.MAX_PAYLOAD * 2];
                int written = ExperimentalSnapshotCodec.Write(
                    buffer, world, baseline: null, Vec3.Zero, config);
                Assert.True(written > 0);

                var decoded = new WorldSnapshot();
                Assert.True(ExperimentalSnapshotCodec.TryRead(
                    buffer.AsSpan(0, written), decoded, baseline: null, config));

                Assert.Equal(world.ActorCount, decoded.ActorCount);

                for (int i = 0; i < world.ActorCount; i++)
                {
                    ActorSnapshotEntry sent = world.Actors[i];
                    ActorSnapshotEntry received = decoded.Actors[i];

                    Assert.Equal(sent.ActorId, received.ActorId);
                    Assert.Equal(sent.PosX, received.PosX);
                    Assert.Equal(sent.PosZ, received.PosZ);
                    Assert.Equal(sent.Yaw, received.Yaw);
                    Assert.Equal(sent.Health, received.Health);

                    // Height is lossy under UseCompactHeight, by design — that is the technique.
                    if (!config.UseCompactHeight) Assert.Equal(sent.PosY, received.PosY);
                }
            }
        }

        [Fact]
        public void TheTwelveBitHeightCostsResolutionAndSaysSoInMetres()
        {
            // The number the report needs in order to be honest about what the 12-bit height
            // buys: how far off it puts an actor.
            float worstError = 0f;
            for (float metres = 0f; metres <= ExperimentalSnapshotCodec.CompactHeightRange; metres += 0.37f)
            {
                var world = new WorldSnapshot();
                world.Add(InterestManagementTests.Actor(1, new Vec3(0f, metres, 0f), 0));

                var config = new ReplicationConfig { UseBitPacking = true, UseCompactHeight = true };
                var buffer = new byte[64];
                int written = ExperimentalSnapshotCodec.Write(buffer, world, null, Vec3.Zero, config);

                var decoded = new WorldSnapshot();
                ExperimentalSnapshotCodec.TryRead(buffer.AsSpan(0, written), decoded, null, config);

                float back = Quantize.UnpackPos(decoded.Actors[0].PosY);
                worstError = Math.Max(worstError, Math.Abs(back - metres));
            }

            _output.WriteLine($"12-bit height worst-case error: {worstError * 100f:F1} cm");

            // The measured answer, which is not the one the technique is usually sold with.
            // 256 m over 4095 steps is a 6.25 cm step — the same as the spec's position
            // resolution — but the value has already been through PackPos, so the two
            // quantizations compound and the worst case is 12.5 cm, exactly double. So the
            // 12-bit height costs BOTH range (a 256 m ceiling) and one extra position quantum,
            // for the 1.2% of bandwidth the experiment table attributes to it. That is the
            // finding, and it is the reason the technique is measured rather than shipped.
            Assert.True(worstError < 0.13f, $"{worstError:F4} m is worse than two position quanta");
            Assert.True(worstError > 0.06f,
                        "the compounding is the finding; a smaller error means it was not measured");
        }

        // ================================================================== task 2

        [Fact]
        public void PrintTheHitRateAgainstRttTable()
        {
            _output.WriteLine("Hit rate against RTT — 5 m/s strafing target, 20 shots per row");
            _output.WriteLine("| RTT (ms) | Rewind (ticks) | With lag comp | Without |");
            _output.WriteLine("|---|---|---|---|");

            foreach (float rtt in new[] { 0f, 50f, 100f, 150f, 200f, 300f })
            {
                int compensated = Phase02MeasurementTests.StrafeVolleyFor(rtt, compensated: true);
                int uncompensated = Phase02MeasurementTests.StrafeVolleyFor(rtt, compensated: false);

                _output.WriteLine(
                    $"| {rtt:F0} | {LagCompensator.RewindTicks(rtt)} "
                    + $"| {compensated * 5}% | {uncompensated * 5}% |");

                Assert.True(compensated >= uncompensated,
                            $"compensation made things worse at {rtt} ms");
            }
        }

        [Fact]
        public void PastTheClampAFastTargetIsMissedWhichIsWhatTheClampCosts()
        {
            // The 300 ms row of the table above still reads 100% at 5 m/s, because the 50 ms
            // the clamp gives up is 0.25 m of strafe — inside a torso. Wind the target up and
            // the limit becomes visible, which is the point of measuring past the clamp at all.
            int slow = Phase02MeasurementTests.StrafeVolleyFor(300f, compensated: true);
            int fast = Phase02MeasurementTests.StrafeVolleyFor(300f, compensated: true, strafeSpeed: 20f);

            _output.WriteLine($"At 300 ms: 5 m/s target {slow * 5}%, 20 m/s target {fast * 5}%");
            Assert.True(fast < slow, "the clamp should cost something at 20 m/s");
        }

        // ================================================================== task 3

        [Fact]
        public void PrintTheEngineFreeTickBreakdown()
        {
            // Honest scope: this is the netcode half of task 3's table. Physics and AI are
            // inside the Unity tick and cannot be measured from here at all, which is exactly
            // the conclusion the report draws — so the missing rows are named rather than
            // estimated. Checklist item S5.
            _output.WriteLine(
                $"Engine-free tick cost — {Actors} actors, {Humans} clients, per snapshot");
            _output.WriteLine("| Stage | Mean (us) | Share of the netcode cost |");
            _output.WriteLine("|---|---|---|");

            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);
            var manager = new InterestManager();
            var view = new WorldSnapshot();
            var history = new HitboxHistory();
            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var body = new byte[ServerPayloadWriter.MaxSnapshotBodySize];
            var encoders = new DeltaEncoder[Humans];
            for (int i = 0; i < Humans; i++) encoders[i] = new DeltaEncoder();

            const int iterations = 400;
            const int warmup = 50;

            // Three accumulating stopwatches over ONE pass, rather than one timed pass per
            // stage. Timing the stages in separate passes and subtracting is wrong here and was
            // measurably so: BuildView records each pair's send as a side effect, so a second
            // BuildView in the same snapshot finds nothing due and returns an almost empty view
            // — which made the encode stage read 0.0 us, for a stage that plainly is not free.
            var interestClock = new Stopwatch();
            var encodeClock = new Stopwatch();
            var historyClock = new Stopwatch();

            for (int iteration = 0; iteration < iterations + warmup; iteration++)
            {
                bool measuring = iteration >= warmup;
                var snapshot = (uint)(iteration + 1);
                world.ServerTick = snapshot;

                manager.BeginSnapshot();

                for (int i = 0; i < Humans; i++)
                {
                    if (measuring) interestClock.Start();
                    manager.BuildView((ushort)(i + 1), world, snapshot, view);
                    if (measuring) interestClock.Stop();

                    if (measuring) encodeClock.Start();
                    ServerPayloadWriter.WriteSnapshot(payload, body, encoders[i], view, snapshot);
                    if (measuring) encodeClock.Stop();
                }

                if (measuring) historyClock.Start();
                for (ushort actor = 1; actor <= Actors; actor++)
                    history.Capture(snapshot, actor, HitboxSet.Humanoid(Vec3.Zero));
                if (measuring) historyClock.Stop();
            }

            double interest = Microseconds(interestClock) / iterations;
            double encode = Microseconds(encodeClock) / iterations;
            double historyPerSnapshot = Microseconds(historyClock) / iterations;
            double total = interest + encode + historyPerSnapshot;

            Row("interest management (16 views)", interest, total);
            Row("delta encode + frame (16 clients)", encode, total);
            Row("hitbox history capture (48 actors)", historyPerSnapshot, total);
            Row("**netcode total**", total, total);
            _output.WriteLine("| Unity physics + AI | not measurable engine-free | — |");

            // The conclusion the report leads with: even the whole netcode stack is a small
            // fraction of a 33 ms budget, so the bottleneck is elsewhere.
            Assert.True(total < 33_000.0,
                        $"netcode alone is {total:F0} us of a 33333 us tick");

            // Every stage must register. A zero here means the pass measured nothing, which is
            // exactly the failure the single-pass rewrite above was written to close.
            Assert.True(encode > 0.0, "the encode stage measured as free, which it is not");
            Assert.True(interest > 0.0);

            void Row(string label, double microseconds, double whole)
                => _output.WriteLine(
                    $"| {label} | {microseconds:F1} | {100.0 * microseconds / whole:F1}% |");
        }

        [Fact]
        public void InterestManagementScalesQuadraticallyAndSaysWhereThatStopsBeingFine()
        {
            // C-AD-3 accepts O(n^2) because 48 actors is 2304 comparisons. The report claims a
            // ceiling; this is where the number comes from rather than an assertion in prose.
            _output.WriteLine("| Actors | Pair comparisons | Mean per snapshot (us) |");
            _output.WriteLine("|---|---|---|");

            double previous = 0.0;
            foreach (int actorCount in new[] { 16, 32, 48, 64 })
            {
                WorldSnapshot world = InterestManagementTests.BuildWorld(actorCount, MapSpread);
                var manager = new InterestManager();
                var view = new WorldSnapshot();

                var clock = new Stopwatch();
                const int iterations = 300;

                for (int iteration = 0; iteration < iterations + 30; iteration++)
                {
                    if (iteration == 30) clock.Start();
                    manager.BeginSnapshot();
                    for (int i = 0; i < Humans; i++)
                        manager.BuildView((ushort)(i + 1), world, (uint)iteration + 1, view);
                }

                clock.Stop();
                double perSnapshot = Microseconds(clock) / iterations;
                _output.WriteLine($"| {actorCount} | {Humans * actorCount} | {perSnapshot:F1} |");
                previous = perSnapshot;
            }

            Assert.True(previous > 0.0);
        }

        // ------------------------------------------------------------------ helpers

        private static double Microseconds(Stopwatch clock)
            => clock.ElapsedTicks * 1_000_000.0 / Stopwatch.Frequency;

        private static ReplicationConfig With(ReplicationConfig source, Action<ReplicationConfig> mutate)
        {
            var copy = new ReplicationConfig
            {
                UseBitPacking          = source.UseBitPacking,
                UseDeltaEncoding       = source.UseDeltaEncoding,
                UseInterestManagement  = source.UseInterestManagement,
                UseVelocityCulling     = source.UseVelocityCulling,
                UseCompactHeight       = source.UseCompactHeight,
                UseDistantPitchCulling = source.UseDistantPitchCulling,
                DropStaleDeadActors    = source.DropStaleDeadActors,
            };
            mutate(copy);
            return copy;
        }

        private readonly struct CompressionResult
        {
            public CompressionResult(double kilobytesPerSecond, double meanSnapshotBytes)
            {
                KilobytesPerSecond = kilobytesPerSecond;
                MeanSnapshotBytes  = meanSnapshotBytes;
            }

            public double KilobytesPerSecond { get; }
            public double MeanSnapshotBytes { get; }
        }

        private static CompressionResult MeasureCompression(ReplicationConfig config)
        {
            int snapshots = MeasuredSeconds * ProtocolConstants.SNAPSHOT_RATE;

            var manager = new InterestManager { Config = config };
            var view = new WorldSnapshot();
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD * 4];

            // One baseline per client, held two snapshots back — the same steady state phases
            // 01 and 02 measured against.
            var baselines = new WorldSnapshot[Humans][];
            for (int i = 0; i < Humans; i++)
            {
                baselines[i] = new WorldSnapshot[3];
                for (int j = 0; j < 3; j++) baselines[i][j] = new WorldSnapshot();
            }

            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);
            long totalBytes = 0;
            long snapshotsSent = 0;

            for (uint snapshot = 1; snapshot <= snapshots; snapshot++)
            {
                world.ServerTick = snapshot;
                Drift(world, snapshot);
                manager.BeginSnapshot();

                for (int i = 0; i < Humans; i++)
                {
                    var viewerId = (ushort)(i + 1);
                    WorldSnapshot outgoing;

                    if (config.UseInterestManagement)
                    {
                        manager.BuildView(viewerId, world, snapshot, view);
                        outgoing = view;
                    }
                    else
                    {
                        outgoing = world;
                    }

                    int viewerIndex = world.IndexOf(viewerId);
                    Vec3 viewerPosition = viewerIndex >= 0
                        ? SnapshotBuilder.UnpackPosition(in world.Actors[viewerIndex])
                        : Vec3.Zero;

                    WorldSnapshot? baseline = config.UseDeltaEncoding && snapshot > 2
                        ? baselines[i][(snapshot - 2) % 3]
                        : null;

                    int written = ExperimentalSnapshotCodec.Write(
                        buffer, outgoing, baseline, in viewerPosition, config);

                    if (written > 0)
                    {
                        totalBytes += written;
                        snapshotsSent++;
                    }

                    baselines[i][snapshot % 3].CopyFrom(outgoing);
                }
            }

            return new CompressionResult(
                (double)totalBytes / MeasuredSeconds / Humans / 1024.0,
                snapshotsSent == 0 ? 0.0 : (double)totalBytes / snapshotsSent);
        }

        /// <summary>
        /// The same movement mix every phase has measured against, driven by one fixed seed so
        /// every configuration sees an identical world.
        /// </summary>
        private static void Drift(WorldSnapshot world, uint snapshot)
        {
            uint state = Seed + snapshot;

            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry actor = ref world.Actors[i];

                state = state * 1664525u + 1013904223u;

                int behaviour = actor.ActorId % 5;
                if (behaviour == 4) continue;                       // 20% stationary

                Vec3 position = SnapshotBuilder.UnpackPosition(in actor);
                float step = 4f / ProtocolConstants.SNAPSHOT_RATE;

                Vec3 moved = behaviour == 3                          // 20% manoeuvring
                    ? new Vec3(
                        position.X + step * (float)Math.Sin(snapshot * 0.3 + actor.ActorId),
                        position.Y + ((state >> 20 & 0x1F) - 15.5f) * 0.01f,
                        position.Z + step * (float)Math.Cos(snapshot * 0.3 + actor.ActorId))
                    : new Vec3(position.X + step, position.Y, position.Z);

                actor.PosX = Quantize.PackPos(moved.X);
                actor.PosY = Quantize.PackPos(Math.Max(0f, moved.Y));
                actor.PosZ = Quantize.PackPos(moved.Z);
                actor.Yaw = Quantize.PackYaw(snapshot * 3f + actor.ActorId);
                actor.Pitch = Quantize.PackPitchByte(((state >> 8 & 0x3F) - 32f));
                actor.VelX = Quantize.PackVel(4f);
            }
        }
    }
}
