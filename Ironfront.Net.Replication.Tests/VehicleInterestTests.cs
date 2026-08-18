using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Interest;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V4 task 3 — vehicle interest management, and the refactor gate on
    /// <see cref="InterestManager"/>.
    /// </summary>
    /// <remarks>
    /// <b>The other half of this task's grading is a suite that is NOT in this file.</b>
    /// <see cref="InterestManagementTests"/> and <see cref="SnapshotSheddingTests"/> come from
    /// phases 02, 03 and 05 and must pass <b>unedited</b> — that is acceptance criterion 2, and
    /// it is the whole reason the actor <c>Evaluate</c> overload became a two-line forwarder with
    /// its signature untouched rather than being rewritten.
    /// </remarks>
    public sealed class VehicleInterestTests
    {
        // ------------------------------------------------------------ the V4-D3 collision

        /// <summary>
        /// The failure nothing else in the codebase would catch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="InterestManager"/> keys its rate table on <c>(viewer &lt;&lt; 16) | target</c>.
        /// Actor ids and vehicle ids are separate <c>u16</c> spaces, so actor 7 and vehicle 7
        /// produce the SAME key — one shared dictionary and each would consume the other's rate
        /// slot, starving both to half their band rate with no error anywhere.
        /// </para>
        /// <para>
        /// This is written as an assertion rather than left to the decision record because the
        /// tempting future change is exactly the one that breaks it: unifying the two trackers
        /// "to remove duplication". This test is what makes that go red.
        /// </para>
        /// </remarks>
        [Fact]
        public void AVehicleAndAnActorWithTheSameIdDoNotShareARateSlot()
        {
            var actors = new InterestManager();
            var vehicles = new VehicleInterestTracker();

            const ushort Viewer = 1;
            const ushort SharedId = 7;

            // Both are Far, so both are due only every 5th snapshot. If they shared a slot, the
            // vehicle's send below would consume the actor's and the actor would be held.
            Assert.True(vehicles.ShouldSend(Viewer, SharedId, InterestLevel.Far, snapshotIndex: 10));
            Assert.True(actors.ShouldSend(Viewer, SharedId, InterestLevel.Far, snapshotIndex: 10));

            // And the reverse direction: neither is due again on 11.
            Assert.False(vehicles.IsDue(Viewer, SharedId, InterestLevel.Far, snapshotIndex: 11));
            Assert.False(actors.IsDue(Viewer, SharedId, InterestLevel.Far, snapshotIndex: 11));
        }

        /// <summary>
        /// V4-D5's other half: the classifier's self-comparison must not fire across id spaces.
        /// </summary>
        /// <remarks>
        /// <c>Evaluate</c> short-circuits "you always see yourself" to Near. Once one method sees
        /// both kinds, actor 7 looking at vehicle 7 matches an id-only test and the vehicle is
        /// pinned to 20 Hz from anywhere on the map, at any distance. That is why
        /// <see cref="InterestSubject.IsSameEntityAs"/> compares the space as well.
        /// </remarks>
        [Fact]
        public void AnActorDoesNotSeeAVehicleWithItsOwnNumberAsItself()
        {
            var interest = new InterestManager();

            InterestSubject viewer = Actor(id: 7, x: 0f);
            InterestSubject vehicle = Vehicle(id: 7, x: 1000f);

            Assert.Equal(InterestLevel.Culled, interest.Evaluate(in viewer, in vehicle));
        }

        // ---------------------------------------------------------------- band edges

        /// <summary>
        /// The bands are read from <see cref="InterestManager"/>, not redeclared — so this asserts
        /// the vehicle path lands on the same radii, expressed against the same constants.
        /// </summary>
        [Theory]
        [InlineData(59.9f, InterestLevel.Near)]
        [InlineData(60.1f, InterestLevel.Mid)]
        [InlineData(149.9f, InterestLevel.Mid)]
        [InlineData(150.1f, InterestLevel.Far)]
        [InlineData(499.9f, InterestLevel.Far)]
        public void VehicleBandEdgesMatchTheActorBands(float distance, InterestLevel expected)
        {
            var tracker = new VehicleInterestTracker();

            InterestSubject viewer = Actor(id: 1, x: 0f);
            VehicleSnapshotEntry vehicle = VehicleEntry(id: 1, x: distance);

            Assert.Equal(expected, tracker.Classify(in viewer, in vehicle));
        }

        /// <summary>
        /// The design's stated invariant, and the reason <see cref="InterestManager.CullRadius"/>
        /// is 500 rather than <see cref="InterestManager.FarRadius"/>'s 300.
        /// </summary>
        [Fact]
        public void NothingInsideFiveHundredMetresIsCulled()
        {
            var tracker = new VehicleInterestTracker();
            InterestSubject viewer = Actor(id: 1, x: 0f);

            for (float d = 0f; d < InterestManager.CullRadius; d += 25f)
            {
                VehicleSnapshotEntry vehicle = VehicleEntry(id: 1, x: d);
                Assert.NotEqual(InterestLevel.Culled, tracker.Classify(in viewer, in vehicle));
            }
        }

        /// <summary>
        /// Past the cull radius the view cone is the only thing that rescues a target — a tank at
        /// 600 m down a scope is exactly what it was sized for.
        /// </summary>
        [Fact]
        public void PastTheCullRadiusOnlyTheViewConeRescuesAVehicle()
        {
            var tracker = new VehicleInterestTracker();

            // Yaw 0 faces +Z in this codebase's convention, so a vehicle on +X at 600 m is
            // outside the cone and one on +Z at 600 m is inside it.
            InterestSubject viewer = Actor(id: 1, x: 0f, yawDegrees: 0f);

            Assert.Equal(
                InterestLevel.Culled,
                tracker.Classify(in viewer, VehicleEntry(id: 1, x: 600f)));

            Assert.Equal(
                InterestLevel.Far,
                tracker.Classify(in viewer, VehicleEntry(id: 2, x: 0f, z: 600f)));
        }

        /// <summary>
        /// A vehicle has no team as far as interest is concerned (V4-D5), so the teammate floor
        /// cannot promote one.
        /// </summary>
        /// <remarks>
        /// The floor exists because a player is expected to care where their own side is. A jeep
        /// somebody's teammate drove ten minutes ago is not that, and promoting every vehicle
        /// within 300 m to 10 Hz on team grounds would be bandwidth spent on nothing.
        /// </remarks>
        [Fact]
        public void TheTeammateFloorNeverFiresForAVehicle()
        {
            var interest = new InterestManager();

            InterestSubject viewer = Actor(id: 1, x: 0f, team: TeamId.None);
            InterestSubject vehicle = Vehicle(id: 2, x: 200f);   // also TeamId.None

            // 200 m is past MidRadius, so only the floor could lift it above Far.
            Assert.Equal(InterestLevel.Far, interest.Evaluate(in viewer, in vehicle));
        }

        // ------------------------------------------------------------------- shedding

        /// <summary>
        /// design section 8 criterion 9 — at the shipped load nothing sheds. <b>Non-zero here is
        /// a failure, not a statistic.</b>
        /// </summary>
        [Fact]
        public void TwelveVehiclesInViewShedNothing()
        {
            var tracker = new VehicleInterestTracker();
            var world = new VehicleWorldSnapshot();

            for (ushort id = 1; id <= 12; id++)
                world.Add(VehicleEntry(id, x: id * 3f));

            InterestSubject viewer = Actor(id: 1, x: 0f);
            var view = new VehicleWorldSnapshot();

            tracker.BuildView(
                in viewer, world, snapshotIndex: 1, view,
                VehicleSnapshotMessage.MaxBodySize);

            Assert.Equal(12, view.VehicleCount);
            Assert.Equal(0, tracker.EntriesShed);
        }

        /// <summary>
        /// The <c>IsDue</c> / <c>RecordSend</c> split. A vehicle that loses the byte race must not
        /// also lose its rate slot.
        /// </summary>
        /// <remarks>
        /// Recording a send that never happened makes a Far vehicle wait a further five snapshots
        /// — a quarter of a second — every time it loses, and the vehicles most likely to lose are
        /// the same ones every snapshot. That is starvation, and it is why the budget is checked
        /// BEFORE due-ness rather than after.
        /// </remarks>
        [Fact]
        public void AShedVehicleKeepsItsRateSlot()
        {
            var tracker = new VehicleInterestTracker();
            var world = new VehicleWorldSnapshot();

            for (ushort id = 1; id <= 8; id++)
                world.Add(VehicleEntry(id, x: 20f + id));

            InterestSubject viewer = Actor(id: 1, x: 0f);
            var view = new VehicleWorldSnapshot();

            // Room for exactly two entries plus the header, so six are shed.
            int budget = VehicleSnapshotHeader.Size + 2 * VehicleInterestTracker.MaxEntrySize;

            tracker.BuildView(in viewer, world, snapshotIndex: 1, view, budget);

            Assert.Equal(2, view.VehicleCount);
            Assert.Equal(6, tracker.EntriesShed);

            // Every vehicle that was shed is still due on the very next snapshot — it never
            // consumed a slot. They are all Near here, so their period is 1.
            for (ushort id = 1; id <= 8; id++)
            {
                bool wasSent = view.IndexOf(id) >= 0;
                if (wasSent) continue;

                Assert.True(
                    tracker.IsDue(viewer.Id, id, InterestLevel.Near, snapshotIndex: 2),
                    $"vehicle {id} was shed and lost its rate slot");
            }
        }

        /// <summary>
        /// The cursor rotates admission, so a vehicle that lost one round leads the next. Without
        /// it the same two vehicles win every snapshot and the other six are never sent at all.
        /// </summary>
        [Fact]
        public void TheShedCursorRotatesWhoIsAdmitted()
        {
            var tracker = new VehicleInterestTracker();
            var world = new VehicleWorldSnapshot();

            for (ushort id = 1; id <= 6; id++)
                world.Add(VehicleEntry(id, x: 20f + id));

            InterestSubject viewer = Actor(id: 1, x: 0f);
            var first = new VehicleWorldSnapshot();
            var second = new VehicleWorldSnapshot();

            int budget = VehicleSnapshotHeader.Size + 2 * VehicleInterestTracker.MaxEntrySize;

            int cursor = tracker.BuildView(in viewer, world, 1, first, budget);
            Assert.True(cursor > 0, "the cursor must advance when something was shed");

            tracker.BuildView(in viewer, world, 2, second, budget, cursor);

            Assert.Equal(2, second.VehicleCount);
            Assert.NotEqual(first.Vehicles[0].VehicleId, second.Vehicles[0].VehicleId);
        }

        // ----------------------------------------------------------------- trap 2

        /// <summary>
        /// The trap-2 leak, one dictionary over: 16 viewers x every vehicle id ever issued,
        /// growing for the life of the process.
        /// </summary>
        [Fact]
        public void TheVehiclePairTableEmptiesOnDespawn()
        {
            var tracker = new VehicleInterestTracker();

            for (ushort viewer = 1; viewer <= 4; viewer++)
            for (ushort vehicle = 1; vehicle <= 5; vehicle++)
                tracker.RecordSend(viewer, vehicle, snapshotIndex: 1);

            Assert.Equal(20, tracker.TrackedPairCount);

            for (ushort vehicle = 1; vehicle <= 5; vehicle++) tracker.Forget(vehicle);

            Assert.Equal(0, tracker.TrackedPairCount);
        }

        /// <summary>A departing viewer leaks one row per vehicle it ever saw, if nothing forgets it.</summary>
        [Fact]
        public void ForgettingAViewerDropsOnlyThatViewersRows()
        {
            var tracker = new VehicleInterestTracker();

            tracker.RecordSend(viewerActorId: 1, vehicleId: 9, snapshotIndex: 1);
            tracker.RecordSend(viewerActorId: 2, vehicleId: 9, snapshotIndex: 1);

            tracker.ForgetViewer(1);

            Assert.Equal(1, tracker.TrackedPairCount);
            Assert.False(tracker.IsDue(2, 9, InterestLevel.Near, snapshotIndex: 1));
        }

        /// <summary>
        /// A vehicle is never a viewer (V4-D5), and the vehicle path asserts it rather than
        /// trusting the caller.
        /// </summary>
        [Fact]
        public void AVehicleCannotBeAViewer()
        {
            var tracker = new VehicleInterestTracker();
            var world = new VehicleWorldSnapshot();
            world.Add(VehicleEntry(1, x: 5f));

            InterestSubject bad = Vehicle(id: 1, x: 0f);

            Assert.Throws<System.ArgumentException>(
                () => tracker.BuildView(
                    in bad, world, 1, new VehicleWorldSnapshot(),
                    VehicleSnapshotMessage.MaxBodySize));
        }

        // ------------------------------------------------------------------- helpers

        private static InterestSubject Actor(
            ushort id, float x, float z = 0f, byte team = TeamId.Team0, float yawDegrees = 0f)
            => new InterestSubject(
                id, InterestSpace.Actor,
                Quantize.PackPos(x), Quantize.PackPos(0f), Quantize.PackPos(z),
                team, Quantize.PackYaw(yawDegrees));

        private static InterestSubject Vehicle(ushort id, float x, float z = 0f)
            => InterestSubject.From(VehicleEntry(id, x, z));

        private static VehicleSnapshotEntry VehicleEntry(ushort id, float x, float z = 0f)
            => new VehicleSnapshotEntry
            {
                VehicleId  = id,
                ChangeMask = VehicleField.Full,
                PosX       = Quantize.PackPos(x),
                PosY       = Quantize.PackPos(0f),
                PosZ       = Quantize.PackPos(z),
            };
    }
}
