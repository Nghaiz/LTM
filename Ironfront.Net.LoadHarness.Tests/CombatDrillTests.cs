using Ironfront.Net.LoadHarness;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Unity.Diagnostics;
using Xunit;

namespace Ironfront.Net.LoadHarness.Tests
{
    /// <summary>
    /// The Combat behaviour's rules, exercised without a socket. Ledger <b>X-34</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole reason the drill takes a <see cref="DrillWorld"/> instead of a
    /// router.</b> Every rule below decides whether a lane-A run provokes one of check 11's four
    /// verbs, and if the only way to exercise them were a 120 s run against a Unity server then
    /// nobody would exercise them — which is exactly how <c>SyntheticClient.PushInput</c> sent
    /// <c>InputButtons.None</c> for two phases without anyone noticing.
    /// </para>
    /// <para>
    /// The tests assert the ARITHMETIC against <see cref="ScriptedAim"/> rather than against
    /// literal degrees. A literal would pin today's answer, and the point of linking the shipped
    /// aim is that when it moves the drill moves with it.
    /// </para>
    /// </remarks>
    public class CombatDrillTests
    {
        private static DrillBody Me(float x = 0f, float y = 0f, float z = 0f)
            => new DrillBody(id: 10, x, y, z);

        private static DrillBody Vehicle(float x, float z, byte seats = 2, ushort id = 77)
            => new DrillBody(id, x, 0f, z, seats);

        private static DrillBody Actor(float x, float z, ushort id = 43)
            => new DrillBody(id, x, 0f, z);

        private static DrillWorld World(
            DrillBody? me = null, DrillBody actor = default, DrillBody vehicle = default,
            bool alive = true)
            => new DrillWorld(me ?? Me(), actor, vehicle, alive);

        [Fact]
        public void SendsNothingBeforeTheServerNamesItsBody()
        {
            var drill = new CombatDrill(0);

            DrillCommand command = drill.Decide(
                new DrillWorld(default, Actor(1f, 1f), Vehicle(1f, 1f), alive: true), 0.0);

            // A frame sent before S_SPAWN_ACTOR is movement input for a body the drill cannot
            // see. The server applies it to whatever actor the connection owns, so it is a real
            // send with no observable intent behind it -- and it would put a yaw of 0 on a
            // client whose body may be facing anywhere.
            Assert.False(command.SendActorInput);
            Assert.False(command.SendVehicleInput);
            Assert.Equal(SeatIntent.None, command.Seat);
        }

        [Fact]
        public void WalksTowardAVehicleThatIsOutOfReach()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(x: 0f, z: 40f);

            DrillCommand command = drill.Decide(World(vehicle: vehicle), 0.0);

            Assert.Equal(DrillPhase.Approach, command.Phase);
            Assert.True(command.SendActorInput);
            Assert.Equal(1f, command.MoveZ);
            Assert.Equal(SeatIntent.None, command.Seat);
            Assert.Equal(ScriptedAim.YawDegrees(0f, 0f, 0f, 40f), command.YawDegrees);
        }

        [Fact]
        public void AsksForSeatZeroOnceItIsInsideTheArbitersReach()
        {
            var drill = new CombatDrill(0);

            // Inside the drill's own margin, which sits a metre inside the server's limit.
            float distance = CombatDrill.SeatRequestDistanceMetres - 0.5f;
            DrillCommand command = drill.Decide(World(vehicle: Vehicle(0f, distance)), 0.0);

            Assert.Equal(SeatIntent.Enter, command.Seat);
            Assert.Equal((ushort)77, command.SeatVehicleId);
            Assert.Equal((byte)0, command.SeatIndex);
            Assert.Equal(1, drill.SeatRequestsSent);
            Assert.Equal(DrillPhase.AwaitSeat, command.Phase);
        }

        [Fact]
        public void ItsMarginSitsInsideTheServersOwnLimit()
        {
            // Asked against SeatArbiter's constant rather than against a literal 5: the two have
            // to move together or the drill spends a round trip per seat being told
            // RejectedTooFar, and the failure looks like a server fault.
            Assert.True(CombatDrill.SeatRequestDistanceMetres < SeatArbiter.MaxSeatReachMetres);
            Assert.True(CombatDrill.SeatRequestDistanceMetres > 0f);
        }

        [Fact]
        public void DoesNotAskTwiceWhileAnAnswerIsInFlight()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, CombatDrill.SeatRequestDistanceMetres - 0.5f);

            drill.Decide(World(vehicle: vehicle), 0.0);
            DrillCommand second = drill.Decide(World(vehicle: vehicle), 100.0);

            Assert.Equal(SeatIntent.None, second.Seat);
            Assert.Equal(1, drill.SeatRequestsSent);
            Assert.Equal(DrillPhase.AwaitSeat, second.Phase);
        }

        [Fact]
        public void DrivesOnlyAfterTheServerGrantsTheSeat()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, CombatDrill.SeatRequestDistanceMetres - 0.5f);

            drill.Decide(World(vehicle: vehicle), 0.0);

            // Still waiting: nothing here predicts a seat, so no vehicle input yet.
            Assert.False(drill.Decide(World(vehicle: vehicle), 50.0).SendVehicleInput);

            drill.OnSeatChange(
                actorId: 10, vehicleId: 77, seatIndex: 0,
                SeatChangeResult.Entered, myActorId: 10);

            DrillCommand driving = drill.Decide(World(vehicle: vehicle), 100.0);

            Assert.Equal(DrillPhase.Drive, driving.Phase);
            Assert.True(driving.SendVehicleInput);
            Assert.Equal((ushort)77, driving.VehicleId);
            Assert.Equal((sbyte)127, driving.Throttle);
            Assert.Equal((ushort)77, drill.SeatedVehicleId);
        }

        [Fact]
        public void AGrantForSomebodyElseIsNotThisClientsSeat()
        {
            var drill = new CombatDrill(0);

            drill.OnSeatChange(
                actorId: 99, vehicleId: 77, seatIndex: 0,
                SeatChangeResult.Entered, myActorId: 10);

            Assert.Equal((ushort)0, drill.SeatedVehicleId);
        }

        [Fact]
        public void LeavesTheSeatOnceItHasHeldItLongEnough()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, 1f);

            drill.OnSeatChange(10, 77, 0, SeatChangeResult.Entered, 10);
            drill.Decide(World(vehicle: vehicle), 0.0);

            DrillCommand leaving = drill.Decide(
                World(vehicle: vehicle), CombatDrill.SeatedMs + 1.0);

            // Getting out is what arms Vehicle.AutoDamage, which is the one route to a burn a
            // client with no explosive has. A drill that never left would deny itself the verb.
            Assert.Equal(SeatIntent.Leave, leaving.Seat);
            Assert.Equal((ushort)77, leaving.SeatVehicleId);

            // And the seat is NOT cleared by having asked -- a refused leave must leave this
            // client driving, not walking on foot inside a vehicle it still occupies.
            Assert.Equal((ushort)77, drill.SeatedVehicleId);
        }

        [Fact]
        public void WalksToTheNextSeatWhenTheFirstIsOccupied()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, CombatDrill.SeatRequestDistanceMetres - 0.5f);

            drill.Decide(World(vehicle: vehicle), 0.0);
            drill.OnSeatChange(10, 77, 0, SeatChangeResult.RejectedOccupied, 10);

            DrillCommand retry = drill.Decide(World(vehicle: vehicle), 100.0);

            Assert.Equal(SeatIntent.Enter, retry.Seat);
            Assert.Equal((byte)1, retry.SeatIndex);
            Assert.Equal(1, drill.SeatRequestsRefused);
        }

        [Fact]
        public void AbandonsAVehicleOnAnyOtherRefusalAndGoesToFight()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, CombatDrill.SeatRequestDistanceMetres - 0.5f);

            drill.Decide(World(vehicle: vehicle), 0.0);
            drill.OnSeatChange(10, 77, 0, SeatChangeResult.RejectedVehicleDead, 10);

            DrillCommand next = drill.Decide(
                World(actor: Actor(0f, 20f), vehicle: vehicle), 100.0);

            Assert.Equal(DrillPhase.Fight, next.Phase);
            Assert.Equal(SeatIntent.None, next.Seat);
        }

        [Fact]
        public void ARefusalIsNotPermanent()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, CombatDrill.SeatRequestDistanceMetres - 0.5f);

            drill.Decide(World(vehicle: vehicle), 0.0);
            drill.OnSeatChange(10, 77, 0, SeatChangeResult.RejectedVehicleDead, 10);
            drill.Decide(World(actor: Actor(0f, 20f), vehicle: vehicle), 100.0);

            // The refusal was a fact about one moment. Without this the drill has a one-way
            // door: a client refused once would never ask again, and a run in which every
            // client lost its first race for the same hull would report no Drive at all.
            DrillCommand later = drill.Decide(
                World(actor: Actor(0f, 20f), vehicle: vehicle),
                100.0 + CombatDrill.FightBeforeReapproachMs + 1.0);

            Assert.Equal(SeatIntent.Enter, later.Seat);
            Assert.Equal((ushort)77, later.SeatVehicleId);
        }

        [Fact]
        public void GivesUpOnASeatRequestNobodyAnswers()
        {
            var drill = new CombatDrill(0);
            DrillBody vehicle = Vehicle(0f, CombatDrill.SeatRequestDistanceMetres - 0.5f);

            drill.Decide(World(vehicle: vehicle), 0.0);

            DrillCommand after = drill.Decide(
                World(actor: Actor(0f, 20f), vehicle: vehicle),
                CombatDrill.SeatAnswerTimeoutMs + 1.0);

            // A server that routes the request and never answers must not leave a synthetic
            // client standing still for the rest of the run.
            Assert.NotEqual(DrillPhase.AwaitSeat, after.Phase);
        }

        [Fact]
        public void AimsAtTheTorsoAndHoldsTheTrigger()
        {
            var drill = new CombatDrill(0);
            DrillBody target = Actor(x: 0f, z: 20f);

            DrillCommand command = drill.Decide(
                new DrillWorld(Me(), target, default, alive: true), 0.0);

            Assert.Equal(DrillPhase.Fight, command.Phase);
            Assert.True(command.SendActorInput);
            Assert.True(command.Buttons.HasFlag(InputButtons.Fire));

            // The two ends are raised by DIFFERENT heights, and that asymmetry IS the fix for
            // X-25: raising both by the eye height reads as "aim level", puts every shot through
            // the 3 cm seam X-24 names, and is why no lane-B combat run scored a hit for a
            // month. Asserted against the shipped helper, so a change there moves this with it.
            Assert.Equal(
                ScriptedAim.PitchAtBody(0f, 0f, 0f, 0f, 0f, 20f), command.PitchDegrees);
            Assert.Equal(ScriptedAim.YawDegrees(0f, 0f, 0f, 20f), command.YawDegrees);
            Assert.NotEqual(0f, command.PitchDegrees);
        }

        [Fact]
        public void StopsClosingOnceItIsInsideTheHoldDistance()
        {
            var drill = new CombatDrill(0);

            DrillCommand far = drill.Decide(
                new DrillWorld(Me(), Actor(0f, 50f), default, alive: true), 0.0);
            DrillCommand near = drill.Decide(
                new DrillWorld(
                    Me(), Actor(0f, CombatDrill.FightHoldDistanceMetres - 1f), default,
                    alive: true),
                100.0);

            Assert.Equal(1f, far.MoveZ);
            Assert.Equal(0f, near.MoveZ);
            Assert.True(near.Buttons.HasFlag(InputButtons.Fire));
        }

        [Fact]
        public void KeepsMovingWhenThereIsNobodyToShoot()
        {
            var drill = new CombatDrill(0);

            DrillCommand command = drill.Decide(
                new DrillWorld(Me(), default, default, alive: true), 0.0);

            // A Combat run that degenerated into eight statues would report a bandwidth figure
            // describing the one case delta encoding is best at -- which is Move's whole
            // argument, one behaviour over.
            Assert.True(command.SendActorInput);
            Assert.Equal(1f, command.MoveZ);
            Assert.False(command.Buttons.HasFlag(InputButtons.Fire));
        }

        [Fact]
        public void WaitsOutTheRespawnGateAndThenAsksOnce()
        {
            var drill = new CombatDrill(0);
            drill.OnLocalDeath(1000.0);

            double dead = 1000.0;
            double due = dead + ProtocolConstants.RESPAWN_SECONDS * 1000.0
                         + CombatDrill.RespawnGraceMs;

            Assert.False(drill.Decide(World(), dead + 100.0).SendRespawn);
            Assert.True(drill.Decide(World(), due + 1.0).SendRespawn);

            // Once, not once per tick. ServerRespawnGate refuses an early request as a normal
            // outcome, so racing it is free -- but 30 reliable messages a second per death per
            // client would put the harness's own impatience into phase 4's bandwidth
            // decomposition.
            Assert.False(drill.Decide(World(), due + 2.0).SendRespawn);
            Assert.Equal(1, drill.RespawnRequestsSent);
        }

        [Fact]
        public void ASnapshotSayingItIsDeadStartsTheClockToo()
        {
            var drill = new CombatDrill(0);

            // S_DEATH is reliable-ordered on channel 2 and can arrive AFTER the snapshot whose
            // flags already say this body is down. Whichever gets here first has to start the
            // clock, or a client that never received the message stands still forever.
            DrillCommand command = drill.Decide(World(alive: false), 5000.0);

            Assert.Equal(DrillPhase.Dead, command.Phase);
            Assert.False(command.SendRespawn);
            Assert.True(
                drill.Decide(
                    World(alive: false),
                    5000.0 + ProtocolConstants.RESPAWN_SECONDS * 1000.0
                    + CombatDrill.RespawnGraceMs + 1.0).SendRespawn);
        }

        [Fact]
        public void ADeathReleasesTheSeatItWasHolding()
        {
            var drill = new CombatDrill(0);
            drill.OnSeatChange(10, 77, 0, SeatChangeResult.Entered, 10);

            drill.OnLocalDeath(1000.0);

            Assert.Equal((ushort)0, drill.SeatedVehicleId);
            Assert.Equal(DrillPhase.Dead, drill.Phase);
        }

        [Fact]
        public void ResumesTheDrillWhenTheNewBodyArrives()
        {
            var drill = new CombatDrill(0);
            drill.OnLocalDeath(1000.0);
            drill.OnLocalSpawn();

            DrillCommand command = drill.Decide(World(vehicle: Vehicle(0f, 40f)), 2000.0);

            Assert.Equal(DrillPhase.Approach, command.Phase);
            Assert.True(command.SendActorInput);
        }
    }
}
