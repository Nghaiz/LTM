using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-02 task 1 and criterion 1 (interest management cuts bandwidth by at least 40%).
    /// </summary>
    public sealed class InterestManagementTests
    {
        private const byte TeamA = 0;
        private const byte TeamB = 1;

        // ------------------------------------------------------------------ banding

        [Fact]
        public void AnActorSeesItselfAtNear()
        {
            var manager = new InterestManager();
            ActorSnapshotEntry viewer = Actor(1, new Vec3(0f, 0f, 0f), TeamA);

            Assert.Equal(InterestLevel.Near, manager.Evaluate(in viewer, in viewer));
        }

        [Theory]
        [InlineData(10f, InterestLevel.Near)]
        [InlineData(59f, InterestLevel.Near)]
        [InlineData(61f, InterestLevel.Mid)]
        [InlineData(149f, InterestLevel.Mid)]
        [InlineData(151f, InterestLevel.Far)]
        [InlineData(299f, InterestLevel.Far)]
        [InlineData(499f, InterestLevel.Far)]
        public void DistanceBandsMatchTheArchitectureTable(float distance, InterestLevel expected)
        {
            var manager = new InterestManager();
            ActorSnapshotEntry viewer = Actor(1, Vec3.Zero, TeamA);
            ActorSnapshotEntry target = Actor(2, new Vec3(distance, 0f, 0f), TeamB);

            Assert.Equal(expected, manager.Evaluate(in viewer, in target));
        }

        [Fact]
        public void NothingInsideFiveHundredMetresIsEverCulled()
        {
            // Option 2 from task 1, and the whole reason it was chosen: an actor that leaves
            // the set entirely is one the client holds at a stale position until it returns and
            // teleports. Keeping everything at Far makes that bug unwriteable.
            var manager = new InterestManager();
            ActorSnapshotEntry viewer = Actor(1, Vec3.Zero, TeamA);

            for (float d = 5f; d < InterestManager.CullRadius; d += 5f)
            {
                ActorSnapshotEntry target = Actor(2, new Vec3(d, 0f, 0f), TeamB);
                Assert.NotEqual(InterestLevel.Culled, manager.Evaluate(in viewer, in target));
            }
        }

        [Fact]
        public void AnEnemyBeyondTheCullRadiusAndOutOfViewIsCulled()
        {
            var manager = new InterestManager();

            // Facing +Z; the target is off to +X, so well outside the cone.
            ActorSnapshotEntry viewer = Actor(1, Vec3.Zero, TeamA, yawDegrees: 0f);
            ActorSnapshotEntry target = Actor(2, new Vec3(700f, 0f, 0f), TeamB);

            Assert.Equal(InterestLevel.Culled, manager.Evaluate(in viewer, in target));
        }

        [Fact]
        public void AScopedTargetBeyondTheCullRadiusSurvivesAsFar()
        {
            var manager = new InterestManager();

            // Facing +Z, target straight ahead at 700 m — a sniper scope case.
            ActorSnapshotEntry viewer = Actor(1, Vec3.Zero, TeamA, yawDegrees: 0f);
            ActorSnapshotEntry target = Actor(2, new Vec3(0f, 0f, 700f), TeamB);

            Assert.Equal(InterestLevel.Far, manager.Evaluate(in viewer, in target));
        }

        [Fact]
        public void ATeammateStandingNextToYouIsStillNear()
        {
            // "Teammates are always at Mid or better" is a FLOOR. Returning Mid directly, before
            // the distance ladder, is the obvious way to write it and demotes the teammate you
            // are standing beside from 20 Hz to 10 Hz — the people whose movement you can see
            // most precisely would be the ones updating least often.
            var manager = new InterestManager();
            ActorSnapshotEntry viewer = Actor(1, Vec3.Zero, TeamA);
            ActorSnapshotEntry mate = Actor(2, new Vec3(5f, 0f, 0f), TeamA);

            Assert.Equal(InterestLevel.Near, manager.Evaluate(in viewer, in mate));
        }

        [Fact]
        public void TheShootableThresholdCoversFullWeaponRange()
        {
            // The R6 filter decides who gets hitbox history, i.e. who can be lag-compensated.
            // Both the phase document and protocol-spec section 7.3 say "Near/Mid zone", and Mid
            // ends at 150 m while a rifle reaches 300 m — so a target in the outer half of every
            // weapon's range would silently fall back to its present pose and the high-ping
            // player who is supposed to be compensated simply misses, with no error anywhere.
            Assert.True(InterestManager.CullRadius >= WeaponConfig.Rifle.Range,
                "the shootable band must reach at least as far as a weapon can shoot");
            Assert.Equal(InterestLevel.Far, InterestManager.ShootableThreshold);

            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));
            world.Add(Actor(2, new Vec3(250f, 0f, 0f), TeamB));   // inside rifle range, past Mid
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);

            Assert.True(manager.IsShootable(2),
                "a target at 250 m is inside a 300 m weapon and must keep hitbox history");
        }

        [Fact]
        public void AnActorNobodyIsNearIsNotShootable()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA, yawDegrees: 0f));
            world.Add(Actor(2, new Vec3(1500f, 0f, 0f), TeamB));
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);

            Assert.False(manager.IsShootable(2));
        }

        [Fact]
        public void ATeammateAtTwoHundredFiftyMetresIsHeldAtMid()
        {
            // The minimap and command map show teammates wherever they are, so a teammate
            // updating at 4 Hz would visibly crawl between positions.
            var manager = new InterestManager();
            ActorSnapshotEntry viewer = Actor(1, Vec3.Zero, TeamA);
            ActorSnapshotEntry mate = Actor(2, new Vec3(250f, 0f, 0f), TeamA);
            ActorSnapshotEntry enemy = Actor(3, new Vec3(250f, 0f, 0f), TeamB);

            Assert.Equal(InterestLevel.Mid, manager.Evaluate(in viewer, in mate));
            Assert.Equal(InterestLevel.Far, manager.Evaluate(in viewer, in enemy));
        }

        // ------------------------------------------------------------------ send rates

        [Fact]
        public void NearSendsEverySnapshot()
        {
            var manager = new InterestManager();
            int sent = CountSends(manager, InterestLevel.Near, snapshots: 20);

            Assert.Equal(20, sent);
        }

        [Fact]
        public void MidSendsAtTenHertz()
        {
            var manager = new InterestManager();
            int sent = CountSends(manager, InterestLevel.Mid, snapshots: 20);

            // Every 2nd snapshot of a 20 Hz stream.
            Assert.Equal(10, sent);
        }

        [Fact]
        public void FarSendsAtFourHertz()
        {
            var manager = new InterestManager();
            int sent = CountSends(manager, InterestLevel.Far, snapshots: 20);

            Assert.Equal(4, sent);
        }

        [Fact]
        public void CulledNeverSends()
        {
            var manager = new InterestManager();

            Assert.Equal(0, CountSends(manager, InterestLevel.Culled, snapshots: 20));
        }

        [Fact]
        public void TheRateLimitIsKeyedOnSnapshotIndexNotServerTick()
        {
            // The trap: snapshots go out at 20 Hz while the simulation runs at 30, so
            // consecutive snapshots are one or two TICKS apart. Feeding ticks in would make
            // "every 5th" fire at roughly 8 Hz instead of 4 — all the architecture rates wrong,
            // and wrong in a direction that still looks like it works.
            var byIndex = new InterestManager();
            var byTick = new InterestManager();

            int indexSends = 0, tickSends = 0;
            uint tick = 0;

            for (uint snapshot = 0; snapshot < 20; snapshot++)
            {
                if (byIndex.ShouldSend(1, 2, InterestLevel.Far, snapshot)) indexSends++;

                // What the server tick actually does between snapshots at 30 Hz sim / 20 Hz send.
                tick += snapshot % 2 == 0 ? 2u : 1u;
                if (byTick.ShouldSend(1, 2, InterestLevel.Far, tick)) tickSends++;
            }

            Assert.Equal(4, indexSends);
            Assert.True(tickSends > indexSends,
                "keying on the server tick over-sends; that is the bug this guards");
        }

        [Fact]
        public void ShouldSendRecordsSoOneSnapshotCannotSendTwice()
        {
            var manager = new InterestManager();

            Assert.True(manager.ShouldSend(1, 2, InterestLevel.Near, 5));
            Assert.False(manager.ShouldSend(1, 2, InterestLevel.Near, 5));
        }

        [Fact]
        public void DifferentViewersHaveIndependentRates()
        {
            var manager = new InterestManager();

            Assert.True(manager.ShouldSend(1, 99, InterestLevel.Far, 0));
            Assert.True(manager.ShouldSend(2, 99, InterestLevel.Far, 0));
            Assert.False(manager.ShouldSend(1, 99, InterestLevel.Far, 1));
        }

        // ------------------------------------------------------------------ trap 2

        [Fact]
        public void ForgettingADespawnedActorDropsBothItsViewerAndTargetRows()
        {
            // Trap 2. Without this the pair table grows for the whole match: ids keep being
            // allocated as players and bots come and go, and nothing ever removes a row.
            var manager = new InterestManager();

            manager.ShouldSend(1, 2, InterestLevel.Near, 0);
            manager.ShouldSend(2, 1, InterestLevel.Near, 0);
            manager.ShouldSend(3, 4, InterestLevel.Near, 0);
            Assert.Equal(3, manager.TrackedPairCount);

            manager.Forget(2);

            Assert.Equal(1, manager.TrackedPairCount);
        }

        [Fact]
        public void ThePairTableDoesNotGrowAcrossManySnapshots()
        {
            var manager = new InterestManager();

            for (uint snapshot = 0; snapshot < 500; snapshot++)
                for (ushort target = 1; target <= 48; target++)
                    manager.ShouldSend(1, target, InterestLevel.Far, snapshot);

            Assert.Equal(48, manager.TrackedPairCount);
        }

        // ------------------------------------------------------------------ view building

        [Fact]
        public void BuildViewReportsFalseForAViewerNotInTheWorld()
        {
            var manager = new InterestManager();
            WorldSnapshot world = BuildWorld(actorCount: 4, spread: 10f);
            var view = new WorldSnapshot();

            Assert.False(manager.BuildView(999, world, 0, view));
        }

        [Fact]
        public void AViewerAlwaysReceivesItself()
        {
            var manager = new InterestManager();
            WorldSnapshot world = BuildWorld(actorCount: 48, spread: 200f);
            var view = new WorldSnapshot();

            for (uint snapshot = 0; snapshot < 10; snapshot++)
            {
                manager.BeginSnapshot();
                Assert.True(manager.BuildView(1, world, snapshot, view));
                Assert.True(view.IndexOf(1) >= 0, $"viewer missing from its own view at {snapshot}");
            }
        }

        [Fact]
        public void TheViewCarriesTheWorldsServerTick()
        {
            var manager = new InterestManager();
            WorldSnapshot world = BuildWorld(actorCount: 8, spread: 20f);
            world.ServerTick = 4242;
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);

            Assert.Equal(4242u, view.ServerTick);
        }

        [Fact]
        public void MaxLevelAmongHumanPlayersTakesTheHighestAcrossViewers()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));                    // human, far from the bot
            world.Add(Actor(2, new Vec3(400f, 0f, 0f), TeamA));       // human, next to the bot
            world.Add(Actor(50, new Vec3(410f, 0f, 0f), TeamB));      // the bot

            var view = new WorldSnapshot();
            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);
            manager.BuildView(2, world, 0, view);

            // Viewer 1 sees the bot at Far; viewer 2 sees it at Near. The relevance filter and
            // the AI LOD both need the maximum, or an actor somebody is standing next to gets
            // treated as unreachable.
            Assert.Equal(InterestLevel.Near, manager.MaxLevelAmongHumanPlayers(50));
        }

        [Fact]
        public void MaxLevelIsClearedBetweenSnapshots()
        {
            // BeginSnapshot per snapshot, not per viewer. Calling it per viewer would leave the
            // map holding only the last viewer's opinion, silently stripping hitbox history
            // from every actor except those near whichever client happened to be encoded last.
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));
            world.Add(Actor(2, new Vec3(10f, 0f, 0f), TeamB));
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);
            Assert.Equal(InterestLevel.Near, manager.MaxLevelAmongHumanPlayers(2));

            manager.BeginSnapshot();
            Assert.Equal(InterestLevel.Culled, manager.MaxLevelAmongHumanPlayers(2));
        }

        [Fact]
        public void AnUnknownActorReportsCulled()
        {
            var manager = new InterestManager();

            Assert.Equal(InterestLevel.Culled, manager.MaxLevelAmongHumanPlayers(1234));
        }

        // ------------------------------------------------------------------ criterion 1

        [Fact]
        public void MostActorSlotsCarryNoFreshStateOnceRatesAreApplied()
        {
            // The update-rate cut. It is NOT the same number as the bandwidth cut criterion 1
            // is graded on — a held slot still costs 3 bytes — so the byte figure is measured
            // separately in Phase02MeasurementTests.
            var manager = new InterestManager();
            WorldSnapshot world = BuildWorld(actorCount: 48, spread: 400f);
            var view = new WorldSnapshot();

            for (uint snapshot = 0; snapshot < 200; snapshot++)
            {
                manager.BeginSnapshot();
                for (ushort viewer = 1; viewer <= 8; viewer++)
                    manager.BuildView(viewer, world, snapshot, view);
            }

            Assert.True(manager.RefreshSavingPercent >= 40.0,
                $"expected at least 40% of slots to carry no fresh state, "
                + $"measured {manager.RefreshSavingPercent:F1}%");
        }

        [Fact]
        public void ARateLimitedActorIsOmittedRatherThanReEmitted()
        {
            // Omission, not a held copy at the previous values. Holding looks cheaper — the
            // change mask would come out empty, so 3 bytes instead of a later 20-byte re-send —
            // but the baseline is the client's ACKED snapshot, roughly two behind, so a held
            // entry usually still differs from it and encodes a full delta anyway. Measured
            // over 30 s at 48 actors: omitting cut bandwidth 25.5%, holding cut it 11.0%.
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));
            world.Add(Actor(2, new Vec3(200f, 0f, 0f), TeamB));   // Far: due every 5th snapshot
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);
            Assert.True(view.IndexOf(2) >= 0, "the first snapshot should carry the actor");

            manager.BeginSnapshot();
            manager.BuildView(1, world, 1, view);

            Assert.True(view.IndexOf(2) < 0, "a rate-limited actor should be absent, not repeated");
            Assert.Equal(1, manager.EntriesHeld);
        }

        [Fact]
        public void AnOmittedActorReappearsOnItsNextDueSnapshot()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));
            world.Add(Actor(2, new Vec3(200f, 0f, 0f), TeamB));
            var view = new WorldSnapshot();

            var carried = new bool[6];
            for (uint snapshot = 0; snapshot < 6; snapshot++)
            {
                manager.BeginSnapshot();
                manager.BuildView(1, world, snapshot, view);
                carried[snapshot] = view.IndexOf(2) >= 0;
            }

            Assert.True(carried[0]);
            Assert.True(carried[5]);
            for (int snapshot = 1; snapshot <= 4; snapshot++) Assert.False(carried[snapshot]);
        }

        [Fact]
        public void ACulledActorIsCountedApartFromARateLimitedOne()
        {
            var manager = new InterestManager();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA, yawDegrees: 0f));
            world.Add(Actor(2, new Vec3(2000f, 0f, 0f), TeamB));   // past the cull radius
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view);

            Assert.Equal(1, manager.EntriesCulled);
            Assert.Equal(0, manager.EntriesHeld);
            Assert.True(view.IndexOf(2) < 0);
        }

        [Fact]
        public void ResetClearsStatisticsAndPairs()
        {
            var manager = new InterestManager();
            manager.ShouldSend(1, 2, InterestLevel.Near, 0);

            manager.Reset();

            Assert.Equal(0, manager.TrackedPairCount);
            Assert.Equal(0, manager.EntriesConsidered);
            Assert.Equal(0, manager.EntriesRefreshed);
            Assert.Equal(0.0, manager.RefreshSavingPercent);
        }

        // ------------------------------------------------------------------ criterion 10

        [Fact]
        public void AnActorIsHeldBackUntilItsSpawnHasBeenSent()
        {
            // Criterion 10 / trap 8. S_SPAWN_ACTOR is reliable-ordered on channel 2 and
            // snapshots are unreliable-sequenced on channel 1, so a snapshot naming an actor
            // can overtake the spawn that introduced it.
            var manager = new InterestManager();
            var gate = new SpawnAckTracker();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));
            world.Add(Actor(2, new Vec3(5f, 0f, 0f), TeamB));
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view, gate);
            Assert.True(view.IndexOf(2) < 0, "actor 2 leaked into a snapshot before its spawn");

            gate.MarkSpawnSent(1, 2);

            manager.BeginSnapshot();
            manager.BuildView(1, world, 1, view, gate);
            Assert.True(view.IndexOf(2) >= 0, "actor 2 never appeared after its spawn was sent");
        }

        [Fact]
        public void TheViewerItselfIsNeverGatedByTheSpawnTracker()
        {
            // A client that cannot see its own player has nothing to predict against, and the
            // spawn it is waiting for is the one announcing itself.
            var manager = new InterestManager();
            var gate = new SpawnAckTracker();
            var world = new WorldSnapshot();
            world.Add(Actor(1, Vec3.Zero, TeamA));
            var view = new WorldSnapshot();

            manager.BeginSnapshot();
            manager.BuildView(1, world, 0, view, gate);

            Assert.True(view.IndexOf(1) >= 0);
        }

        // ------------------------------------------------------------------ helpers

        private static int CountSends(InterestManager manager, InterestLevel level, int snapshots)
        {
            int sent = 0;
            for (uint snapshot = 0; snapshot < snapshots; snapshot++)
                if (manager.ShouldSend(1, 2, level, snapshot)) sent++;

            return sent;
        }

        internal static ActorSnapshotEntry Actor(
            ushort actorId, in Vec3 position, byte team, float yawDegrees = 0f)
            => SnapshotBuilder.Capture(
                actorId, in position, yawDegrees, pitchDegrees: 0f, velocity: Vec3.Zero,
                stateFlags: ActorStateFlags.IsAlive, health: 100f,
                weaponId: 0, ammoInClip: 30, team: team);

        /// <summary>
        /// A world of actors spread deterministically over a square, teams alternating. Ids 1..8
        /// are treated as the human players by the tests that need one.
        /// </summary>
        internal static WorldSnapshot BuildWorld(int actorCount, float spread)
        {
            var world = new WorldSnapshot();
            uint state = 12345;

            for (ushort i = 1; i <= actorCount; i++)
            {
                state = state * 1664525u + 1013904223u;
                float x = (state >> 16 & 0xFF) / 255f * spread - spread * 0.5f;
                state = state * 1664525u + 1013904223u;
                float z = (state >> 16 & 0xFF) / 255f * spread - spread * 0.5f;

                world.Add(Actor(i, new Vec3(x, 0f, z), (byte)(i % 2)));
            }

            return world;
        }
    }
}
