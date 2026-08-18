using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-05 task 4: an over-budget snapshot sheds actors instead of being discarded whole.
    /// </summary>
    /// <remarks>
    /// Before this, <c>ServerPayloadWriter.WriteSnapshot</c> returned -1 at 64 actors and the
    /// tick loop logged an error and sent nothing — so the densest moment in a match was the one
    /// where clients received no world state at all.
    /// </remarks>
    public sealed class SnapshotSheddingTests
    {
        private const ushort Viewer = 1;
        private const int Budget = ServerPayloadWriter.MaxSnapshotBodySize;

        [Fact]
        public void AFullSnapshotAtSixtyFourActorsDoesNotFitOneDatagram()
        {
            // The premise. If this ever stops being true the shedding is dead weight, and a
            // test that silently stopped exercising anything is worse than no test.
            WorldSnapshot world = World(64);
            int full = SnapshotHeader.Size;

            for (int i = 0; i < world.ActorCount; i++)
                full += SnapshotMessage.EntrySize(SnapshotField.FullNoSeat);

            Assert.True(full > Budget, $"a 64-actor full snapshot is {full} B, budget is {Budget} B");
        }

        [Fact]
        public void AnOverBudgetSnapshotShedsActorsRatherThanDropping()
        {
            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: Viewer);
            var view = new WorldSnapshot();

            interest.BeginSnapshot();
            Assert.True(interest.BuildView(session, World(64), 1u, view, null, Budget));

            Assert.True(view.ActorCount > 0, "nothing was sent at all — this is the old bug");
            Assert.True(view.ActorCount < 64, "nothing was shed, so the budget was not applied");
            Assert.True(interest.LastViewShedCount > 0);

            // And the thing that actually matters: it encodes.
            Assert.True(Encode(view) > 0, "the shed view still did not frame");
        }

        [Fact]
        public void TheViewerIsNeverShed()
        {
            // A client that cannot see itself has nothing to reconcile its prediction against,
            // so it rubber-bands for as long as the world stays dense.
            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: Viewer);
            var view = new WorldSnapshot();

            for (uint snapshot = 1; snapshot <= 20; snapshot++)
            {
                interest.BeginSnapshot();
                interest.BuildView(session, World(64), snapshot, view, null, Budget);

                Assert.True(
                    view.TryFind(Viewer, out ActorSnapshotEntry _),
                    $"the viewer was shed from snapshot {snapshot}");
            }
        }

        [Fact]
        public void AShedActorIsNotStarvedAcrossSnapshots()
        {
            // Decision D6. Without the rotating cursor the same actors lose the budget race
            // every snapshot, so a handful of players are permanently invisible — and nothing
            // reports it, because a snapshot was produced every time.
            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: Viewer);
            var view = new WorldSnapshot();
            var seen = new HashSet<ushort>();

            const int window = 12;

            for (uint snapshot = 1; snapshot <= window; snapshot++)
            {
                interest.BeginSnapshot();
                interest.BuildView(session, World(64), snapshot, view, null, Budget);

                for (int i = 0; i < view.ActorCount; i++) seen.Add(view.Actors[i].ActorId);
            }

            Assert.Equal(64, seen.Count);
        }

        [Fact]
        public void AShedActorDoesNotAlsoLoseItsRateSlot()
        {
            // The anti-starvation property, at the level it is actually implemented: budget is
            // checked BEFORE due-ness, so an actor dropped for lack of bytes has not had a send
            // recorded against it. If it had, a Far actor would wait a further five snapshots on
            // top of losing the round — and the actors that lose are the same ones every time.
            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: Viewer);
            var view = new WorldSnapshot();
            WorldSnapshot world = World(64);

            interest.BeginSnapshot();
            interest.BuildView(session, world, 1u, view, null, Budget);

            int shed = interest.LastViewShedCount;
            Assert.True(shed > 0);

            // Every shed actor is still due on the very next snapshot.
            var delivered = new HashSet<ushort>();
            for (int i = 0; i < view.ActorCount; i++) delivered.Add(view.Actors[i].ActorId);

            interest.BeginSnapshot();
            interest.BuildView(session, world, 2u, view, null, Budget);

            var second = new HashSet<ushort>();
            for (int i = 0; i < view.ActorCount; i++) second.Add(view.Actors[i].ActorId);

            int recovered = 0;
            for (int id = 1; id <= 64; id++)
                if (!delivered.Contains((ushort)id) && second.Contains((ushort)id)) recovered++;

            Assert.Equal(shed, recovered);
        }

        [Fact]
        public void TheLeastInterestingActorsAreShedFirst()
        {
            // D6: Far before Mid, Mid before Near. Shedding in registry order instead would drop
            // whichever actors happened to be last in the scene, which on a real map means the
            // ones that spawned last rather than the ones nobody can see.
            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: Viewer);
            var view = new WorldSnapshot();

            // 40 close (Near), 40 distant (Far). Only 50 fit now that MaxEntrySize is 23, so
            // 31 must go — and every one of them has to come out of the distant group.
            var world = new WorldSnapshot { ServerTick = 1 };
            world.Add(Actor(Viewer, Vec3.Zero));

            for (int i = 0; i < 40; i++)
                world.Add(Actor((ushort)(100 + i), new Vec3(i * 0.5f, 0f, 5f)));

            for (int i = 0; i < 40; i++)
                world.Add(Actor((ushort)(200 + i), new Vec3(i * 0.5f, 0f, 250f)));

            interest.BeginSnapshot();
            interest.BuildView(session, world, 1u, view, null, Budget);

            var delivered = new HashSet<ushort>();
            for (int i = 0; i < view.ActorCount; i++) delivered.Add(view.Actors[i].ActorId);

            Assert.True(interest.LastViewShedCount > 0);

            for (int i = 0; i < 40; i++)
                Assert.True(
                    delivered.Contains((ushort)(100 + i)),
                    $"near actor {100 + i} was shed while distant actors survived");
        }

        [Fact]
        public void AFortyEightActorWorldShedsNothing()
        {
            // The phase-05 risk table's threshold, as an assertion. Shedding turns an overflow
            // from a loud dropped snapshot into a quiet degraded one, so a bandwidth regression
            // could otherwise hide behind "it always sends something".
            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: Viewer);
            var view = new WorldSnapshot();

            interest.BeginSnapshot();
            interest.BuildView(session, World(48), 1u, view, null, Budget);

            Assert.Equal(0, interest.LastViewShedCount);
            Assert.Equal(48, view.ActorCount);
        }

        [Fact]
        public void AnUnbudgetedBuildViewIsUnchanged()
        {
            // The three-argument overload every existing caller uses must still admit everything
            // it used to, or task 4 has quietly changed phase-02's measured bandwidth figures.
            var interest = new InterestManager();
            var view = new WorldSnapshot();

            interest.BeginSnapshot();
            Assert.True(interest.BuildView(Viewer, World(64), 1u, view));

            Assert.Equal(64, view.ActorCount);
            Assert.Equal(0, interest.LastViewShedCount);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A world of <paramref name="count"/> actors, all within Near of the viewer.</summary>
        private static WorldSnapshot World(int count)
        {
            var world = new WorldSnapshot { ServerTick = 1 };

            world.Add(Actor(Viewer, Vec3.Zero));

            for (int i = 1; i < count; i++)
            {
                // Spread across a 40 m square so they are distinct positions and all Near.
                float x = i % 8 * 5f;
                float z = i / 8 * 5f;
                world.Add(Actor((ushort)(i + 1), new Vec3(x, 0f, z)));
            }

            return world;
        }

        private static ActorSnapshotEntry Actor(ushort id, Vec3 position)
        {
            ActorSnapshotEntry entry = SnapshotBuilder.Capture(
                id, position, yawDegrees: 0f, pitchDegrees: 0f, velocity: Vec3.Zero,
                stateFlags: ActorStateFlags.IsAlive, health: 100f, weaponId: 1, ammoInClip: 30,
                team: 0);

            return entry;
        }

        /// <summary>Frames a view the way the tick loop does, and reports the byte count.</summary>
        private static int Encode(WorldSnapshot view)
        {
            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var body = new byte[ServerPayloadWriter.MaxSnapshotBodySize];

            return ServerPayloadWriter.WriteSnapshot(
                payload, body, new DeltaEncoder(), view, lastProcessedInputTick: 1);
        }
    }
}
