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
            Assert.Equal(0, clock.PendingDeathCount);
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
            Assert.Equal(0, clock.PendingDeathCount);

            // On the tick it expires.
            clock.Tick(Start + BurnTicks);
            Assert.Equal(1, clock.PendingDeathCount);
            Assert.Equal(Tank, clock.PendingDeaths[0]);

            // And never again. The queue is drained by the caller, so it is cleared here the way
            // the snapshot stage clears it — a Tick that reset it itself would discard a crash
            // death that arrived from the input stage earlier in the same frame.
            clock.ClearPendingDeaths();
            clock.Tick(Start + BurnTicks + 1);
            Assert.Equal(0, clock.PendingDeathCount);
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
            Assert.Equal(1, clock.PendingDeathCount);
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
            Assert.Equal(1, clock.PendingDeathCount);

            Assert.False(clock.KillImmediately(Tank));
            Assert.Equal(1, clock.DeathsAnnounced);
            Assert.Equal(1, clock.PendingDeathCount);
        }

        /// <summary>
        /// The two-stage hazard: a crash death from the INPUT stage must survive the snapshot
        /// stage's <c>Tick</c>.
        /// </summary>
        /// <remarks>
        /// <c>KillImmediately</c> fires from <c>Vehicle.Damage</c> during input; <c>Tick</c> fires
        /// from the snapshot stage. A buffer that either of them reset on entry would have the
        /// second discard the first, and the vehicle would be marked dead in the registry, stop
        /// appearing in snapshots, and never be despawned — every client holding a wreck forever
        /// with nothing anywhere to say why. Nothing else in the suite would notice, because both
        /// halves individually behave correctly.
        /// </remarks>
        [Fact]
        public void ACrashDeathFromTheInputStageSurvivesTheSnapshotStagesTick()
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(1, 0, VehicleKind.Car, 2, 100f, 0),
                new VehicleCaptureTests.FakePose());
            registry.Add(
                VehicleState.Spawned(2, 0, VehicleKind.Car, 2, 100f, 0),
                new VehicleCaptureTests.FakePose());

            var clock = new VehicleBurnClock(registry);

            // Vehicle 2 is burning and will expire on tick 40.
            clock.StartBurning(2, burnTicks: 30, nowTick: 10);

            // Vehicle 1 crashes during the input stage of the same frame.
            Assert.True(clock.KillImmediately(1));
            Assert.Equal(1, clock.PendingDeathCount);

            // The snapshot stage runs. BOTH must be pending.
            clock.Tick(40);

            Assert.Equal(2, clock.PendingDeathCount);
            Assert.Contains((ushort)1, new[] { clock.PendingDeaths[0], clock.PendingDeaths[1] });
            Assert.Contains((ushort)2, new[] { clock.PendingDeaths[0], clock.PendingDeaths[1] });
        }

        /// <summary>
        /// A repair that puts the fire out must stop the countdown, or the clock despawns a
        /// vehicle that is drivable and possibly occupied.
        /// </summary>
        /// <remarks>
        /// The scene's <c>Vehicle.Repair</c> reaches <c>StopBurning()</c> after three repairs,
        /// which clears its own <c>burning</c> flag and knows nothing about
        /// <see cref="VehicleState"/>. Without <see cref="VehicleBurnClock.CancelBurn"/> the
        /// tick-counted clock kept <c>BurnEndsAtTick</c> armed and killed the repaired vehicle on
        /// schedule — telling every client it was gone while the GameObject stayed solid in the
        /// world. Nothing about the repair itself would have looked wrong.
        /// </remarks>
        [Fact]
        public void ExtinguishingABurnStopsTheCountdown()
        {
            (VehicleRegistry registry, VehicleBurnClock clock) = Fixture();

            clock.StartBurning(Tank, burnTicks: 60, nowTick: 10);
            Assert.True(clock.CancelBurn(Tank));

            registry.TryGetState(Tank, out VehicleState state);
            Assert.False(state.Burning);
            Assert.False(state.Dead);

            // Well past where the burn would have expired.
            clock.Tick(200);

            Assert.Equal(0, clock.PendingDeathCount);
            Assert.Equal(0, clock.DeathsAnnounced);
            Assert.Equal(1, clock.BurnsExtinguished);
        }

        /// <summary>A vehicle that is not burning, or already dead, cannot be extinguished.</summary>
        [Fact]
        public void ExtinguishingRefusesAVehicleThatIsNotBurning()
        {
            (_, VehicleBurnClock clock) = Fixture();

            Assert.False(clock.CancelBurn(Tank));

            clock.KillImmediately(Tank);
            Assert.False(clock.CancelBurn(Tank));
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

            Assert.Equal(1, clock.PendingDeathCount);
        }

        /// <summary>
        /// Acceptance criterion 9's second half, which the plan named
        /// (<c>ATankDeathEmitsWreckedAndStopsSnapshotting</c>) and nothing graded: <b>no snapshot
        /// entry for a vehicle follows its death.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ordering this pins is <c>ServerTickLoop.BuildAndSendSnapshots</c>'s: deaths are
        /// resolved, and the despawn unregisters the vehicle, BEFORE the capture runs. Capturing
        /// first and killing afterwards also compiles, also passes every other test in this file,
        /// and ships one final entry for a vehicle whose <c>S_VEHICLE_DESPAWN</c> is already in
        /// flight on a different channel with no ordering guarantee between them.
        /// </para>
        /// <para>
        /// <b>This is the engine-free half.</b> It proves that an unregistered vehicle produces no
        /// entry; that the tick loop unregisters in the right order is a Unity-side fact and is on
        /// the client track's Editor list.
        /// </para>
        /// </remarks>
        [Fact]
        public void NoSnapshotEntryFollowsAVehiclesDeath()
        {
            (VehicleRegistry registry, VehicleBurnClock clock) = Fixture();
            var world = new VehicleWorldSnapshot();

            // Alive: it is captured.
            registry.CaptureInto(world, serverTick: 1);
            Assert.Equal(1, world.VehicleCount);
            Assert.True(world.TryFind(Tank, out _));

            // Dead, and the despawn has unregistered it — which is what the tick loop does before
            // it captures.
            clock.KillImmediately(Tank);
            Assert.Equal(1, clock.PendingDeathCount);
            Assert.True(registry.Remove(Tank));

            registry.CaptureInto(world, serverTick: 2);

            Assert.Equal(0, world.VehicleCount);
            Assert.False(world.TryFind(Tank, out _));
            Assert.Equal(-1, world.IndexOf(Tank));
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
        /// A vehicle whose every seat is claimed refuses a new bot on every index, and
        /// <c>HasUnclaimedSeats</c> agrees.
        /// </summary>
        /// <remarks>
        /// This is the state the Unity-side <c>NetVehicleAuthority.TryClaimSeat</c> must NOT
        /// answer with "not mine". Reading that as "fall back to <c>seatsClaimedByBots</c>" would
        /// put the claim in a counter that <c>Vehicle.ClaimedSeatCount</c> does not read on a
        /// replicated vehicle — so the count would under-report at exactly the moment it must say
        /// "full", and the AI would keep sending bots to a vehicle with no room.
        /// </remarks>
        [Fact]
        public void AFullyClaimedVehicleRefusesEverySeat()
        {
            var claims = new BotSeatClaims();
            const int Seats = 2;

            Assert.True(claims.TryClaim(Tank, 0, 11, 0f));
            Assert.True(claims.TryClaim(Tank, 1, 12, 0f));

            for (byte seat = 0; seat < Seats; seat++)
                Assert.False(claims.TryClaim(Tank, seat, botActorId: 13, nowSeconds: 0f));

            Assert.Equal(Seats, claims.ClaimCount(Tank));
            Assert.False(claims.HasUnclaimedSeats(Tank, Seats));
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

        /// <summary>
        /// The id quarantine must outlast the delta encoder's baseline history, and nothing said
        /// so until now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A despawned vehicle id stays in every client's <see cref="VehicleDeltaEncoder"/>
        /// baseline — the encoder has no per-id forget, only a whole-encoder <c>Reset</c> at match
        /// reset. So if an id were reissued while a baseline still named it, a delta measured
        /// against that baseline would apply the wreck's health, flags and turret angle to the
        /// live replacement. That is precisely the hazard <c>VehicleIdPool</c>'s own doc comment
        /// warns about, and it is closed today only because 32 snapshots at 20 Hz is 1.6 s against
        /// a 5 s quarantine.
        /// </para>
        /// <para>
        /// <b>Nothing related the two constants.</b> Raise <c>BaselineHistory</c> to 128, or move
        /// <c>SIM_TICK_RATE</c> to 60 (which halves the quarantine in seconds while leaving the
        /// tick count untouched), and the hazard silently reopens with every test still green.
        /// This is the guard that goes red instead.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheIdQuarantineOutlastsTheDeltaBaselineHistory()
        {
            float quarantineSeconds =
                ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS
                / (float)ProtocolConstants.SIM_TICK_RATE;

            float baselineSeconds =
                VehicleDeltaEncoder.BaselineHistory / (float)ProtocolConstants.SNAPSHOT_RATE;

            Assert.True(
                quarantineSeconds > baselineSeconds,
                $"quarantine is {quarantineSeconds:0.00}s but a client can still hold a baseline "
                + $"naming a released id for {baselineSeconds:0.00}s. Reissuing inside that window "
                + "applies a wreck's state to its replacement, silently.");

            // And with real margin, not by a hair -- the point is that a modest change to either
            // constant must not be able to cross it unnoticed.
            Assert.True(quarantineSeconds >= 2f * baselineSeconds);
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
