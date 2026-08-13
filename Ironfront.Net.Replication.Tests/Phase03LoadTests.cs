using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;
using Xunit.Abstractions;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-03 tasks 5 and 6: the full 16-player load, the optimizations that survive the
    /// frozen wire format, and five matches back to back with a clean state after each.
    /// </summary>
    public sealed class Phase03LoadTests
    {
        private const int Humans = ProtocolConstants.MAX_PLAYERS;   // 16
        private const int Bots = ProtocolConstants.MAX_BOTS;        // 32
        private const int Actors = Humans + Bots;                   // 48
        private const int MeasuredSeconds = 30;

        /// <summary>Dustbowl's measured playable extent (protocol-spec.md section 4.4).</summary>
        private const float MapSpread = 1700f;

        private readonly ITestOutputHelper _output;

        public Phase03LoadTests(ITestOutputHelper output) => _output = output;

        // ------------------------------------------------------------------ task 6, criteria 8-10

        [Fact]
        public void PrintTheSixteenPlayerBandwidthTableAndHoldTheBudget()
        {
            _output.WriteLine(
                $"Bandwidth at {Humans} clients + {Bots} bots, {MapSpread} m map, "
                + $"{ProtocolConstants.SNAPSHOT_RATE} Hz, {MeasuredSeconds} s");
            _output.WriteLine("| Configuration | KB/s/client | Slots sent | Saving vs previous |");
            _output.WriteLine("|---|---|---|---|");

            LoadResult off = Measure(ReplicationConfig.Baseline, useInterest: false);
            LoadResult interest = Measure(
                new ReplicationConfig { UseVelocityCulling = false, DropStaleDeadActors = false },
                useInterest: true);
            LoadResult full = Measure(ReplicationConfig.Shipped, useInterest: true);

            Row("no interest management", off, null);
            Row("+ interest management", interest, off);
            Row("+ velocity culling, stale-corpse drop", full, interest);

            // Criterion 8: at most 8 KB/s per client, at the full 16 players plus 32 bots.
            Assert.True(full.KilobytesPerSecond <= 8.0,
                        $"criterion 8: {full.KilobytesPerSecond:F2} KB/s exceeds the 8 KB/s budget");

            // Every optimization must actually pay. A flag that costs code and saves nothing is
            // worse than no flag, and the only way to know is to compare the two runs.
            Assert.True(full.KilobytesPerSecond < interest.KilobytesPerSecond,
                        "the task-5 optimizations should be cheaper than interest alone");

            void Row(string label, LoadResult result, LoadResult? previous)
            {
                string saving = previous is { } p
                    ? $"{100.0 * (p.KilobytesPerSecond - result.KilobytesPerSecond) / p.KilobytesPerSecond:F1}%"
                    : "—";
                _output.WriteLine(
                    $"| {label} | {result.KilobytesPerSecond:F2} | {result.SlotsSent} | {saving} |");
            }
        }

        [Fact]
        public void SixteenClientsJoiningAtOnceAreAllServedInTheSameSnapshot()
        {
            var manager = new InterestManager();
            var view = new WorldSnapshot();
            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);
            world.ServerTick = 1;

            manager.BeginSnapshot();

            int served = 0;
            for (ushort viewer = 1; viewer <= Humans; viewer++)
                if (manager.BuildView(viewer, world, 1, view) && view.ActorCount > 0) served++;

            Assert.Equal(Humans, served);
        }

        [Fact]
        public void ContinuousJoiningAndLeavingLeaksNoPairTableEntries()
        {
            // Task 6's third scenario. The interest and spawn tables are keyed on
            // (viewer, target) pairs, so without a despawn path they grow for the whole match.
            var manager = new InterestManager();
            var spawnAcks = new SpawnAckTracker();
            var history = new HitboxHistory();
            var pool = new ActorIdPool(ProtocolConstants.MAX_ACTORS, quarantineSeconds: 0f);
            var audit = new ServerStateAudit(pool, history, manager, spawnAcks);

            var view = new WorldSnapshot();
            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);

            for (uint snapshot = 1; snapshot <= 200; snapshot++)
            {
                manager.BeginSnapshot();

                for (ushort viewer = 1; viewer <= Humans; viewer++)
                {
                    manager.BuildView(viewer, world, snapshot, view, spawnAcks);
                    for (int i = 0; i < view.ActorCount; i++)
                        spawnAcks.MarkSpawnSent(viewer, view.Actors[i].ActorId);
                }

                // One player leaves and rejoins on the same id every ten snapshots.
                if (snapshot % 10 != 0) continue;

                const ushort churning = 3;
                manager.Forget(churning);
                spawnAcks.Forget(churning);
                history.Forget(churning);
                pool.Release(churning, snapshot);
                pool.TryAcquire(snapshot, out _);
            }

            ServerStateSnapshot state = audit.Capture();

            // The bound is the worst case the protocol allows: 16 viewers x 48 targets. What
            // is being pinned is that it does not GROW past it over 200 snapshots of churn.
            Assert.True(state.InterestPairs <= Humans * Actors,
                        $"interest pairs grew to {state.InterestPairs}");
            Assert.True(state.SpawnAckPairs <= Humans * Actors,
                        $"spawn-ack pairs grew to {state.SpawnAckPairs}");
        }

        // ------------------------------------------------------------------ trap 1

        [Fact]
        public void FiveMatchesBackToBackLeaveACleanServerEveryTime()
        {
            var manager = new InterestManager();
            var spawnAcks = new SpawnAckTracker();
            var history = new HitboxHistory();
            var pool = new ActorIdPool(ProtocolConstants.MAX_ACTORS);
            var audit = new ServerStateAudit(pool, history, manager, spawnAcks);

            var view = new WorldSnapshot();
            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);

            for (int round = 1; round <= 5; round++)
            {
                // Play: build views, announce spawns, capture hitbox history — everything that
                // puts an entry in a per-actor table.
                for (uint snapshot = 1; snapshot <= 40; snapshot++)
                {
                    manager.BeginSnapshot();

                    for (ushort viewer = 1; viewer <= Humans; viewer++)
                    {
                        manager.BuildView(viewer, world, snapshot, view, spawnAcks);
                        for (int i = 0; i < view.ActorCount; i++)
                            spawnAcks.MarkSpawnSent(viewer, view.Actors[i].ActorId);
                    }

                    for (ushort actor = 1; actor <= Actors; actor++)
                        if (manager.IsShootable(actor))
                            history.Capture(snapshot, actor, HitboxSet.Humanoid(Vec3.Zero));
                }

                for (int i = 0; i < Humans; i++) pool.TryAcquire(round * 100f, out _);

                ServerStateSnapshot dirty = audit.Capture();
                Assert.True(dirty.InterestPairs > 0, $"round {round} built no interest state");
                Assert.True(dirty.HitboxHistoryActors > 0, $"round {round} captured no history");

                audit.ResetForNewMatch();

                ServerStateSnapshot clean = audit.Capture();
                Assert.True(clean.IsClean, $"round {round} left state behind — {clean}");
                Assert.Equal(ProtocolConstants.MAX_ACTORS, clean.ActorIdsFree);
            }
        }

        [Fact]
        public void TheAuditReportsWhatIsActuallyLeftRatherThanJustPassOrFail()
        {
            var manager = new InterestManager();
            var spawnAcks = new SpawnAckTracker();
            var history = new HitboxHistory();
            var pool = new ActorIdPool(8);
            var audit = new ServerStateAudit(pool, history, manager, spawnAcks, () => 3);

            spawnAcks.MarkSpawnSent(1, 2);
            history.Capture(1, 2, HitboxSet.Humanoid(Vec3.Zero));
            pool.TryAcquire(0f, out _);

            ServerStateSnapshot state = audit.Capture();

            Assert.False(state.IsClean);
            Assert.Equal(1, state.SpawnAckPairs);
            Assert.Equal(1, state.HitboxHistoryActors);
            Assert.Equal(1, state.ActorIdsInUse);
            Assert.Equal(3, state.Sessions);
            Assert.Contains("spawnAckPairs=1", state.ToString());
        }

        // ------------------------------------------------------------------ task 5 optimizations

        [Fact]
        public void VelocityIsSuppressedBelowNearAndKeptForNeighbours()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            var velocity = new Vec3(6f, 0f, 0f);
            world.Add(Moving(1, Vec3.Zero, velocity));                       // the viewer
            world.Add(Moving(2, new Vec3(10f, 0f, 0f), velocity));           // Near
            world.Add(Moving(3, new Vec3(200f, 0f, 0f), velocity));          // Far

            manager.BeginSnapshot();
            Assert.True(manager.BuildView(1, world, 1, view));

            Assert.NotEqual(0, view.Actors[view.IndexOf(2)].VelX);
            Assert.Equal(0, view.Actors[view.IndexOf(3)].VelX);
            Assert.True(manager.VelocityFieldsCulled > 0);
        }

        [Fact]
        public void OneViewersCullingDoesNotStripVelocityFromAnothersView()
        {
            // The view is built from a COPY. Mutating the world entry in place would apply a
            // distant viewer's decision to the client standing right next to the actor.
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            var velocity = new Vec3(6f, 0f, 0f);
            world.Add(Moving(1, new Vec3(300f, 0f, 0f), velocity));   // far viewer
            world.Add(Moving(2, Vec3.Zero, velocity));                // near viewer
            world.Add(Moving(3, new Vec3(5f, 0f, 0f), velocity));     // the target

            manager.BeginSnapshot();
            manager.BuildView(1, world, 1, view);                     // sees 3 at Far
            manager.BuildView(2, world, 1, view);                     // sees 3 at Near

            Assert.NotEqual(0, view.Actors[view.IndexOf(3)].VelX);
        }

        [Fact]
        public void TurningVelocityCullingOffKeepsEveryVelocity()
        {
            var manager = new InterestManager
            {
                Config = new ReplicationConfig { UseVelocityCulling = false },
            };
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            world.Add(Moving(1, Vec3.Zero, new Vec3(6f, 0f, 0f)));
            world.Add(Moving(2, new Vec3(200f, 0f, 0f), new Vec3(6f, 0f, 0f)));

            manager.BeginSnapshot();
            manager.BuildView(1, world, 1, view);

            Assert.NotEqual(0, view.Actors[view.IndexOf(2)].VelX);
            Assert.Equal(0, manager.VelocityFieldsCulled);
        }

        [Fact]
        public void ACorpseIsSentForThreeSecondsAndThenDropped()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            world.Add(InterestManagementTests.Actor(1, Vec3.Zero, 0));
            world.Add(Dead(2, new Vec3(5f, 0f, 0f)));

            int holdSnapshots =
                (int)(ProtocolConstants.SNAPSHOT_RATE * ReplicationConfig.Shipped.DeadActorHoldSeconds);

            for (uint snapshot = 1; snapshot <= holdSnapshots; snapshot++)
            {
                manager.BeginSnapshot();
                manager.BuildView(1, world, snapshot, view);

                // Still present while the client may still be settling its ragdoll.
                Assert.True(view.IndexOf(2) >= 0, $"corpse dropped early at snapshot {snapshot}");
            }

            manager.BeginSnapshot();
            manager.BuildView(1, world, (uint)holdSnapshots + 1, view);

            Assert.True(view.IndexOf(2) < 0, "corpse should have been dropped by now");
            Assert.True(manager.EntriesDroppedDead > 0);
        }

        [Fact]
        public void ARespawnBringsTheActorStraightBack()
        {
            // Forgetting to clear the death time makes a respawned player invisible, with no
            // error anywhere.
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            world.Add(InterestManagementTests.Actor(1, Vec3.Zero, 0));
            world.Add(Dead(2, new Vec3(5f, 0f, 0f)));

            int hold =
                (int)(ProtocolConstants.SNAPSHOT_RATE * ReplicationConfig.Shipped.DeadActorHoldSeconds);

            for (uint snapshot = 1; snapshot <= hold + 5; snapshot++)
            {
                manager.BeginSnapshot();
                manager.BuildView(1, world, snapshot, view);
            }

            Assert.True(view.IndexOf(2) < 0);

            world.Actors[1] = InterestManagementTests.Actor(2, new Vec3(5f, 0f, 0f), 0);
            manager.BeginSnapshot();
            manager.BuildView(1, world, (uint)hold + 6, view);

            Assert.True(view.IndexOf(2) >= 0, "a respawned actor must reappear immediately");
        }

        [Fact]
        public void AnActorThatDiesOutOfRangeIsNotHeldInFullWhenItComesBack()
        {
            // Liveness is tracked before the cull test. Otherwise an actor that dies while
            // beyond the cull radius records no death time, and reappears at 20 Hz as a corpse
            // for three seconds — the opposite of the optimization.
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            world.Add(InterestManagementTests.Actor(1, Vec3.Zero, 0, yawDegrees: 180f));
            world.Add(Dead(2, new Vec3(2000f, 0f, 0f)));

            int hold =
                (int)(ProtocolConstants.SNAPSHOT_RATE * ReplicationConfig.Shipped.DeadActorHoldSeconds);

            for (uint snapshot = 1; snapshot <= hold + 1; snapshot++)
            {
                manager.BeginSnapshot();
                manager.BuildView(1, world, snapshot, view);
            }

            // Walks back into range as a corpse.
            world.Actors[1] = Dead(2, new Vec3(5f, 0f, 0f));
            manager.BeginSnapshot();
            manager.BuildView(1, world, (uint)hold + 2, view);

            Assert.True(view.IndexOf(2) < 0, "a long-dead actor should not be re-sent on approach");
        }

        [Fact]
        public void ForgettingAnActorForgetsWhetherItWasDead()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            var view = new WorldSnapshot();

            world.Add(InterestManagementTests.Actor(1, Vec3.Zero, 0));
            world.Add(Dead(2, new Vec3(5f, 0f, 0f)));

            int hold =
                (int)(ProtocolConstants.SNAPSHOT_RATE * ReplicationConfig.Shipped.DeadActorHoldSeconds);

            for (uint snapshot = 1; snapshot <= hold + 1; snapshot++)
            {
                manager.BeginSnapshot();
                manager.BuildView(1, world, snapshot, view);
            }

            manager.Forget(2);

            // Id 2 is recycled to a live actor. Inheriting the previous occupant's death time
            // would drop the new one from every snapshot forever.
            world.Actors[1] = InterestManagementTests.Actor(2, new Vec3(5f, 0f, 0f), 0);
            manager.BeginSnapshot();
            manager.BuildView(1, world, (uint)hold + 2, view);

            Assert.True(view.IndexOf(2) >= 0);
        }

        // ------------------------------------------------------------------ helpers

        private static ActorSnapshotEntry Moving(ushort actorId, in Vec3 position, in Vec3 velocity)
            => SnapshotBuilder.Capture(
                actorId, in position, yawDegrees: 0f, pitchDegrees: 0f, velocity: in velocity,
                stateFlags: ActorStateFlags.IsAlive, health: 100f,
                weaponId: 0, ammoInClip: 30, team: 0);

        private static ActorSnapshotEntry Dead(ushort actorId, in Vec3 position)
            => SnapshotBuilder.Capture(
                actorId, in position, yawDegrees: 0f, pitchDegrees: 0f, velocity: Vec3.Zero,
                stateFlags: ActorStateFlags.IsRagdoll, health: 0f,
                weaponId: 0, ammoInClip: 0, team: 1);

        private readonly struct LoadResult
        {
            public LoadResult(double kilobytesPerSecond, long slotsSent)
            {
                KilobytesPerSecond = kilobytesPerSecond;
                SlotsSent          = slotsSent;
            }

            public double KilobytesPerSecond { get; }
            public long SlotsSent { get; }
        }

        private static LoadResult Measure(ReplicationConfig config, bool useInterest)
        {
            int snapshots = MeasuredSeconds * ProtocolConstants.SNAPSHOT_RATE;

            var manager = new InterestManager { Config = config };
            var view = new WorldSnapshot();
            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var body = new byte[ServerPayloadWriter.MaxSnapshotBodySize];

            var encoders = new DeltaEncoder[Humans];
            for (int i = 0; i < Humans; i++) encoders[i] = new DeltaEncoder();

            WorldSnapshot world = InterestManagementTests.BuildWorld(Actors, MapSpread);
            long totalBytes = 0;
            long slotsSent = 0;

            for (uint snapshot = 1; snapshot <= snapshots; snapshot++)
            {
                world.ServerTick = snapshot;
                Drift(world, snapshot);
                manager.BeginSnapshot();

                for (int i = 0; i < Humans; i++)
                {
                    WorldSnapshot outgoing;
                    if (useInterest)
                    {
                        manager.BuildView((ushort)(i + 1), world, snapshot, view);
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

                    if (snapshot > 2) encoders[i].OnClientAck(snapshot - 2);
                }
            }

            // Kilobytes, on the same 1024 basis phases 01 and 02 reported, so the three
            // figures in the report are directly comparable.
            return new LoadResult(
                (double)totalBytes / MeasuredSeconds / Humans / 1024.0, slotsSent);
        }

        /// <summary>
        /// The same movement mix phases 01 and 02 measured against, so the three bandwidth
        /// figures describe the same world. A tenth of the actors are corpses, which is what
        /// gives the stale-corpse drop something to remove.
        /// </summary>
        private static void Drift(WorldSnapshot world, uint snapshot)
        {
            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry actor = ref world.Actors[i];

                if (actor.ActorId % 10 == 7)
                {
                    actor.StateFlags = ActorStateFlags.IsRagdoll;
                    actor.Health = 0;
                    continue;
                }

                int behaviour = actor.ActorId % 5;
                if (behaviour == 4) continue;

                Vec3 position = SnapshotBuilder.UnpackPosition(in actor);
                float step = 4f / ProtocolConstants.SNAPSHOT_RATE;

                Vec3 moved = behaviour == 3
                    ? new Vec3(
                        position.X + step * (float)Math.Sin(snapshot * 0.3 + actor.ActorId),
                        position.Y,
                        position.Z + step * (float)Math.Cos(snapshot * 0.3 + actor.ActorId))
                    : new Vec3(position.X + step, position.Y, position.Z);

                actor.PosX = Quantize.PackPos(moved.X);
                actor.PosZ = Quantize.PackPos(moved.Z);
                actor.Yaw = Quantize.PackYaw(snapshot * 3f + actor.ActorId);
                actor.VelX = Quantize.PackVel(4f);
            }
        }
    }
}
