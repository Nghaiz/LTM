using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;
using Xunit.Abstractions;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The measured numbers phase 02 is graded on: criterion 1 (interest management cuts
    /// bandwidth by at least 40%), criterion 2 (at most 8 KB/s per client), and the hit-rate
    /// against RTT experiment the report's headline chart is drawn from.
    /// </summary>
    /// <remarks>
    /// Every table printed here is reproducible by running the test — the report quotes these
    /// numbers rather than any measured by hand.
    /// </remarks>
    public sealed class Phase02MeasurementTests
    {
        private const int ActorCount = 48;
        private const int HumanViewers = 8;
        private const int MeasuredSeconds = 30;

        /// <summary>
        /// Dustbowl's measured playable extent, 1700 m on X (protocol-spec.md section 4.4).
        /// The larger of the two shipping maps, and the one criterion 1 is graded on.
        /// </summary>
        private const float DustbowlSpread = 1700f;

        /// <summary>
        /// Island, whose worst playable coordinate is 589.7 m from the origin
        /// (protocol-spec.md section 4.4) — so roughly a 1180 m box.
        /// </summary>
        private const float IslandSpread = 1180f;

        private readonly ITestOutputHelper _output;

        public Phase02MeasurementTests(ITestOutputHelper output) => _output = output;

        // ------------------------------------------------------------------ criteria 1 and 2

        [Fact]
        public void PrintTheBandwidthAgainstMapDensityTable()
        {
            // The saving is a function of how spread out the actors are, and it is worth
            // publishing rather than hiding behind one number: the bands are 60/150/300 m, so
            // on a map small enough that everybody is within 300 m of everybody, interest
            // management has almost nothing to remove. The two real maps are marked.
            _output.WriteLine("Bandwidth against map size, 48 actors, 8 clients, 20 Hz, 30 s");
            _output.WriteLine("| Map box (m) | Off (KB/s) | On (KB/s) | Saving | Note |");
            _output.WriteLine("|---|---|---|---|---|");

            double previousSaving = -1.0;

            foreach (float spread in new[] { 400f, 800f, IslandSpread, 1600f, DustbowlSpread })
            {
                BandwidthResult off = MeasureBandwidth(useInterest: false, spread);
                BandwidthResult on = MeasureBandwidth(useInterest: true, spread);
                double saving = SavingPercent(off, on);

                string note = spread switch
                {
                    IslandSpread => "Island",
                    DustbowlSpread => "Dustbowl",
                    400f => "denser than any real map; criterion 1 would fail here",
                    _ => "",
                };

                _output.WriteLine(
                    $"| {spread:F0} | {off.BytesPerSecondPerClient / 1024.0:F2} "
                    + $"| {on.BytesPerSecondPerClient / 1024.0:F2} | {saving:F1}% | {note} |");

                Assert.True(saving > previousSaving,
                    "a more spread-out map must save more, not less");
                previousSaving = saving;
            }
        }

        [Fact]
        public void PrintTheBandwidthTableAndHoldTheBudget()
        {
            BandwidthResult without = MeasureBandwidth(useInterest: false, DustbowlSpread);
            BandwidthResult with = MeasureBandwidth(useInterest: true, DustbowlSpread);

            double saving = SavingPercent(without, with);

            _output.WriteLine("Phase-02 bandwidth, 48 actors, 8 clients, 20 Hz, 30 s, Dustbowl");
            _output.WriteLine("| Case | KB/s/client | Actor slots sent |");
            _output.WriteLine("|---|---|---|");
            _output.WriteLine(
                $"| No interest management | {without.BytesPerSecondPerClient / 1024.0:F2} "
                + $"| {without.ActorSlotsSent} |");
            _output.WriteLine(
                $"| Interest management on | {with.BytesPerSecondPerClient / 1024.0:F2} "
                + $"| {with.ActorSlotsSent} |");
            _output.WriteLine($"| Saving | {saving:F1}% | |");

            // Criterion 1.
            Assert.True(saving >= 40.0,
                $"expected at least a 40% bandwidth cut, measured {saving:F1}%");

            // Criterion 2. The phase-01 report measured 10.94 KB/s before interest management
            // and projected 4.5-6 KB/s after; this is that projection being checked. Also
            // checked on Island, the smaller map, so the budget is not being claimed on the
            // most favourable geometry available.
            double kilobytesPerSecond = with.BytesPerSecondPerClient / 1024.0;
            Assert.True(kilobytesPerSecond <= 8.0,
                $"expected at most 8 KB/s per client, measured {kilobytesPerSecond:F2} KB/s");

            BandwidthResult island = MeasureBandwidth(useInterest: true, IslandSpread);
            double islandKilobytes = island.BytesPerSecondPerClient / 1024.0;

            _output.WriteLine($"| Island, interest on | {islandKilobytes:F2} | {island.ActorSlotsSent} |");

            Assert.True(islandKilobytes <= 8.0,
                $"Island exceeds the budget at {islandKilobytes:F2} KB/s");
        }

        private static double SavingPercent(BandwidthResult off, BandwidthResult on)
            => 100.0 * (off.BytesPerSecondPerClient - on.BytesPerSecondPerClient)
               / off.BytesPerSecondPerClient;

        [Fact]
        public void InterestManagementDoesNotBreakTheDeltaStream()
        {
            // The thing that could quietly go wrong: an actor omitted from a snapshot is also
            // absent from the baseline that snapshot becomes, so when it returns it has to be
            // re-sent in full. That is correct and handled by DeltaEncoder, but it must
            // actually happen rather than the client inheriting a stale entry.
            var manager = new InterestManager();
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            var view = new WorldSnapshot();
            var body = new byte[ServerPayloadWriter.MaxSnapshotBodySize];

            WorldSnapshot world = InterestManagementTests.BuildWorld(ActorCount, DustbowlSpread);

            for (uint snapshot = 1; snapshot <= 60; snapshot++)
            {
                world.ServerTick = snapshot;

                // The actors have to MOVE. Without this the assertion below is satisfied by any
                // stale entry the client happens to be holding, so the test passes whether or
                // not omitted actors are correctly re-sent in full — which is the whole thing
                // it claims to check.
                DriftActors(world, snapshot);

                manager.BeginSnapshot();
                Assert.True(manager.BuildView(1, world, snapshot, view));

                int written = encoder.Write(body, view, lastProcessedInputTick: snapshot);
                Assert.True(written > 0);
                Assert.Equal(SnapshotReadResult.Applied,
                    decoder.Read(new ReadOnlySpan<byte>(body, 0, written)));

                encoder.OnClientAck(snapshot);
            }

            // Whatever the client ended up holding must match a real world position for every
            // actor it was told about — no zeroed entries, no actors at the origin.
            for (int i = 0; i < decoder.Current.ActorCount; i++)
            {
                ActorSnapshotEntry received = decoder.Current.Actors[i];
                Assert.True(world.TryFind(received.ActorId, out ActorSnapshotEntry truth));
                Assert.Equal(truth.PosX, received.PosX);
                Assert.Equal(truth.PosZ, received.PosZ);
                Assert.Equal(truth.Team, received.Team);
            }
        }

        // ------------------------------------------------------------------ the headline chart

        [Fact]
        public void PrintTheHitRateAgainstRttTable()
        {
            float[] pings = { 0f, 50f, 100f, 150f, 200f, 300f };

            _output.WriteLine("Hit rate against RTT, target strafing at 5 m/s across 20 m");
            _output.WriteLine("| RTT (ms) | Rewind (ticks) | Compensated | Uncompensated |");
            _output.WriteLine("|---|---|---|---|");

            foreach (float ping in pings)
            {
                int compensated = StrafeVolley(ping, compensated: true);
                int uncompensated = StrafeVolley(ping, compensated: false);

                _output.WriteLine(
                    $"| {ping:F0} | {LagCompensator.RewindTicks(ping)} "
                    + $"| {compensated * 5}% | {uncompensated * 5}% |");

                Assert.True(compensated >= uncompensated,
                    $"compensation made things worse at {ping} ms");
            }

            // The point of the chart: the compensated series stays flat where the other falls
            // away. A 300 ms ping is past the 200 ms rewind clamp, so even compensation cannot
            // fully rescue it — which is the anti-abuse limit working, not a regression.
            Assert.True(StrafeVolley(200f, compensated: true) >= 15);
            Assert.True(StrafeVolley(200f, compensated: false) <= 5);
        }

        [Fact]
        public void PrintTheLodTickingTable()
        {
            var scheduler = new BotLodScheduler();
            const int nearBots = 12;
            const int totalBots = 32;

            for (uint tick = 0; tick < 300; tick++)
            {
                for (ushort bot = 100; bot < 100 + nearBots; bot++)
                    scheduler.ShouldTick(bot, InterestLevel.Near, tick);

                for (ushort bot = (ushort)(100 + nearBots); bot < 100 + totalBots; bot++)
                    scheduler.ShouldTick(bot, InterestLevel.Far, tick);
            }

            _output.WriteLine("Bot AI LOD, 32 bots, 20 of them distant, 300 ticks");
            _output.WriteLine($"| AI updates run     | {scheduler.TicksGranted} |");
            _output.WriteLine($"| AI updates skipped | {scheduler.TicksSkipped} |");
            _output.WriteLine($"| Skipped share      | {scheduler.SkippedPercent:F1}% |");

            Assert.True(scheduler.SkippedPercent >= 30.0);
        }

        [Fact]
        public void PrintTheHitboxHistoryMemoryTable()
        {
            // The task document estimates ~166 KB for 48 actors. This is the same arithmetic
            // against the struct that actually shipped, so the report quotes a measured figure.
            int aabbBytes = 2 * 3 * sizeof(float);                      // centre + extents
            int setBytes = HitboxSet.Count * aabbBytes;
            int frameBytes = setBytes + sizeof(uint) + sizeof(bool);
            int perActor = frameBytes * HitboxHistory.Ticks;
            int total = perActor * ActorCount;

            _output.WriteLine("Hitbox history memory");
            _output.WriteLine($"| Bytes per box        | {aabbBytes} |");
            _output.WriteLine($"| Boxes per actor      | {HitboxSet.Count} |");
            _output.WriteLine($"| Bytes per frame      | {frameBytes} |");
            _output.WriteLine($"| Frames per actor     | {HitboxHistory.Ticks} |");
            _output.WriteLine($"| Bytes per actor      | {perActor / 1024.0:F1} KB |");
            _output.WriteLine($"| Total, {ActorCount} actors    | {total / 1024.0:F0} KB |");

            // Negligible against any budget, and that is the point worth pinning: this is not
            // a structure anyone should be tempted to shrink at the cost of clarity.
            Assert.True(total < 512 * 1024);
        }

        // ------------------------------------------------------------------ helpers

        private readonly struct BandwidthResult
        {
            public readonly double BytesPerSecondPerClient;
            public readonly long ActorSlotsSent;

            public BandwidthResult(double bytesPerSecondPerClient, long actorSlotsSent)
            {
                BytesPerSecondPerClient = bytesPerSecondPerClient;
                ActorSlotsSent = actorSlotsSent;
            }
        }

        /// <summary>
        /// Runs 30 s of real snapshot encoding for 8 clients and totals the wire bytes.
        /// </summary>
        /// <remarks>
        /// Measured through <see cref="ServerPayloadWriter"/>, so the figure includes the
        /// payload framing the transport adds rather than only the snapshot body — the same
        /// basis as the phase-01 report's 10.94 KB/s, which is what makes the two comparable.
        /// </remarks>
        private static BandwidthResult MeasureBandwidth(bool useInterest, float mapSpread)
        {
            int snapshots = MeasuredSeconds * ProtocolConstants.SNAPSHOT_RATE;

            var manager = new InterestManager();
            var view = new WorldSnapshot();
            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var body = new byte[ServerPayloadWriter.MaxSnapshotBodySize];

            var encoders = new DeltaEncoder[HumanViewers];
            for (int i = 0; i < HumanViewers; i++) encoders[i] = new DeltaEncoder();

            WorldSnapshot world = InterestManagementTests.BuildWorld(ActorCount, mapSpread);
            long totalBytes = 0;
            long slotsSent = 0;

            for (uint snapshot = 1; snapshot <= snapshots; snapshot++)
            {
                world.ServerTick = snapshot;
                DriftActors(world, snapshot);
                manager.BeginSnapshot();

                for (int i = 0; i < HumanViewers; i++)
                {
                    var viewerId = (ushort)(i + 1);
                    WorldSnapshot outgoing;

                    if (useInterest)
                    {
                        manager.BuildView(viewerId, world, snapshot, view);
                        outgoing = view;
                    }
                    else
                    {
                        outgoing = world;
                    }

                    slotsSent += outgoing.ActorCount;

                    int written = ServerPayloadWriter.WriteSnapshot(
                        payload, body, encoders[i], outgoing, lastProcessedInputTick: snapshot);

                    if (written > 0) totalBytes += written;

                    // A client on a clean link acks roughly every snapshot; two behind is a
                    // realistic steady state and is what phase 01 measured against.
                    if (snapshot > 2) encoders[i].OnClientAck(snapshot - 2);
                }
            }

            return new BandwidthResult(
                (double)totalBytes / MeasuredSeconds / HumanViewers, slotsSent);
        }

        /// <summary>
        /// Moves the world a little between snapshots so the delta encoder has real work.
        /// </summary>
        /// <remarks>
        /// 60% walking a straight line, 20% manoeuvring, 20% stationary — the same mix the
        /// phase-01 measurement used, so the two bandwidth figures describe the same world.
        /// </remarks>
        private static void DriftActors(WorldSnapshot world, uint snapshot)
        {
            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry actor = ref world.Actors[i];

                int behaviour = actor.ActorId % 5;
                if (behaviour == 4) continue;                       // 20% stationary

                Vec3 position = SnapshotBuilder.UnpackPosition(in actor);
                float step = 4f / ProtocolConstants.SNAPSHOT_RATE;  // ~4 m/s

                Vec3 moved = behaviour == 3                          // 20% manoeuvring
                    ? new Vec3(
                        position.X + step * (float)Math.Sin(snapshot * 0.3 + actor.ActorId),
                        position.Y,
                        position.Z + step * (float)Math.Cos(snapshot * 0.3 + actor.ActorId))
                    : new Vec3(position.X + step, position.Y, position.Z);

                actor.PosX = Quantize.PackPos(moved.X);
                actor.PosZ = Quantize.PackPos(moved.Z);
                actor.Yaw = Quantize.PackYaw(snapshot * 3f + actor.ActorId);
            }
        }

        /// <summary>20 shots at a strafing target; returns how many landed.</summary>
        private static int StrafeVolley(float rttMs, bool compensated)
            => StrafeVolleyFor(rttMs, compensated);

        /// <summary>
        /// 20 shots at a strafing target; returns how many landed.
        /// </summary>
        /// <remarks>
        /// Internal rather than private because the phase-04 experiment prints the same
        /// measurement across six RTTs and two target speeds. Re-implementing it there would
        /// give the report a chart drawn by a second fixture that could drift from the one the
        /// phase-02 criteria were graded on.
        /// </remarks>
        internal static int StrafeVolleyFor(float rttMs, bool compensated, float strafeSpeed = 5f)
        {
            const float range = 20f;
            const uint firstShotTick = 200;
            const ushort shooter = 1;
            const ushort target = 2;
            var muzzle = new Vec3(0f, 1.5f, 0f);

            // Captured as the volley advances, not pre-filled: the ring holds one second, so
            // filling it to tick 400 before firing at tick 200 leaves nothing to rewind to.
            var history = new HitboxHistory();
            var compensator = new LagCompensator(compensated ? history : new HitboxHistory());
            int rewind = LagCompensator.RewindTicks(rttMs);
            int hits = 0;
            uint capturedThrough = 0;

            for (int shot = 0; shot < 20; shot++)
            {
                uint currentTick = firstShotTick + (uint)(shot * 3);
                uint seenTick = currentTick - (uint)rewind;

                for (uint tick = capturedThrough; tick <= currentTick; tick++)
                    history.Capture(tick, target, HitboxSet.Humanoid(PositionAt(tick)));

                capturedThrough = currentTick + 1;

                // Derived from protocol-spec section 7.1 in milliseconds, NOT by calling
                // RewindTicks — using the function under test to build the aim point makes the
                // experiment self-fulfilling.
                float clientViewLagMs = rttMs * 0.5f + ProtocolConstants.INTERP_BUFFER_MS;
                float seenTimeMs = currentTick * ProtocolConstants.MS_PER_TICK - clientViewLagMs;
                Vec3 aimPoint = HitboxSet.Humanoid(PositionAtTime(seenTimeMs)).Torso.Center;
                var present = new[]
                {
                    new HitscanTarget(target, true, HitboxSet.Humanoid(PositionAt(currentTick))),
                };

                if (compensator.ResolveHitscan(
                        present, shooter, muzzle, aimPoint - muzzle,
                        maxDistance: 100f, smoothedRttMs: rttMs, currentTick: currentTick).Hit)
                    hits++;
            }

            return hits;

            // Not `static`: the target speed is now a parameter, and a static local function
            // cannot see it (CS8421).
            Vec3 PositionAt(uint tick)
                => PositionAtTime(tick * ProtocolConstants.MS_PER_TICK);

            Vec3 PositionAtTime(float milliseconds)
                => new Vec3(
                    strafeSpeed * (milliseconds - firstShotTick * ProtocolConstants.MS_PER_TICK)
                        / 1000f,
                    0f,
                    range);
        }
    }
}
