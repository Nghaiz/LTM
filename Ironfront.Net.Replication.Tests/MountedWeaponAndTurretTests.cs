using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Tools.SpecChecker;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V6: mounted weapons and turrets. Every test here runs without Unity, which is the whole
    /// point of putting the aim policy, the ammo and the cooldown in a library rather than in two
    /// <c>MonoBehaviour</c>s — the shipped turrets integrated their aim inside <c>Update</c>, so
    /// none of this was reachable from any commit-time gate at all.
    /// </summary>
    public sealed class MountedWeaponAndTurretTests
    {
        private const ushort VehicleId = 4;
        private const byte GunnerSeat = 1;

        private static TurretAimLimits Limits(
            float yawRate = 60f, float pitchRate = 60f, float min = -40f, float max = 15f)
            => new TurretAimLimits
            {
                YawRateDegPerSec   = yawRate,
                PitchRateDegPerSec = pitchRate,
                PitchMin           = min,
                PitchMax           = max,
            };

        // ------------------------------------------------------------------ task 1: aim

        /// <summary>Brainstorm criterion 4: the same arc at any timestep.</summary>
        [Fact]
        public void ATurretTraversesTheSameArcAtAnyTimestep()
        {
            TurretAimLimits limits = Limits();

            float OneSecondAt(int steps)
            {
                var state = new TurretAimState();
                float dt = 1f / steps;
                for (int i = 0; i < steps; i++)
                    TurretAimCore.Step(ref state, 1f, 0f, in limits, dt);
                return state.Yaw;
            }

            float at1 = OneSecondAt(1);
            float at30 = OneSecondAt(30);
            float at144 = OneSecondAt(144);

            // One quantization step of the wire's u16 yaw is 360/65536 degrees. The three agree
            // to far better than that, but the wire is the tolerance that actually matters: two
            // peers cannot disagree by less than one step no matter how exact the maths is.
            const float step = 360f / 65536f;
            Assert.Equal(60f, at1, 3);
            Assert.True(System.MathF.Abs(at30 - at1) < step, $"30 Hz drifted: {at30} vs {at1}");
            Assert.True(System.MathF.Abs(at144 - at1) < step, $"144 Hz drifted: {at144} vs {at1}");
        }

        /// <summary>
        /// Brainstorm criterion 3: an out-of-range axis buys exactly one step's arc.
        /// </summary>
        [Fact]
        public void ATurretClampsOutOfRangeClientInput()
        {
            TurretAimLimits limits = Limits(yawRate: 90f);

            var hostile = new TurretAimState();
            TurretAimCore.Step(ref hostile, 1_000_000f, 0f, in limits, 0.1f);

            var honest = new TurretAimState();
            TurretAimCore.Step(ref honest, 1f, 0f, in limits, 0.1f);

            Assert.Equal(9f, hostile.Yaw, 3);
            Assert.Equal(honest.Yaw, hostile.Yaw, 5);
        }

        [Fact]
        public void ATurretPitchClampsToItsAuthoredLimits()
        {
            // MountedTurret's shipped stops, which were inline literals before V0 named them.
            TurretAimLimits limits = Limits(pitchRate: 600f, min: -40f, max: 15f);

            var up = new TurretAimState();
            for (int i = 0; i < 20; i++) TurretAimCore.Step(ref up, 0f, 1f, in limits, 0.1f);
            Assert.Equal(15f, up.Pitch, 4);

            var down = new TurretAimState();
            for (int i = 0; i < 20; i++) TurretAimCore.Step(ref down, 0f, -1f, in limits, 0.1f);
            Assert.Equal(-40f, down.Pitch, 4);
        }

        /// <summary>
        /// The server treats the wire's absolute aim as a TARGET, so a snap costs one step's arc.
        /// </summary>
        /// <remarks>
        /// The phase plan assumed <c>C_VEHICLE_INPUT</c> carried turret AXES. It does not — the
        /// v3.0.0 wire carries a <c>u16</c> yaw and an <c>i16</c> pitch in degrees, documented in
        /// protocol-spec.md § 4.10 as "what the player asked for". Writing that straight into the
        /// authoritative pose would hand every client an infinite slew rate, which is precisely
        /// what acceptance criterion 2 forbids. This test is the reason the distinction is not a
        /// matter of remembering.
        /// </remarks>
        [Fact]
        public void ARequestedSnapBuysOnlyOneStepsArc()
        {
            TurretAimLimits limits = Limits(yawRate: 90f);
            var state = new TurretAimState();

            TurretAimCore.StepToward(ref state, targetYaw: 180f, targetPitch: 0f, in limits, 0.1f);

            Assert.Equal(9f, state.Yaw, 3);
        }

        [Fact]
        public void ATurretTakesTheShortWayRoundTheWrap()
        {
            TurretAimLimits limits = Limits(yawRate: 3600f);
            var state = new TurretAimState { Yaw = 359f };

            // 2 degrees the short way, not 358 the long way. Subtracting the raw values would
            // spin the tower a full turn every time the aim crossed north.
            TurretAimCore.StepToward(ref state, targetYaw: 1f, targetPitch: 0f, in limits, 1f);

            Assert.Equal(1f, state.Yaw, 3);
        }

        [Fact]
        public void ANonFiniteTargetHoldsTheCurrentPose()
        {
            TurretAimLimits limits = Limits();
            var state = new TurretAimState { Yaw = 42f, Pitch = 5f };

            TurretAimCore.StepToward(ref state, float.NaN, 0f, in limits, 1f);
            TurretAimCore.StepToward(ref state, 0f, float.PositiveInfinity, in limits, 1f);

            // A NaN written into a joint target removes the body from the PhysX simulation
            // outright, and nothing anywhere reports it.
            Assert.Equal(42f, state.Yaw, 4);
            Assert.Equal(5f, state.Pitch, 4);
        }

        /// <summary>
        /// The turret pose survives the vehicle entry, and an unchanged one sets no mask bit.
        /// </summary>
        [Fact]
        public void ATurretPoseRoundTripsThroughTheVehicleEntry()
        {
            const float yaw = 123.75f;
            const float pitch = -12.5f;

            var entry = new VehicleSnapshotEntry
            {
                VehicleId   = VehicleId,
                ChangeMask  = VehicleField.Turret,
                TurretYaw   = Quantize.PackYaw(yaw),
                TurretPitch = Quantize.PackPitchByte(pitch),
            };

            Span<byte> buffer = stackalloc byte[VehicleSnapshotMessage.MaxBodySize];
            var header = new VehicleSnapshotHeader(serverTick: 7, baselineTick: 0, vehicleCount: 1);
            int written = VehicleSnapshotMessage.Write(buffer, in header, new[] { entry });
            Assert.True(written > 0);

            var parsed = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            Assert.True(VehicleSnapshotMessage.TryParse(
                buffer.Slice(0, written), parsed, out _, out int count));
            Assert.Equal(1, count);

            const float yawStep = 360f / 65536f;
            const float pitchStep = 180f / 256f;
            Assert.True(System.MathF.Abs(Quantize.UnpackYaw(parsed[0].TurretYaw) - yaw) <= yawStep);
            Assert.True(
                System.MathF.Abs(Quantize.UnpackPitchByte(parsed[0].TurretPitch) - pitch)
                <= pitchStep);
        }

        // ------------------------------------------------- task 1: server turret authority

        [Fact]
        public void AnUnregisteredTurretHasNoAim()
        {
            var authority = new ServerTurretAuthority();

            Assert.False(authority.TryGetAim(VehicleId, GunnerSeat, out _));
            Assert.False(authority.SetTarget(VehicleId, GunnerSeat, 90f, 0f));
            Assert.Equal(0, authority.TrackedCount);
        }

        [Fact]
        public void AReRegisteredTurretKeepsItsPose()
        {
            var authority = new ServerTurretAuthority();
            TurretAimLimits limits = Limits();

            authority.Register(VehicleId, GunnerSeat, in limits);
            authority.SetAim(VehicleId, GunnerSeat, 90f, 10f);

            // A gunner getting out and back in runs through Seat.SetOccupant every time. A
            // re-register that reset the pose would snap the gun to the prefab's rest heading on
            // every seat entry, which reads as a network glitch and is not one.
            authority.Register(VehicleId, GunnerSeat, in limits, seedYaw: 0f, seedPitch: 0f);

            Assert.True(authority.TryGetAim(VehicleId, GunnerSeat, out TurretAimState aim));
            Assert.Equal(90f, aim.Yaw, 3);
            Assert.Equal(10f, aim.Pitch, 3);
            Assert.Equal(1, authority.TrackedCount);
        }

        [Fact]
        public void ATurretHoldsItsPoseWhenNobodyIsAsking()
        {
            var authority = new ServerTurretAuthority();
            TurretAimLimits limits = Limits(yawRate: 90f);

            authority.Register(VehicleId, GunnerSeat, in limits);
            authority.SetTarget(VehicleId, GunnerSeat, 45f, 0f);
            authority.Step(0.1f);

            authority.TryGetAim(VehicleId, GunnerSeat, out TurretAimState moved);
            Assert.Equal(9f, moved.Yaw, 3);

            // Unlike a throttle, a turret left alone does NOT centre (V5-D11 governs the axes,
            // not this): centring on packet loss would swing the gun off target every time a
            // connection hiccuped.
            authority.ClearTarget(VehicleId, GunnerSeat);
            for (int i = 0; i < 50; i++) authority.Step(0.1f);

            authority.TryGetAim(VehicleId, GunnerSeat, out TurretAimState held);
            Assert.Equal(moved.Yaw, held.Yaw, 4);
        }

        [Fact]
        public void ThePrimaryAimIsTheFirstTrackedSeat()
        {
            var authority = new ServerTurretAuthority();
            TurretAimLimits limits = Limits();

            // V6-D3: the entry's single turret slot belongs to the vehicle's FIRST
            // mounted-weapon seat in seat order.
            authority.Register(VehicleId, seatIndex: 3, in limits);
            authority.Register(VehicleId, seatIndex: 1, in limits);
            authority.SetAim(VehicleId, 1, 90f, 0f);
            authority.SetAim(VehicleId, 3, 270f, 0f);

            Assert.True(authority.TryGetPrimaryAim(VehicleId, out TurretAimState aim));
            Assert.Equal(90f, aim.Yaw, 3);
        }

        [Fact]
        public void UnregisteringAVehicleDropsEveryTurretOnIt()
        {
            var authority = new ServerTurretAuthority();
            TurretAimLimits limits = Limits();

            authority.Register(VehicleId, 1, in limits);
            authority.Register(VehicleId, 2, in limits);
            Assert.Equal(2, authority.TrackedCount);

            authority.UnregisterVehicle(VehicleId);

            // Criterion 13: a turret that outlives its vehicle is a leak that only shows up on
            // the second or third round of a server nobody is watching.
            Assert.Equal(0, authority.TrackedCount);
        }

        // ------------------------------------------------------------- task 3: fire authority

        private static WeaponConfig MountedGun(
            float cooldown = 0.25f, byte clipSize = 10, short spareAmmo = 30,
            bool spendsAmmo = true)
            => new WeaponConfig(
                cooldown: cooldown, spread: 0f, projectilesPerShot: 1, range: 0f,
                damage: 0f, force: 0f, clipSize: clipSize,
                spareAmmo: spareAmmo, spendsAmmo: spendsAmmo);

        private static InputFrame Frame(InputButtons buttons)
            => new InputFrame(moveX: 0, moveZ: 0, yaw: 0, pitch: 0, buttons);

        private static (MountedWeaponRegistry, MountedWeaponAuthority) Armed(
            in WeaponConfig config, ISpareAmmoPool? pool = null)
        {
            var registry = new MountedWeaponRegistry();
            registry.Register(VehicleId, GunnerSeat, WeaponIds.NONE, in config);
            return (registry,
                new MountedWeaponAuthority(registry, pool ?? MountedSpareAmmoPool.Instance));
        }

        [Fact]
        public void AMountedWeaponSpendsServerAmmoAndHonoursItsOwnCooldown()
        {
            WeaponConfig config = MountedGun(cooldown: 0.25f, clipSize: 10);
            (MountedWeaponRegistry registry, MountedWeaponAuthority authority) = Armed(in config);

            MountedFireResult first = authority.Step(
                VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, nowSeconds: 10f);

            Assert.True(first.Fired);
            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState afterFirst);
            Assert.Equal(9, afterFirst.AmmoInClip);

            // A client sending ten frames inside one tick gets one shot and nine refusals. That
            // is the rapid-fire hole server authority is supposed to close for free, and it only
            // closes if the cooldown being checked is the SERVER's number.
            for (int i = 0; i < 9; i++)
            {
                MountedFireResult refused = authority.Step(
                    VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, nowSeconds: 10f);
                Assert.False(refused.Fired);
                Assert.Equal(FireRejection.OnCooldown, refused.Rejection);
            }

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState afterSpam);
            Assert.Equal(9, afterSpam.AmmoInClip);
            Assert.Equal(9, authority.FireRateViolations);

            MountedFireResult second = authority.Step(
                VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, nowSeconds: 10.25f);
            Assert.True(second.Fired);
        }

        [Fact]
        public void AMountedWeaponsClipSurvivesTheGunnerLeaving()
        {
            WeaponConfig config = MountedGun(clipSize: 10);
            (MountedWeaponRegistry registry, MountedWeaponAuthority authority) = Armed(in config);

            authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, 10f);
            authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, 11f);

            // Registration is idempotent, and Seat.SetOccupant runs on EVERY entry. Two players
            // swapping seats on a half-empty coaxial must not each find a full one.
            registry.Register(VehicleId, GunnerSeat, WeaponIds.NONE, in config);

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState state);
            Assert.Equal(8, state.AmmoInClip);
        }

        [Fact]
        public void AMountedReloadDrawsFromThePerWeaponPoolNotTheActorPool()
        {
            WeaponConfig config = MountedGun(clipSize: 10, spareAmmo: 4);
            (MountedWeaponRegistry registry, MountedWeaponAuthority authority) = Armed(in config);

            var actorPool = new ActorSpareAmmoPool();
            actorPool.Set(actorId: VehicleId, slot: GunnerSeat, rounds: 500);

            // Empty the clip.
            float now = 10f;
            for (int i = 0; i < 10; i++)
            {
                authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, now);
                now += 1f;
            }

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState empty);
            Assert.Equal(0, empty.AmmoInClip);

            authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Reload), true, now);
            authority.Step(
                VehicleId, GunnerSeat, Frame(InputButtons.None), true,
                now + ProtocolConstants.RELOAD_SECONDS + 0.01f);

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState reloaded);

            // FOUR, not ten: the weapon's own pool held four rounds. Before V6 the policy refilled
            // to ClipSize unconditionally, which is correct only for an infinite pool.
            Assert.Equal(4, reloaded.AmmoInClip);
            Assert.Equal(0, reloaded.SpareAmmo);

            // And the Actor's five-slot pool was never touched. This is the double-spend V6-D6's
            // seam exists to make structurally impossible rather than merely unlikely.
            var untouched = default(WeaponRuntimeState);
            Assert.Equal(500, actorPool.Remaining(VehicleId, GunnerSeat, in untouched));
        }

        [Fact]
        public void AnInfiniteSpareAmmoSentinelNeverDecrements()
        {
            WeaponConfig config = MountedGun(
                clipSize: 5, spareAmmo: WeaponConfig.InfiniteSpareAmmo);
            (MountedWeaponRegistry registry, MountedWeaponAuthority authority) = Armed(in config);

            float now = 10f;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                for (int i = 0; i < 5; i++)
                {
                    authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, now);
                    now += 1f;
                }

                authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Reload), true, now);
                now += ProtocolConstants.RELOAD_SECONDS + 0.01f;
                authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.None), true, now);

                registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState state);
                Assert.Equal(5, state.AmmoInClip);

                // Decrementing -2 yields -3, which is neither sentinel and reads as a negative
                // round count everywhere downstream.
                Assert.Equal(WeaponConfig.InfiniteSpareAmmo, state.SpareAmmo);
            }
        }

        [Fact]
        public void ANoResupplySentinelIsNeverRefilled()
        {
            WeaponConfig config = MountedGun(
                clipSize: 2, spareAmmo: WeaponConfig.NoResupplySpareAmmo);
            (MountedWeaponRegistry registry, MountedWeaponAuthority authority) = Armed(in config);

            authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, 10f);
            authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, 11f);

            authority.Step(VehicleId, GunnerSeat, Frame(InputButtons.Reload), true, 12f);
            authority.Step(
                VehicleId, GunnerSeat, Frame(InputButtons.None), true,
                12f + ProtocolConstants.RELOAD_SECONDS + 0.01f);

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState state);

            // -1 is a statement about whether an ammo bag may refill this weapon, not a round
            // count. It carries no rounds, so it grants none — and it must not be decremented
            // into -2, which would silently promote the weapon to infinite.
            Assert.Equal(0, state.AmmoInClip);
            Assert.Equal(WeaponConfig.NoResupplySpareAmmo, state.SpareAmmo);
        }

        /// <summary>Task 5: the horn fires forever on a clip of one.</summary>
        [Fact]
        public void AHornSpendsNoAmmo()
        {
            WeaponConfig horn = WeaponCatalog.For(WeaponIds.CAR_HORN);
            Assert.False(horn.SpendsAmmo);

            (MountedWeaponRegistry registry, MountedWeaponAuthority authority) = Armed(in horn);

            float now = 10f;
            for (int i = 0; i < 5; i++)
            {
                MountedFireResult result = authority.Step(
                    VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, now);

                // CarHorn.Shoot never reaches `ammo--`. A server that decremented would leave the
                // horn NoAmmo after one honk and fine offline — a divergence with no error.
                Assert.True(result.Fired, $"honk {i} was refused: {result.Rejection}");
                now += horn.Cooldown + 0.01f;
            }

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState state);
            Assert.Equal(1, state.AmmoInClip);
        }

        [Fact]
        public void ADeadGunnerCannotFire()
        {
            WeaponConfig config = MountedGun();
            (_, MountedWeaponAuthority authority) = Armed(in config);

            MountedFireResult result = authority.Step(
                VehicleId, GunnerSeat, Frame(InputButtons.Fire), gunnerIsAlive: false, 10f);

            Assert.False(result.Fired);
            Assert.Equal(FireRejection.ShooterDead, result.Rejection);
        }

        [Fact]
        public void AnUntrackedSeatIsInertRatherThanThrowing()
        {
            var registry = new MountedWeaponRegistry();
            var authority = new MountedWeaponAuthority(registry, MountedSpareAmmoPool.Instance);

            // A vehicle torn down between the seat lookup and the fire step is an ordinary race,
            // not a programming error.
            MountedFireResult result = authority.Step(
                VehicleId, GunnerSeat, Frame(InputButtons.Fire), true, 10f);

            Assert.False(result.Fired);
            Assert.Equal(0, authority.ShotsFired);
        }

        // ------------------------------------------------------------- task 4: currentMuzzle

        [Fact]
        public void ADecodedMuzzleIndexIsClampedByModulo()
        {
            // A client running a prefab revision with two barrels against a server that has four.
            Assert.Equal(0, VehicleSubtypeTail.FoldMuzzleIndex(200, muzzleCount: 2));
            Assert.Equal(1, VehicleSubtypeTail.FoldMuzzleIndex(201, muzzleCount: 2));

            // And a prefab with no muzzle array at all, which would otherwise divide by zero.
            Assert.Equal(0, VehicleSubtypeTail.FoldMuzzleIndex(7, muzzleCount: 0));
        }

        [Fact]
        public void TheTankTailCarriesTheMuzzleIndex()
        {
            VehicleSubtypeTail.PackTank(0f, currentMuzzle: 3, out byte a, out byte b);

            Assert.Equal(0, a);
            Assert.Equal(3, b);
        }

        // --------------------------------------------------- task 2: the input turret lane

        private static ClampedVehicleInput TurretInput(
            uint tick, ushort vehicleId, float yawDegrees, float pitchDegrees)
            => ClampedVehicleInput.From(new VehicleInputMessage(
                tick, vehicleId,
                throttle: 0, steer: 0, pitchAxis: 0, auxAxis: 0,
                turretYaw: Quantize.PackYaw(yawDegrees),
                turretPitch: Quantize.PackPitch(pitchDegrees),
                buttons: 0));

        [Fact]
        public void AGunnerMayAimButMayNotSteer()
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(VehicleId, 0, VehicleKind.Tank, 4, 1000f, 0),
                new NullPoseSource());
            registry.TrySetOccupant(VehicleId, GunnerSeat, actorId: 12);

            var authority = new VehicleInputAuthority(registry);

            VehicleInputAuthority.Acceptance acceptance =
                authority.Accept(12, TurretInput(1, VehicleId, 90f, 0f), serverTick: 100);

            // The whole of V6 task 2's server half: before this, a gunner's message was thrown
            // away entire as RefusedNotDriver, which is why no turret aim ever reached a server.
            Assert.Equal(VehicleInputAuthority.Acceptance.TurretOnly, acceptance);
            Assert.True(authority.TryGetTurretTarget(
                12, out ushort vehicleId, out byte seat, out float yaw, out _));
            Assert.Equal(VehicleId, vehicleId);
            Assert.Equal(GunnerSeat, seat);
            Assert.Equal(90f, yaw, 1);

            // And V5-D5 is untouched: the axes are still the driver's alone.
            Assert.False(authority.TryGetCurrent(12, 100, out _));
        }

        [Fact]
        public void AnOutsiderAimsNothing()
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(VehicleId, 0, VehicleKind.Tank, 4, 1000f, 0),
                new NullPoseSource());

            var authority = new VehicleInputAuthority(registry);

            Assert.Equal(
                VehicleInputAuthority.Acceptance.Refused,
                authority.Accept(99, TurretInput(1, VehicleId, 90f, 0f), serverTick: 100));

            Assert.Equal(1, authority.TurretRefusedNotOccupant);
            Assert.False(authority.TryGetTurretTarget(99, out _, out _, out _, out _));
        }

        [Fact]
        public void ADriverAimsAndSteersBoth()
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(VehicleId, 0, VehicleKind.Tank, 4, 1000f, 0),
                new NullPoseSource());
            registry.TrySetOccupant(VehicleId, 0, actorId: 11);

            var authority = new VehicleInputAuthority(registry);

            Assert.Equal(
                VehicleInputAuthority.Acceptance.Driver,
                authority.Accept(11, TurretInput(1, VehicleId, 45f, 0f), serverTick: 100));

            Assert.True(authority.TryGetCurrent(11, 100, out _));
            Assert.True(authority.TryGetTurretTarget(11, out _, out byte seat, out _, out _));
            Assert.Equal(0, seat);
        }

        [Fact]
        public void ForgettingAnActorDropsItsTurretTarget()
        {
            var registry = new VehicleRegistry();
            registry.Add(
                VehicleState.Spawned(VehicleId, 0, VehicleKind.Tank, 4, 1000f, 0),
                new NullPoseSource());
            registry.TrySetOccupant(VehicleId, GunnerSeat, actorId: 12);

            var authority = new VehicleInputAuthority(registry);
            authority.Accept(12, TurretInput(1, VehicleId, 90f, 0f), serverTick: 100);

            authority.Forget(12);

            // Without this, a gunner who left and re-entered within the hold window would resume
            // with the aim they left at — the turret equivalent of the throttle V5-D11 closes.
            Assert.False(authority.TryGetTurretTarget(12, out _, out _, out _, out _));
        }

        // ------------------------------------------------------------- criterion 13: cleanliness

        [Fact]
        public void TheMountedWeaponRegistryJoinsTheCleanStateAudit()
        {
            var ids = new ActorIdPool();
            var mounted = new MountedWeaponRegistry();
            var turrets = new ServerTurretAuthority();

            var audit = new ServerStateAudit(
                ids,
                new Combat.HitboxHistory(),
                new Interest.InterestManager(),
                new SpawnAckTracker(),
                mountedWeapons: mounted,
                turrets: turrets);

            Assert.True(audit.Capture().IsClean);

            WeaponConfig config = MountedGun();
            mounted.Register(VehicleId, GunnerSeat, WeaponIds.NONE, in config);
            TurretAimLimits limits = Limits();
            turrets.Register(VehicleId, GunnerSeat, in limits);

            ServerStateSnapshot dirty = audit.Capture();
            Assert.Equal(1, dirty.MountedWeaponsTracked);
            Assert.Equal(1, dirty.TurretsTracked);

            // The failure this guards: a mounted weapon keyed on a vehicle that no longer exists,
            // held for the life of the process, visible only on the second or third round.
            Assert.False(dirty.IsClean);

            audit.ResetForNewMatch();
            Assert.True(audit.Capture().IsClean);
        }

        /// <summary>A vehicle pose source that reports nothing. The registry needs one.</summary>
        private sealed class NullPoseSource : IVehiclePoseSource
        {
            public float TurretYaw => 0f;
            public float TurretPitch => 0f;
            public bool IsInWater => false;
            public bool IsAirborne => false;

            public void ReadPose(
                out Movement.Vec3 position,
                out float rotationX, out float rotationY, out float rotationZ, out float rotationW,
                out Movement.Vec3 linearVelocity,
                out Movement.Vec3 angularVelocity)
            {
                position        = default;
                rotationX       = 0f;
                rotationY       = 0f;
                rotationZ       = 0f;
                rotationW       = 1f;
                linearVelocity  = default;
                angularVelocity = default;
            }

            public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB)
            {
                subtypeA = 0;
                subtypeB = 0;
            }
        }
    }

    /// <summary>
    /// V6-D8: <c>CAR_HORN</c> is a wire id with no loadout-registry row, and the exemption that
    /// says so is checked in BOTH directions.
    /// </summary>
    /// <remarks>
    /// <b>An exemption list owes a staleness companion or it becomes a graveyard.</b> A
    /// one-directional skip would let this entry survive long after the weapon became equippable,
    /// and nothing would ever say so — which is the failure mode
    /// <c>pinned-baseline-test-companion.md</c> exists to prevent. Both halves are driven here
    /// from a fixture, because a gate nobody has watched go red is a gate nobody has proven.
    /// </remarks>
    public sealed class WeaponRegistryExemptionTests
    {
        private static List<Program.WeaponPrefabRecord> EveryRegisteredWeapon()
        {
            var rows = new List<Program.WeaponPrefabRecord>();
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                if (!WeaponIds.IsLoadoutRegistered(id)) continue;
                rows.Add(new Program.WeaponPrefabRecord(WeaponIds.NameOf(id), id));
            }
            return rows;
        }

        [Fact]
        public void AnExemptIdNeedsNoPrefabRow()
        {
            var failures = new List<string>();

            int judged = Program.ValidateWeaponRegistry(
                EveryRegisteredWeapon(), failures);

            Assert.Equal(WeaponIds.MAX_ASSIGNED - 1, judged);
            Assert.Empty(failures);
        }

        [Fact]
        public void TheExemptionHasNoStaleEntries()
        {
            List<Program.WeaponPrefabRecord> rows = EveryRegisteredWeapon();
            rows.Add(new Program.WeaponPrefabRecord("CAR HORN", WeaponIds.CAR_HORN));

            var failures = new List<string>();
            Program.ValidateWeaponRegistry(rows, failures);

            // A row for an exempt id means the weapon became equippable — remove it from
            // WeaponIds.IsLoadoutRegistered — or that the id was reused, which is worse. Either
            // way the exemption has stopped describing the world. Do not delete this check to
            // make a build green.
            Assert.Contains(failures, f => f.Contains("exempts it from the loadout registry"));
        }

        [Fact]
        public void ANonExemptIdStillNeedsItsRow()
        {
            List<Program.WeaponPrefabRecord> rows = EveryRegisteredWeapon();
            rows.RemoveAll(r => r.NetworkId == WeaponIds.RK44);

            var failures = new List<string>();
            Program.ValidateWeaponRegistry(rows, failures);

            // The exemption widened the sweep's skip list; it must not have disabled the sweep.
            Assert.Contains(failures, f => f.Contains("but no prefab entry has it"));
        }
    }
}
