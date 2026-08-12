using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-02 task 2. The ring buffer itself, and the relevance filter that keeps it from
    /// costing 5,760 bounds reads a second (risk R6).
    /// </summary>
    public sealed class HitboxHistoryTests
    {
        [Fact]
        public void TheRingHoldsExactlyOneSecond()
        {
            Assert.Equal(30, HitboxHistory.Ticks);
            Assert.Equal(ProtocolConstants.SIM_TICK_RATE, HitboxHistory.Ticks);
        }

        [Fact]
        public void ACapturedFrameCanBeReadBack()
        {
            var history = new HitboxHistory();
            HitboxSet boxes = HitboxSet.Humanoid(new Vec3(3f, 0f, 7f));

            history.Capture(tick: 42, actorId: 9, in boxes);

            Assert.True(history.TryGetFrame(9, 42, out HitboxHistory.Frame frame));
            Assert.Equal(42u, frame.Tick);
            Assert.True(frame.Valid);
            Assert.Equal(boxes.Torso.Center.X, frame.Boxes.Torso.Center.X, 5);
        }

        [Fact]
        public void AnUnknownActorHasNoFrames()
        {
            var history = new HitboxHistory();

            Assert.False(history.TryGetFrame(9, 42, out _));
        }

        [Fact]
        public void ATickNeverCapturedIsNotReported()
        {
            var history = new HitboxHistory();
            history.Capture(tick: 42, actorId: 9, HitboxSet.Humanoid(Vec3.Zero));

            Assert.False(history.TryGetFrame(9, 43, out _));
        }

        [Fact]
        public void AnOverwrittenSlotIsRejectedRatherThanReturningLastSecondsPose()
        {
            // Ticks 100 and 130 share slot 10. A populated ring index proves nothing, so the
            // stored tick is verified — otherwise a rewind would resolve against a pose from a
            // full second earlier and hand the shooter a hit on empty air.
            var history = new HitboxHistory();
            history.Capture(100, 9, HitboxSet.Humanoid(new Vec3(0f, 0f, 0f)));
            history.Capture(130, 9, HitboxSet.Humanoid(new Vec3(50f, 0f, 0f)));

            Assert.False(history.TryGetFrame(9, 100, out _));
            Assert.True(history.TryGetFrame(9, 130, out HitboxHistory.Frame frame));
            Assert.Equal(50f, frame.Boxes.Torso.Center.X, 3);
        }

        [Fact]
        public void AFullSecondOfHistoryIsRetained()
        {
            var history = new HitboxHistory();
            for (uint tick = 100; tick < 130; tick++)
                history.Capture(tick, 9, HitboxSet.Humanoid(new Vec3(tick, 0f, 0f)));

            for (uint tick = 100; tick < 130; tick++)
                Assert.True(history.TryGetFrame(9, tick, out _), $"tick {tick} was evicted early");
        }

        [Fact]
        public void TheOldestFrameIsEvictedOnceTheRingWraps()
        {
            var history = new HitboxHistory();
            for (uint tick = 100; tick <= 130; tick++)
                history.Capture(tick, 9, HitboxSet.Humanoid(new Vec3(tick, 0f, 0f)));

            Assert.False(history.TryGetFrame(9, 100, out _));
            Assert.True(history.TryGetFrame(9, 101, out _));
        }

        [Fact]
        public void EveryRewindableTickIsInsideTheRing()
        {
            // The ring only needs to be longer than the maximum rewind. It is five times
            // longer, which is the margin that lets the relevance filter skip captures without
            // making a target unhittable the moment it becomes relevant again.
            Assert.True(HitboxHistory.Ticks > ProtocolConstants.MAX_REWIND_TICKS);
        }

        [Fact]
        public void EachActorGetsItsOwnRing()
        {
            var history = new HitboxHistory();
            history.Capture(50, 1, HitboxSet.Humanoid(new Vec3(1f, 0f, 0f)));
            history.Capture(50, 2, HitboxSet.Humanoid(new Vec3(2f, 0f, 0f)));

            Assert.True(history.TryGetFrame(1, 50, out HitboxHistory.Frame first));
            Assert.True(history.TryGetFrame(2, 50, out HitboxHistory.Frame second));
            Assert.Equal(1f, first.Boxes.Torso.Center.X, 3);
            Assert.Equal(2f, second.Boxes.Torso.Center.X, 3);
            Assert.Equal(2, history.TrackedActorCount);
        }

        [Fact]
        public void ForgettingAnActorDropsItsRing()
        {
            // Without this the dictionary keeps a 30-frame ring alive for every id ever
            // allocated — a slow leak with no symptom until the working set stops fitting.
            var history = new HitboxHistory();
            history.Capture(50, 1, HitboxSet.Humanoid(Vec3.Zero));

            history.Forget(1);

            Assert.Equal(0, history.TrackedActorCount);
            Assert.False(history.TryGetFrame(1, 50, out _));
        }

        [Fact]
        public void ClearDropsEverything()
        {
            var history = new HitboxHistory();
            history.Capture(50, 1, HitboxSet.Humanoid(Vec3.Zero));
            history.Capture(50, 2, HitboxSet.Humanoid(Vec3.Zero));

            history.Clear();

            Assert.Equal(0, history.TrackedActorCount);
            Assert.Equal(0, history.FramesCaptured);
        }

        // ------------------------------------------------------------------ risk R6

        [Fact]
        public void TheRelevanceFilterSkipsActorsNoHumanIsNear()
        {
            // protocol-spec.md section 7.3's mandatory optimization. A bot in the corner of the
            // map cannot be shot, so storing its pose 30 times a second is pure cost.
            var interest = new InterestManager();
            var history = new HitboxHistory();

            var world = new WorldSnapshot();
            world.Add(InterestManagementTests.Actor(1, Vec3.Zero, 0));                     // human
            world.Add(InterestManagementTests.Actor(2, new Vec3(20f, 0f, 0f), 1));         // nearby
            world.Add(InterestManagementTests.Actor(3, new Vec3(1200f, 0f, 0f), 1));       // far corner

            var view = new WorldSnapshot();
            interest.BeginSnapshot();
            interest.BuildView(1, world, 0, view);

            CaptureRelevant(history, interest, world, tick: 10);

            Assert.True(history.TryGetFrame(2, 10, out _));
            Assert.False(history.TryGetFrame(3, 10, out _));
            Assert.Equal(2, history.FramesCaptured);   // the human and the nearby enemy
        }

        [Fact]
        public void TheRelevanceFilterCutsCapturesSubstantiallyAtScale()
        {
            var interest = new InterestManager();
            var filtered = new HitboxHistory();
            var unfiltered = new HitboxHistory();

            // 8 players clustered in one corner, 40 bots scattered over a large map.
            var world = new WorldSnapshot();
            for (ushort id = 1; id <= 8; id++)
                world.Add(InterestManagementTests.Actor(id, new Vec3(id * 3f, 0f, 0f), 0));

            uint state = 99;
            for (ushort id = 9; id <= 48; id++)
            {
                state = state * 1664525u + 1013904223u;
                float x = (state >> 16 & 0x3FF) + 400f;
                state = state * 1664525u + 1013904223u;
                float z = (state >> 16 & 0x3FF) + 400f;
                world.Add(InterestManagementTests.Actor(id, new Vec3(x, 0f, z), 1));
            }

            var view = new WorldSnapshot();
            interest.BeginSnapshot();
            for (ushort viewer = 1; viewer <= 8; viewer++) interest.BuildView(viewer, world, 0, view);

            CaptureRelevant(filtered, interest, world, tick: 10);
            for (int i = 0; i < world.ActorCount; i++)
                unfiltered.Capture(10, world.Actors[i].ActorId, HitboxSet.Humanoid(Vec3.Zero));

            Assert.True(filtered.FramesCaptured * 2 < unfiltered.FramesCaptured,
                $"filter kept {filtered.FramesCaptured} of {unfiltered.FramesCaptured} — "
                + "expected to more than halve the capture cost");
        }

        /// <summary>
        /// The capture loop as the server runs it: interest decides, history stores.
        /// </summary>
        /// <remarks>
        /// Deliberately written here rather than inside <see cref="HitboxHistory"/>. The filter
        /// needs the interest set, and folding it in would make the history class depend on the
        /// interest manager purely to answer a question its caller already knows.
        /// </remarks>
        private static void CaptureRelevant(
            HitboxHistory history, InterestManager interest, WorldSnapshot world, uint tick)
        {
            for (int i = 0; i < world.ActorCount; i++)
            {
                ref ActorSnapshotEntry actor = ref world.Actors[i];
                if (interest.MaxLevelAmongHumanPlayers(actor.ActorId) < InterestLevel.Mid) continue;

                history.Capture(tick, actor.ActorId,
                    HitboxSet.Humanoid(SnapshotBuilder.UnpackPosition(in actor)));
            }
        }
    }
}
