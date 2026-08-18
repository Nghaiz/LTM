using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V4 tasks 5 and 6 — the burn clock, and bot seat claims that name somebody.
    /// </summary>
    public sealed class VehicleDamageAndClaimTests
    {
        private const ushort Tank = 1;

        // ------------------------------------------------------------------ burn clock

        /// <summary>
        /// V4-D11, against <c>Vehicle.ApplyHealth</c>. Zero health starts a burn; it does not kill.
        /// </summary>
        /// <remarks>
        /// A server that killed at zero health would ship a game in which no vehicle ever burns —
        /// a visible, dramatic difference from single-player that a test asserting "damage kills"
        /// would pass.
        /// </remarks>
        [Fact]
        public void ZeroHealthStartsBurningAndDoesNotKill()
        {
            (VehicleRegistry registry, VehicleBurnClock clock) = Fixture();

            Assert.True(clock.StartBurning(Tank, burnTicks: 60, nowTick: 10));

            registry.TryGetState(Tank, out VehicleState state);
            Assert.True(state.Burning);
            Assert.False(state.Dead);
            Assert.Equal(0, clock.DiedThisTickCount);
        }

        /// <summary>
        /// Death arrives exactly <c>burnTime</c> later, and exactly once. Tick-counted, so a test
        /// advances it by hand rather than by sleeping.
        /// </summary>
        [Fact]
        public void DeathArrivesExactlyBurnTimeLaterAndExactlyOnce()
        {
            (VehicleRegistry registry, VehicleBurnClock clock) = Fixture();

            const uint Start = 10;
            const int BurnTicks = 60;

            clock.StartBurning(Tank, BurnTicks, Start);

            // One tick early: still burning.
            clock.Tick(Start + BurnTicks - 1);
            Assert.Equal(0, clock.DiedThisTickCount);

            // On the tick it expires.
            clock.Tick(Start + BurnTicks);
            Assert.Equal(1, clock.DiedThisTickCount);
            Assert.Equal(Tank, clock.DiedThisTick[0]);

            // And never again.
            clock.Tick(Start + BurnTicks + 1);
            Assert.Equal(0, clock.DiedThisTickCount);
            Assert.Equal(1, clock.DeathsAnnounced);

            registry.TryGetState(Tank, out VehicleState state);
            Assert.True(state.Dead);
            Assert.False(state.Burning);
        }

        /// <summary>
        /// A second <c>StartBurning</c> must not restart a countdown that is halfway through — a
        /// burning vehicle taking more fire would otherwise be immortal.
        /// </summary>
        [Fact]
        public void ASecondStartBurningDoesNotRestartTheCountdown()
        {
            (_, VehicleBurnClock clock) = Fixture();

            clock.StartBurning(Tank, burnTicks: 60, nowTick: 10);
            Assert.False(clock.StartBurning(Tank, burnTicks: 60, nowTick: 40));

            clock.Tick(70);
            Assert.Equal(1, clock.DiedThisTickCount);
        }

        /// <summary>
        /// <c>crashSkipsBurn</c> short-circuits to immediate death, through the same
        /// <c>MarkDead</c> — two death paths is how a wreck ends up announced twice or not at all.
        /// </summary>
        [Fact]
        public void ACrashThatSkipsTheBurnKillsImmediatelyAndOnce()
        {
            (_, VehicleBurnClock clock) = Fixture();

            Assert.True(clock.KillImmediately(Tank));
            Assert.Equal(1, clock.DiedThisTickCount);

            Assert.False(clock.KillImmediately(Tank));
            Assert.Equal(1, clock.DeathsAnnounced);
        }

        /// <summary>
        /// A zero-tick burn dies on the tick it starts, rather than never — a prefab that authored
        /// no burn time must still produce a despawn.
        /// </summary>
        [Fact]
        public void AZeroTickBurnStillProducesADeath()
        {
            (_, VehicleBurnClock clock) = Fixture();

            clock.StartBurning(Tank, burnTicks: 0, nowTick: 50);
            clock.Tick(50);

            Assert.Equal(1, clock.DiedThisTickCount);
        }

        /// <summary>
        /// A dead vehicle's seats are emptied, so the next request for one is refused on
        /// <c>RejectedVehicleDead</c> rather than on a seat that looks free.
        /// </summary>
        [Fact]
        public void DeathEmptiesEverySeat()
        {
            (VehicleRegistry registry, VehicleBurnClock clock) = Fixture();

            registry.TrySetOccupant(Tank, 0, 42);
            Assert.Equal((ushort)42, registry.OccupantOf(Tank, 0));

            clock.KillImmediately(Tank);

            Assert.Equal(0, registry.OccupantOf(Tank, 0));
        }

        // ------------------------------------------------------------------ bot claims

        /// <summary>
        /// The bug the <c>int</c> counter cannot express, and the reason V4-D10 exists.
        /// </summary>
        /// <remarks>
        /// <c>Vehicle.seatsClaimedByBots</c> is incremented by <c>ClaimSeat()</c> and drained by a
        /// 10-second whole-vehicle timer that names nobody. Two bots claim and one dies: nothing
        /// decrements, so the vehicle reports itself full to the AI while a seat sits empty, until
        /// a timer takes one off an anonymous pile.
        /// </remarks>
        [Fact]
        public void TwoBotsClaimingAndOneDyingLeavesOneClaim()
        {
            var claims = new BotSeatClaims();

            Assert.True(claims.TryClaim(Tank, 0, botActorId: 11, nowSeconds: 0f));
            Assert.True(claims.TryClaim(Tank, 1, botActorId: 12, nowSeconds: 0f));
            Assert.Equal(2, claims.ClaimCount(Tank));

            claims.Release(botActorId: 11);

            Assert.Equal(1, claims.ClaimCount(Tank));
            Assert.Equal(0, claims.ClaimantOf(Tank, 0));
            Assert.Equal((ushort)12, claims.ClaimantOf(Tank, 1));
        }

        /// <summary>A seat already claimed by a different bot is refused.</summary>
        [Fact]
        public void ASeatClaimedByAnotherBotIsRefused()
        {
            var claims = new BotSeatClaims();

            Assert.True(claims.TryClaim(Tank, 0, 11, nowSeconds: 0f));
            Assert.False(claims.TryClaim(Tank, 0, 12, nowSeconds: 0f));
        }

        /// <summary>
        /// A bot re-claiming its own seat renews the deadline — which is how a bot on a long walk
        /// keeps its reservation without a second mechanism.
        /// </summary>
        [Fact]
        public void ABotReclaimingItsOwnSeatRenewsTheDeadline()
        {
            var claims = new BotSeatClaims();

            claims.TryClaim(Tank, 0, 11, nowSeconds: 0f);
            Assert.True(claims.TryClaim(Tank, 0, 11, nowSeconds: 9f));

            // The original deadline would have passed by now; the renewal moved it.
            claims.ReleaseExpired(nowSeconds: 10.5f);
            Assert.Equal(1, claims.ClaimCount(Tank));

            claims.ReleaseExpired(nowSeconds: 19.5f);
            Assert.Equal(0, claims.ClaimCount(Tank));
        }

        /// <summary>
        /// Expiry is PER CLAIM, not per vehicle. The shipped drain takes one claim off an
        /// anonymous pile every ten seconds, which is precisely why the count cannot be trusted.
        /// </summary>
        [Fact]
        public void AClaimExpiresPerClaimAndNotPerVehicle()
        {
            var claims = new BotSeatClaims();

            claims.TryClaim(Tank, 0, 11, nowSeconds: 0f);
            claims.TryClaim(Tank, 1, 12, nowSeconds: 6f);

            int released = claims.ReleaseExpired(nowSeconds: 11f);

            Assert.Equal(1, released);
            Assert.Equal(1, claims.ClaimCount(Tank));
            Assert.Equal((ushort)12, claims.ClaimantOf(Tank, 1));
        }

        /// <summary>
        /// <c>HasUnclaimedSeats</c> agrees with the claim table after an arbitrary interleave of
        /// claims, releases and expiries.
        /// </summary>
        [Fact]
        public void HasUnclaimedSeatsAgreesWithTheTableAfterAnInterleave()
        {
            var claims = new BotSeatClaims();
            const int Seats = 3;

            Assert.True(claims.HasUnclaimedSeats(Tank, Seats));

            claims.TryClaim(Tank, 0, 11, 0f);
            claims.TryClaim(Tank, 1, 12, 0f);
            claims.TryClaim(Tank, 2, 13, 0f);
            Assert.False(claims.HasUnclaimedSeats(Tank, Seats));

            claims.Release(12);
            Assert.True(claims.HasUnclaimedSeats(Tank, Seats));

            claims.TryClaim(Tank, 1, 14, 1f);
            Assert.False(claims.HasUnclaimedSeats(Tank, Seats));

            claims.ReleaseExpired(nowSeconds: 100f);
            Assert.True(claims.HasUnclaimedSeats(Tank, Seats));
            Assert.Equal(0, claims.ClaimCount(Tank));
        }

        /// <summary>Claims on one vehicle are not affected by claims on another.</summary>
        [Fact]
        public void ClaimsAreScopedToOneVehicle()
        {
            var claims = new BotSeatClaims();

            claims.TryClaim(vehicleId: 1, seatIndex: 0, botActorId: 11, nowSeconds: 0f);
            claims.TryClaim(vehicleId: 2, seatIndex: 0, botActorId: 12, nowSeconds: 0f);

            claims.ReleaseVehicle(1);

            Assert.Equal(0, claims.ClaimCount(1));
            Assert.Equal(1, claims.ClaimCount(2));
        }

        // ---------------------------------------------------------------- clean state

        /// <summary>
        /// design section 8 criterion 13 / 14 — <c>AssertCleanState()</c> covers the vehicle pool,
        /// the registry and the vehicle pair table, and a reset empties all three.
        /// </summary>
        [Fact]
        public void AssertCleanStateCoversTheVehiclePoolAndPairTable()
        {
            var actorIds = new ActorIdPool();
            var vehicleIds = new VehicleIdPool();
            var interest = new Interest.InterestManager();
            var vehicleInterest = new Interest.VehicleInterestTracker();
            var registry = new VehicleRegistry();
            var spawnAcks = new SpawnAckTracker();
            var history = new Combat.HitboxHistory();

            var audit = new ServerStateAudit(
                actorIds, history, interest, spawnAcks, () => 0,
                vehicleIds, vehicleInterest, registry);

            // Dirty every vehicle-side table.
            Assert.True(vehicleIds.TryAcquire(nowTick: 0, out ushort id));
            registry.Add(
                VehicleState.Spawned(id, 0, VehicleKind.Tank, 2, 1000f, 0),
                new VehicleCaptureTests.FakePose());
            vehicleInterest.RecordSend(viewerActorId: 1, vehicleId: id, snapshotIndex: 1);

            ServerStateSnapshot dirty = audit.Capture();
            Assert.False(dirty.IsCleanOfVehicleState);
            Assert.Equal(1, dirty.VehicleIdsInUse);
            Assert.Equal(1, dirty.VehicleInterestPairs);
            Assert.Equal(1, dirty.VehiclesRegistered);

            audit.ResetForNewMatch();

            ServerStateSnapshot clean = audit.Capture();
            Assert.True(clean.IsCleanOfVehicleState);
            Assert.True(clean.IsCleanOfActorState);
        }

        /// <summary>
        /// Five back-to-back resets leave nothing behind — the trap-1 leak shows up on the second
        /// and third round of a server that has been up for an hour with nobody watching.
        /// </summary>
        [Fact]
        public void FiveBackToBackMatchesLeaveNoVehicleStateBehind()
        {
            var vehicleIds = new VehicleIdPool();
            var vehicleInterest = new Interest.VehicleInterestTracker();
            var registry = new VehicleRegistry();

            var audit = new ServerStateAudit(
                new ActorIdPool(), new Combat.HitboxHistory(),
                new Interest.InterestManager(), new SpawnAckTracker(), () => 0,
                vehicleIds, vehicleInterest, registry);

            for (int round = 0; round < 5; round++)
            {
                for (int i = 0; i < 4; i++)
                {
                    Assert.True(vehicleIds.TryAcquire((uint)round, out ushort id));
                    registry.Add(
                        VehicleState.Spawned(id, 0, VehicleKind.Car, 2, 100f, 0),
                        new VehicleCaptureTests.FakePose());
                    vehicleInterest.RecordSend(1, id, (uint)round);
                }

                audit.ResetForNewMatch();

                Assert.True(
                    audit.Capture().IsCleanOfVehicleState,
                    $"round {round} left vehicle state behind: {audit.Capture()}");
            }
        }

        private static (VehicleRegistry, VehicleBurnClock) Fixture()
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(
                    Tank, 0, VehicleKind.Tank, seatCount: 2, maxHealth: 1000f, ownerTeam: 0),
                new VehicleCaptureTests.FakePose());

            return (registry, new VehicleBurnClock(registry));
        }
    }
}
