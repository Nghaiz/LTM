using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V4 task 4 — seat arbitration. The seat race is this phase's highest-scored risk
    /// (15 = likelihood 3 x impact 5) and the determinism test below is a <b>precondition</b> of
    /// merging the task, not a follow-up.
    /// </summary>
    public sealed class SeatArbitrationTests
    {
        private const ushort Jeep = 1;
        private const byte DriverSeat = 0;

        /// <summary>
        /// The race. Two clients ask for the same driver seat in one tick; exactly one gets it.
        /// </summary>
        /// <remarks>
        /// This works because the arbiter books occupancy the moment it accepts, before anything
        /// has moved in the scene — so the second decision in the same tick reads a seat that is
        /// already taken. There is no window between the two to lose.
        /// </remarks>
        [Fact]
        public void TwoClientsRacingForTheDriverSeatProduceOneAcceptAndOneRefusal()
        {
            (SeatArbiter arbiter, _) = Fixture();

            SeatDecision first = arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);
            SeatDecision second = arbiter.Decide(Enter(conn: 11, actor: 2), nowTick: 100);

            Assert.Equal(SeatChangeResult.Entered, first.Result);
            Assert.Equal(SeatChangeResult.RejectedOccupied, second.Result);
            Assert.Equal(1, arbiter.RequestsAccepted);
        }

        /// <summary>
        /// The same outcome when only the arrival order changes — determinism, V4-D9.
        /// </summary>
        /// <remarks>
        /// Arrival order is the tie-break, ascending connection id. Asserting that the WINNER is
        /// whoever arrived first (rather than always connection 10) is what makes the rule
        /// testable: a "deterministic" arbiter that always favours the lower id would pass a test
        /// that only checked "exactly one accept".
        /// </remarks>
        [Fact]
        public void TheRaceOutcomeFollowsArrivalOrderAndNotTheConnectionNumber()
        {
            (SeatArbiter arbiter, _) = Fixture();

            SeatDecision first = arbiter.Decide(Enter(conn: 11, actor: 2), nowTick: 100);
            SeatDecision second = arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);

            Assert.Equal(SeatChangeResult.Entered, first.Result);
            Assert.Equal((ushort)2, first.ActorId);
            Assert.Equal(SeatChangeResult.RejectedOccupied, second.Result);
        }

        /// <summary>
        /// V4-D7 — the path that does not exist in the shipped game at all. An accept is
        /// everyone's business; a refusal is one client's.
        /// </summary>
        [Fact]
        public void ARefusalReachesOnlyTheRequesterAndAnAcceptReachesEveryone()
        {
            (SeatArbiter arbiter, _) = Fixture();

            SeatDecision accept = arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);
            SeatDecision refusal = arbiter.Decide(Enter(conn: 11, actor: 2), nowTick: 100);

            Assert.True(accept.Broadcast);
            Assert.False(refusal.Broadcast);
            Assert.Equal((ushort)11, refusal.ConnectionId);
        }

        /// <summary>
        /// The hole V4-D8 closes.
        /// </summary>
        /// <remarks>
        /// <c>Actor.SwitchSeat</c> is a <c>LeaveSeat()</c> + <c>EnterSeat()</c> pair inside one
        /// frame that <b>bypasses <c>CanEnterSeat()</c></b> — so the shipped 1-second re-entry
        /// lockout is enforced on the use-ray path and not on that one. Routing the network path
        /// through two independently arbitrated requests is what buys it back.
        /// </remarks>
        [Fact]
        public void TheReentryLockoutHoldsOnTheNetworkPath()
        {
            (SeatArbiter arbiter, _) = Fixture();

            arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);
            SeatDecision left = arbiter.Decide(Leave(conn: 10, actor: 1), nowTick: 100);
            Assert.Equal(SeatChangeResult.Left, left.Result);

            // Immediately, and one tick before the lockout expires.
            Assert.Equal(
                SeatChangeResult.RejectedLockedOut,
                arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100).Result);

            Assert.Equal(
                SeatChangeResult.RejectedLockedOut,
                arbiter.Decide(
                    Enter(conn: 10, actor: 1),
                    nowTick: 100 + SeatArbiter.ReentryLockoutTicks - 1).Result);

            // And on the tick it expires.
            Assert.Equal(
                SeatChangeResult.Entered,
                arbiter.Decide(
                    Enter(conn: 10, actor: 1),
                    nowTick: 100 + SeatArbiter.ReentryLockoutTicks).Result);
        }

        /// <summary>
        /// The lockout has its own wire code because it is the only refusal whose remedy is "ask
        /// again shortly".
        /// </summary>
        /// <remarks>
        /// <c>Actor.CanEnterSeat()</c> is <c>!IsSeated() &amp;&amp;
        /// cannotEnterVehicleAction.TrueDone()</c> — two conditions behind one predicate — so
        /// <c>RejectedAlreadySeated</c> was the tempting home for it and is a lie whenever the
        /// actor is standing on the ground. This pins that the two are distinguishable, which is
        /// the entire reason protocol v3's enum gained a value.
        /// </remarks>
        [Fact]
        public void LockedOutAndAlreadySeatedAreDistinguishableOnTheWire()
        {
            (SeatArbiter arbiter, _) = Fixture(seatCount: 2);

            arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);

            // Seated, asking for a different seat on the same vehicle.
            Assert.Equal(
                SeatChangeResult.RejectedAlreadySeated,
                arbiter.Decide(Enter(conn: 10, actor: 1, seat: 1), nowTick: 100).Result);

            arbiter.Decide(Leave(conn: 10, actor: 1), nowTick: 100);

            // On foot, asking too soon.
            Assert.Equal(
                SeatChangeResult.RejectedLockedOut,
                arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 101).Result);
        }

        /// <summary>
        /// Network seat switching is leave-then-enter, two requests (V4-D8). There is no atomic
        /// switch on the wire, and asking for one is refused rather than silently honoured.
        /// </summary>
        [Fact]
        public void SwitchingSeatsIsRefusedAndMustBeExpressedAsLeaveThenEnter()
        {
            (SeatArbiter arbiter, _) = Fixture(seatCount: 3);

            arbiter.Decide(Enter(conn: 10, actor: 1, seat: 0), nowTick: 100);

            Assert.Equal(
                SeatChangeResult.RejectedAlreadySeated,
                arbiter.Decide(Enter(conn: 10, actor: 1, seat: 2), nowTick: 100).Result);

            arbiter.Decide(Leave(conn: 10, actor: 1), nowTick: 100);

            Assert.Equal(
                SeatChangeResult.Entered,
                arbiter.Decide(
                    Enter(conn: 10, actor: 1, seat: 2),
                    nowTick: 100 + SeatArbiter.ReentryLockoutTicks).Result);
        }

        /// <summary>A request naming a dead vehicle is refused, and says so specifically.</summary>
        [Fact]
        public void ARequestNamingADeadVehicleIsRefused()
        {
            (SeatArbiter arbiter, VehicleRegistry registry) = Fixture();

            registry.TryGetState(Jeep, out VehicleState state);
            state.Dead = true;
            registry.TrySetState(Jeep, in state);

            Assert.Equal(
                SeatChangeResult.RejectedVehicleDead,
                arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100).Result);
        }

        /// <summary>An unknown vehicle and an out-of-range seat index are the same refusal.</summary>
        [Theory]
        [InlineData(99, 0)]   // no such vehicle
        [InlineData(1, 9)]    // no such seat on a 2-seat jeep
        public void AnUnknownVehicleOrSeatIsRefused(int vehicleId, int seatIndex)
        {
            (SeatArbiter arbiter, _) = Fixture();

            var request = new SeatRequest(
                connectionId: 10, actorId: 1, vehicleId: (ushort)vehicleId,
                seatIndex: (byte)seatIndex, SeatAction.Enter);

            Assert.Equal(
                SeatChangeResult.RejectedNoSuchSeat,
                arbiter.Decide(in request, nowTick: 100).Result);
        }

        /// <summary>
        /// A client asking to board a vehicle across the map is refused. The distance is measured
        /// on the server; the request only carries what the server measured.
        /// </summary>
        [Fact]
        public void AVehicleOutOfReachIsRefused()
        {
            (SeatArbiter arbiter, _) = Fixture();

            float tooFar = (SeatArbiter.MaxSeatReachMetres + 1f)
                         * (SeatArbiter.MaxSeatReachMetres + 1f);

            var request = new SeatRequest(
                connectionId: 10, actorId: 1, vehicleId: Jeep, seatIndex: DriverSeat,
                SeatAction.Enter, distanceSquaredToSeat: tooFar);

            Assert.Equal(
                SeatChangeResult.RejectedTooFar,
                arbiter.Decide(in request, nowTick: 100).Result);
        }

        /// <summary>
        /// A hostile actor id must not reach past the lockout array. It names nothing this server
        /// issued, so it is refused rather than indexed with.
        /// </summary>
        [Fact]
        public void AnOutOfRangeActorIdIsRefusedRatherThanIndexedWith()
        {
            (SeatArbiter arbiter, _) = Fixture();

            var request = new SeatRequest(
                connectionId: 10, actorId: 60000, vehicleId: Jeep, seatIndex: DriverSeat,
                SeatAction.Enter);

            Assert.Equal(
                SeatChangeResult.RejectedNoSuchSeat,
                arbiter.Decide(in request, nowTick: 100).Result);
        }

        /// <summary>
        /// A leave is answered from where the actor actually is, not from what the request named.
        /// </summary>
        /// <remarks>
        /// A client asking to leave a vehicle it is not in is describing a state it has already
        /// diverged from, and honouring its id would empty somebody ELSE's seat.
        /// </remarks>
        [Fact]
        public void ALeaveIsAnsweredFromTheActorsRealSeatAndNotTheRequestedOne()
        {
            (SeatArbiter arbiter, VehicleRegistry registry) = Fixture(seatCount: 2);

            arbiter.Decide(Enter(conn: 10, actor: 1, seat: 1), nowTick: 100);

            // Asks to leave seat 0 of a vehicle it is not in.
            var request = new SeatRequest(
                connectionId: 10, actorId: 1, vehicleId: 99, seatIndex: 0, SeatAction.Leave);

            SeatDecision decision = arbiter.Decide(in request, nowTick: 100);

            Assert.Equal(SeatChangeResult.Left, decision.Result);
            Assert.Equal(Jeep, decision.VehicleId);
            Assert.Equal((byte)1, decision.SeatIndex);
            Assert.Equal(0, registry.OccupantOf(Jeep, 1));
        }

        /// <summary>
        /// A re-request for the seat the actor already occupies succeeds, so a client whose
        /// <c>S_SEAT_CHANGE</c> was lost converges instead of being told it is somewhere it is not.
        /// </summary>
        [Fact]
        public void ReAskingForTheSeatYouAlreadyHoldIsIdempotent()
        {
            (SeatArbiter arbiter, _) = Fixture();

            arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);

            Assert.Equal(
                SeatChangeResult.Entered,
                arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 101).Result);
        }

        /// <summary>
        /// The idempotent accept is ADDRESSED, not broadcast — it changed nothing.
        /// </summary>
        /// <remarks>
        /// <c>C_SEAT_REQUEST</c> has no rate limit anywhere, so treating this as a normal accept
        /// let one seated client repeating "enter the seat I am already in" multiply into
        /// N-players x request-rate reliable broadcasts, each retransmitted until acked. The
        /// first, real accept still reaches everyone — a tank gaining a driver is everyone's
        /// business.
        /// </remarks>
        [Fact]
        public void AnIdempotentAcceptIsAddressedRatherThanBroadcast()
        {
            (SeatArbiter arbiter, _) = Fixture();

            SeatDecision real = arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);
            Assert.True(real.Broadcast);
            Assert.False(real.ChangedNothing);

            SeatDecision repeat = arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 101);

            Assert.Equal(SeatChangeResult.Entered, repeat.Result);
            Assert.True(repeat.Accepted);
            Assert.True(repeat.ChangedNothing);
            Assert.False(repeat.Broadcast);
            Assert.Equal((ushort)10, repeat.ConnectionId);
        }

        /// <summary>
        /// V4-D7's rollback. <c>Actor.EnterSeat</c> re-reads the live scene, so a <c>false</c> is
        /// a condition the arbiter could not see — and the booking must not survive it.
        /// </summary>
        [Fact]
        public void RollingBackARefusedAcceptFreesTheSeatForTheNextRequest()
        {
            (SeatArbiter arbiter, VehicleRegistry registry) = Fixture();

            SeatDecision accepted = arbiter.Decide(Enter(conn: 10, actor: 1), nowTick: 100);
            Assert.Equal((ushort)1, registry.OccupantOf(Jeep, DriverSeat));

            arbiter.Rollback(in accepted);
            Assert.Equal(0, registry.OccupantOf(Jeep, DriverSeat));

            Assert.Equal(
                SeatChangeResult.Entered,
                arbiter.Decide(Enter(conn: 11, actor: 2), nowTick: 100).Result);
        }

        /// <summary>
        /// V4-D6 — <c>seats[0]</c> is the driver by array-index convention, and
        /// <c>Seat.Type.Driver</c> stays unconsulted exactly as it is today. A prefab reorder
        /// should fail a test rather than fail silently in a match.
        /// </summary>
        /// <remarks>
        /// What is pinnable in CI is the protocol half: index 0 is what the wire and the arbiter
        /// mean by "driver". The scene half — that <c>Vehicle.seats[0]</c> is the seat the prefab
        /// author intended as the driver's — is a prefab fact and is in the Editor-only list the
        /// client track owns.
        /// </remarks>
        [Fact]
        public void SeatZeroIsTheDriver()
        {
            (SeatArbiter arbiter, VehicleRegistry registry) = Fixture(seatCount: 4);

            SeatDecision decision = arbiter.Decide(
                Enter(conn: 10, actor: 1, seat: 0), nowTick: 100);

            Assert.Equal(SeatChangeResult.Entered, decision.Result);
            Assert.Equal((byte)0, decision.SeatIndex);
            Assert.Equal((ushort)1, registry.OccupantOf(Jeep, 0));

            // And the passenger seats are independently occupiable, so index 0 is not merely
            // "the only seat".
            Assert.Equal(
                SeatChangeResult.Entered,
                arbiter.Decide(Enter(conn: 11, actor: 2, seat: 3), nowTick: 100).Result);
        }

        // ------------------------------------------------------------------- helpers

        private static (SeatArbiter, VehicleRegistry) Fixture(byte seatCount = 2)
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(
                    Jeep, 0, VehicleKind.Car, seatCount, maxHealth: 100f, ownerTeam: 0),
                new VehicleCaptureTests.FakePose());

            return (new SeatArbiter(registry), registry);
        }

        private static SeatRequest Enter(ushort conn, ushort actor, byte seat = DriverSeat)
            => new SeatRequest(conn, actor, Jeep, seat, SeatAction.Enter);

        private static SeatRequest Leave(ushort conn, ushort actor, byte seat = DriverSeat)
            => new SeatRequest(conn, actor, Jeep, seat, SeatAction.Leave);
    }
}
