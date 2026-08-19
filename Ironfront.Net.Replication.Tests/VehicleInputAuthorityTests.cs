using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V5 task 5, server half: who may steer what, for how long after their last packet, and
    /// what an out-of-range axis is worth.
    /// </summary>
    public sealed class VehicleInputAuthorityTests
    {
        private const ushort VehicleId = 4;
        private const ushort DriverActor = 11;
        private const ushort PassengerActor = 12;
        private const ushort BystanderActor = 13;

        [Fact]
        public void TheDriverSInputIsAccepted()
        {
            (VehicleRegistry registry, VehicleInputAuthority authority) = Seated();

            Assert.True(authority.TryAccept(DriverActor, Input(tick: 1, throttle: 1f), serverTick: 100));
            Assert.Equal(1, authority.Accepted);
            Assert.Equal(0, authority.RefusedNotDriver);

            Assert.True(authority.TryGetCurrent(DriverActor, 100, out ClampedVehicleInput held));
            Assert.Equal(1f, held.Throttle, 2);

            _ = registry;
        }

        [Fact]
        public void InputFromANonDriverIsRefused()
        {
            (VehicleRegistry registry, VehicleInputAuthority authority) = Seated();

            // A passenger is in the vehicle and still may not steer it. Seat 1 is not seat 0,
            // and the message's vehicleId is the client's claim rather than the server's record.
            registry.TrySetOccupant(VehicleId, 1, PassengerActor);

            Assert.False(authority.TryAccept(PassengerActor, Input(tick: 1, throttle: 1f), serverTick: 100));
            Assert.False(authority.TryAccept(BystanderActor, Input(tick: 1, throttle: 1f), serverTick: 100));

            Assert.Equal(2, authority.RefusedNotDriver);
            Assert.False(authority.TryGetCurrent(PassengerActor, 100, out _));
        }

        [Fact]
        public void InputNamingAVehicleTheSenderIsNotInIsRefused()
        {
            (VehicleRegistry registry, VehicleInputAuthority authority) = Seated();

            // Exactly the window a same-frame leave-then-enter opens, which is why
            // C_VEHICLE_INPUT carries a vehicleId the server already knows.
            var wrongVehicle = ClampedVehicleInput.From(
                new VehicleInputMessage(
                    tick: 1, vehicleId: VehicleId + 1,
                    throttle: 127, steer: 0, pitchAxis: 0, auxAxis: 0,
                    turretYaw: 0, turretPitch: 0, buttons: 0));

            Assert.False(authority.TryAccept(DriverActor, in wrongVehicle, serverTick: 100));
            Assert.Equal(1, authority.RefusedNotDriver);

            _ = registry;
        }

        [Fact]
        public void AxesDecayToZeroAfterTheHoldWindow()
        {
            (_, VehicleInputAuthority authority) = Seated();

            authority.TryAccept(DriverActor, Input(tick: 1, throttle: 1f), serverTick: 100);

            // Inside the window the throttle stands.
            uint lastLive = 100u + VehicleInputAuthority.VehicleInputHoldTicks;
            Assert.True(authority.TryGetCurrent(DriverActor, lastLive, out ClampedVehicleInput held));
            Assert.Equal(1f, held.Throttle, 2);

            // One tick past it the axes centre, so a driver whose connection stalls coasts to a
            // stop instead of driving into the sea at full throttle.
            Assert.False(authority.TryGetCurrent(DriverActor, lastLive + 1, out ClampedVehicleInput decayed));
            Assert.Equal(0f, decayed.Throttle);
            Assert.Equal(0f, decayed.Steer);
            Assert.Equal(0, decayed.VehicleId);
            Assert.Equal(1, authority.DecayedReads);
        }

        [Fact]
        public void TheHoldWindowIsTwoHundredMillisecondsAtTheSimTickRate()
        {
            // Named rather than measured elsewhere: the window is a statement about how long the
            // server keeps believing a fact it has not heard repeated, and 6 ticks is that
            // statement at 30 Hz.
            Assert.Equal(6, VehicleInputAuthority.VehicleInputHoldTicks);
            Assert.Equal(
                200,
                VehicleInputAuthority.VehicleInputHoldTicks * 1000 / ProtocolConstants.SIM_TICK_RATE);
        }

        [Fact]
        public void AFreshInputRearmsTheHoldWindow()
        {
            (_, VehicleInputAuthority authority) = Seated();

            authority.TryAccept(DriverActor, Input(tick: 1, throttle: 1f), serverTick: 100);
            authority.TryAccept(DriverActor, Input(tick: 2, throttle: 0.5f), serverTick: 105);

            uint stillLive = 105u + VehicleInputAuthority.VehicleInputHoldTicks;
            Assert.True(authority.TryGetCurrent(DriverActor, stillLive, out ClampedVehicleInput held));
            Assert.Equal(0.5f, held.Throttle, 1);
        }

        [Fact]
        public void AnInputNotNewerThanTheOneHeldIsRefused()
        {
            (_, VehicleInputAuthority authority) = Seated();

            authority.TryAccept(DriverActor, Input(tick: 10, throttle: 1f), serverTick: 100);

            // A replayed or reordered packet must not re-arm a stale throttle.
            Assert.False(authority.TryAccept(DriverActor, Input(tick: 9, throttle: -1f), serverTick: 101));
            Assert.Equal(1, authority.RefusedStale);

            Assert.True(authority.TryGetCurrent(DriverActor, 101, out ClampedVehicleInput held));
            Assert.Equal(1f, held.Throttle, 2);
        }

        [Fact]
        public void TheTickComparisonSurvivesTheU32Wrap()
        {
            (_, VehicleInputAuthority authority) = Seated();

            authority.TryAccept(DriverActor, Input(tick: uint.MaxValue - 1, throttle: 1f), serverTick: 100);

            // A plain `>` would refuse every input for a while after the wrap: a driver whose
            // controls stop working for no reason anybody can reproduce.
            Assert.True(authority.TryAccept(DriverActor, Input(tick: 1, throttle: -1f), serverTick: 101));
            Assert.Equal(2, authority.Accepted);
        }

        [Fact]
        public void ForgettingADriverDropsTheirAxes()
        {
            (_, VehicleInputAuthority authority) = Seated();

            authority.TryAccept(DriverActor, Input(tick: 1, throttle: 1f), serverTick: 100);
            authority.Forget(DriverActor);

            // An id that is reissued must not inherit the previous occupant's throttle.
            Assert.False(authority.TryGetCurrent(DriverActor, 100, out _));
        }

        [Fact]
        public void OutOfRangeAxesAreClampedOnDecode()
        {
            // The wire cannot express the full hostile range and CAN still express an
            // out-of-range one: -128 unpacks to -1.0079 at MOVE_AXIS_SCALE, a permanent 0.8%
            // advantage for a client that simply writes the one value the encoder never
            // produces.
            var hostile = ClampedVehicleInput.From(
                new VehicleInputMessage(
                    tick: 1, vehicleId: VehicleId,
                    throttle: sbyte.MinValue, steer: sbyte.MinValue,
                    pitchAxis: sbyte.MinValue, auxAxis: sbyte.MinValue,
                    turretYaw: 0, turretPitch: 0, buttons: 0));

            Assert.Equal(-1f, hostile.Throttle);
            Assert.Equal(-1f, hostile.Steer);
            Assert.Equal(-1f, hostile.PitchAxis);
            Assert.Equal(-1f, hostile.AuxAxis);
        }

        [Fact]
        public void TurretAimFieldsRoundTripAsZero()
        {
            // V5 sends them as zeros and V6 fills them in, so V6 needs no wire change. This pins
            // the round trip rather than the absence: a field that silently stopped decoding
            // would be found by V6 and not before.
            var message = new VehicleInputMessage(
                tick: 7, vehicleId: VehicleId,
                throttle: 0, steer: 0, pitchAxis: 0, auxAxis: 0,
                turretYaw: 0, turretPitch: 0, buttons: 0);

            var buffer = new byte[VehicleInputMessage.Size];
            Assert.Equal(VehicleInputMessage.Size, message.Write(buffer));
            Assert.True(VehicleInputMessage.TryParse(buffer, out VehicleInputMessage parsed));

            ClampedVehicleInput clamped = ClampedVehicleInput.From(in parsed);

            Assert.Equal(0f, clamped.TurretYaw, 3);
            Assert.Equal(0f, clamped.TurretPitch, 3);
            Assert.Equal(7u, clamped.Tick);
            Assert.Equal(VehicleId, clamped.VehicleId);
        }

        [Fact]
        public void ANonFiniteAxisResolvesToNeutralRatherThanRemovingTheVehicleFromPhysX()
        {
            // Mathf.Clamp(NaN, -1, 1) returns NaN -- the comparison chain is false in both
            // directions -- and one NaN axis into AddForce removes the vehicle from the
            // simulation entirely.
            Assert.Equal(0f, VehicleInputClamp.Axis(float.NaN));
            Assert.Equal(0f, VehicleInputClamp.Axis(float.PositiveInfinity));
            Assert.Equal(0f, VehicleInputClamp.Axis(float.NegativeInfinity));
        }

        [Fact]
        public void ResetDropsEveryRecordAndCounter()
        {
            (_, VehicleInputAuthority authority) = Seated();

            authority.TryAccept(DriverActor, Input(tick: 1, throttle: 1f), serverTick: 100);
            authority.TryAccept(BystanderActor, Input(tick: 1, throttle: 1f), serverTick: 100);

            authority.Reset();

            Assert.Equal(0, authority.Accepted);
            Assert.Equal(0, authority.RefusedNotDriver);
            Assert.False(authority.TryGetCurrent(DriverActor, 100, out _));
        }

        // ------------------------------------------------------------------ helpers

        private static (VehicleRegistry, VehicleInputAuthority) Seated()
        {
            var registry = new VehicleRegistry();

            registry.Add(
                VehicleState.Spawned(
                    VehicleId, spawnerId: 1, VehicleKind.Car,
                    seatCount: 4, maxHealth: 1000f, ownerTeam: 0),
                new StaticPose());

            registry.TrySetOccupant(VehicleId, VehicleInputAuthority.DriverSeatIndex, DriverActor);

            return (registry, new VehicleInputAuthority(registry));
        }

        private static ClampedVehicleInput Input(uint tick, float throttle)
            => ClampedVehicleInput.From(
                new VehicleInputMessage(
                    tick, VehicleId,
                    Quantize.PackMoveAxis(throttle), 0, 0, 0,
                    turretYaw: 0, turretPitch: 0, buttons: 0));

        /// <summary>A pose source that never moves. The registry needs one; this test does not.</summary>
        private sealed class StaticPose : IVehiclePoseSource
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
                position = default;
                rotationX = 0f;
                rotationY = 0f;
                rotationZ = 0f;
                rotationW = 1f;
                linearVelocity = default;
                angularVelocity = default;
            }

            public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB)
            {
                subtypeA = 0;
                subtypeB = 0;
            }
        }
    }
}
