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
        /// Acceptance criterion 5 — at the shipped load the VEHICLE tracker sheds nothing.
        /// <b>Non-zero here is a failure, not a statistic.</b>
        /// </summary>
        /// <remarks>
        /// <b>Read the sibling below before trusting this one.</b> On its own this assertion is
        /// close to vacuous: departure 3 gives the vehicle body its own bound
        /// (<see cref="VehicleSnapshotMessage.MaxBodySize"/>), so 12 vehicles cannot be shed by
        /// actor pressure no matter how many actors there are — criterion 5 is structurally true
        /// rather than earned. What the criterion was reaching for is that the shipped load fits
        /// in one datagram, and that question moved to the ACTOR stream when the budget order was
        /// reversed. The sibling measures it.
        /// </remarks>
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
        /// What criterion 5 costs the ACTOR stream, measured rather than assumed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Departure 3 writes the bounded vehicle body first and gives actors the remainder, so
        /// the co-residency pressure lands entirely on the elastic stream. At the shipped load —
        /// 12 vehicles beside 16 players and 32 bots — the actor budget admits fewer than the 48
        /// actors the criterion names, so some actor entries ARE shed.
        /// </para>
        /// <para>
        /// <b>That is degradation working, not breaking.</b> The actor tracker rotates its
        /// admission window on every shed (phase-05 D6), so a shed actor leads the next snapshot
        /// and every actor still arrives within a bounded number of them. protocol-spec.md § 4.10
        /// already states the worst case as 29 actors. This test exists so the number is a fact
        /// somebody chose rather than a surprise found in a playtest, and so a future change that
        /// makes it materially worse fails here.
        /// </para>
        /// </remarks>
        [Fact]
        public void TwelveVehiclesLeaveTheActorStreamAMeasuredBudget()
        {
            const int Vehicles = 12;

            int vehicleBody = Vehicles * VehicleSnapshotMessage.FullEntrySize
                            + VehicleSnapshotHeader.Size;

            int actorBudget = Server.ServerPayloadWriter.ActorBodyBudget(vehicleBody);
            int actorsAdmitted = (actorBudget - SnapshotHeader.Size) / InterestManager.MaxEntrySize;

            // Pinned as a RANGE, not a single number: the point is the order of magnitude and the
            // direction, and an exact figure would go red on any harmless entry-width change
            // without telling anybody anything.
            Assert.InRange(actorsAdmitted, 30, 40);

            // The honest half — this is BELOW the 48 the criterion names, and the shortfall is
            // real. Asserting it rather than hiding it is the difference between a known cost and
            // a bandwidth regression nobody attributed.
            Assert.True(
                actorsAdmitted < 48,
                $"actor budget admits {actorsAdmitted}; if this now covers 48 the co-residency "
                + "note in protocol-spec.md section 4.10 is stale and should be corrected.");

            // And vehicles never pay: their body is bounded, so it always fits whole.
            Assert.True(vehicleBody <= VehicleSnapshotMessage.MaxBodySize);
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
        /// A not-due vehicle is SKIPPED, not stopped at, so the scan reaches the entries behind
        /// it — and that is what makes the shared shed cursor safe.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This pins the reasoning, because the arithmetic alone points the other way.</b> The
        /// cursor advances by the TOTAL admitted across all three buckets and is then applied
        /// modulo each bucket's own length, so with 6 Near and 4 Mid it returns 8 every round,
        /// <c>8 % 4 == 0</c>, and Mid appears to restart at the same two entries forever. A review
        /// concluded exactly that, and so did a first attempt at fixing it.
        /// </para>
        /// <para>
        /// It does not happen, because Mid sends every 2nd snapshot: the two entries admitted last
        /// round are not due this round, the scan walks past them <b>without spending budget</b>,
        /// and the ones behind them go out. Change that <c>continue</c> to a <c>break</c> — the
        /// obvious "stop when we hit a not-due entry" optimisation — and the starvation becomes
        /// real, silently, with every counter still adding up.
        /// </para>
        /// <para>
        /// Near is the one band where everything is always due, and it cannot starve either: it is
        /// admitted first, so if Near sheds then no lower bucket gets any budget and the total
        /// advance IS Near's own admitted count.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANotDueVehicleIsSkippedSoTheOnesBehindItStillGetThrough()
        {
            var tracker = new VehicleInterestTracker();
            var world = new VehicleWorldSnapshot();

            // 6 Near (inside 60 m) and 4 Mid (60..150 m) — the exact shape that made the shared
            // cursor's advance a multiple of the Mid bucket's length.
            for (ushort id = 1; id <= 6; id++) world.Add(VehicleEntry(id, x: 10f + id));
            for (ushort id = 7; id <= 10; id++) world.Add(VehicleEntry(id, x: 100f + id));

            InterestSubject viewer = Actor(id: 1, x: 0f);
            var view = new VehicleWorldSnapshot();

            int budget = VehicleSnapshotHeader.Size + 8 * VehicleInterestTracker.MaxEntrySize;

            var seen = new System.Collections.Generic.HashSet<ushort>();
            int cursor = 0;

            for (uint snapshot = 1; snapshot <= 12; snapshot++)
            {
                cursor = tracker.BuildView(in viewer, world, snapshot, view, budget, cursor);
                for (int i = 0; i < view.VehicleCount; i++) seen.Add(view.Vehicles[i].VehicleId);
            }

            for (ushort id = 1; id <= 10; id++)
                Assert.True(seen.Contains(id), $"vehicle {id} was never delivered in 12 snapshots");
        }

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
