using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Input.Tests
{
    /// <summary>
    /// V5 task 5, client half: the four helicopter axes, the wire slots they occupy, and what a
    /// network source reports when it is not driving.
    /// </summary>
    /// <remarks>
    /// <b>The mapping is implicit in one line of <c>Helicopter.cs</c> and has no field names at
    /// all</b> — <c>vector.x</c>, <c>.y</c>, <c>.z</c>, <c>.w</c>, indexed straight into torque
    /// and lift. Getting a component wrong produces a helicopter that flies, badly, in a way
    /// nobody can attribute. So the mapping is pinned here as well as documented (V5-D10).
    /// </remarks>
    public class HelicopterAxisMappingTests
    {
        [Fact]
        public void TheHelicopterAxisMappingIsComponentIdentical()
        {
            // Read as: this is the Vector4 FpsActorController.HelicopterInput assembles, and
            // Helicopter.cs indexes .x as yaw, .y as collective, .z as roll and .w as pitch.
            var axes = new HelicopterAxes(yaw: 0.1f, collective: 0.2f, roll: 0.3f, pitch: 0.4f);

            Assert.Equal(0.1f, axes.Yaw);
            Assert.Equal(0.2f, axes.Collective);
            Assert.Equal(0.3f, axes.Roll);
            Assert.Equal(0.4f, axes.Pitch);
        }

        [Fact]
        public void TheWireSlotsAreTheVectorComponentsInDeclarationOrder()
        {
            var axes = new HelicopterAxes(yaw: 0.1f, collective: 0.2f, roll: 0.3f, pitch: 0.4f);

            // C_VEHICLE_INPUT's four axes are generic slots whose meaning is per VehicleKind.
            // Reading the field names as if they were helicopter controls -- "throttle must be
            // the collective" -- is exactly the mistake HelicopterAxes exists to prevent.
            Assert.Equal(axes.Yaw, axes.ThrottleSlot);
            Assert.Equal(axes.Collective, axes.SteerSlot);
            Assert.Equal(axes.Roll, axes.PitchAxisSlot);
            Assert.Equal(axes.Pitch, axes.AuxAxisSlot);
        }

        [Fact]
        public void TheSlotsRoundTripBackToTheSameFourControls()
        {
            var sent = new HelicopterAxes(yaw: -0.7f, collective: 0.9f, roll: 0.25f, pitch: -0.5f);

            HelicopterAxes received = HelicopterAxes.FromWireSlots(
                sent.ThrottleSlot, sent.SteerSlot, sent.PitchAxisSlot, sent.AuxAxisSlot);

            Assert.Equal(sent.Yaw, received.Yaw);
            Assert.Equal(sent.Collective, received.Collective);
            Assert.Equal(sent.Roll, received.Roll);
            Assert.Equal(sent.Pitch, received.Pitch);
        }

        [Fact]
        public void TheSlotsSurviveTheWireQuantiserWithTheSignsIntact()
        {
            // The sign is the part that goes wrong silently: a helicopter with an inverted roll
            // still flies.
            var sent = new HelicopterAxes(yaw: -1f, collective: 1f, roll: -0.5f, pitch: 0.5f);

            var message = new VehicleInputMessage(
                tick: 3, vehicleId: 9,
                Quantize.PackMoveAxis(sent.ThrottleSlot),
                Quantize.PackMoveAxis(sent.SteerSlot),
                Quantize.PackMoveAxis(sent.PitchAxisSlot),
                Quantize.PackMoveAxis(sent.AuxAxisSlot),
                turretYaw: 0, turretPitch: 0, buttons: 0);

            var buffer = new byte[VehicleInputMessage.Size];
            Assert.Equal(VehicleInputMessage.Size, message.Write(buffer));
            Assert.True(VehicleInputMessage.TryParse(buffer, out VehicleInputMessage parsed));

            HelicopterAxes received = HelicopterAxes.FromWireSlots(
                Quantize.UnpackMoveAxis(parsed.Throttle),
                Quantize.UnpackMoveAxis(parsed.Steer),
                Quantize.UnpackMoveAxis(parsed.PitchAxis),
                Quantize.UnpackMoveAxis(parsed.AuxAxis));

            Assert.Equal(-1f, received.Yaw, 2);
            Assert.Equal(1f, received.Collective, 2);
            Assert.Equal(-0.5f, received.Roll, 2);
            Assert.Equal(0.5f, received.Pitch, 2);
        }

        [Fact]
        public void ANetSourceReportsTheAxesItWasGiven()
        {
            var source = new NetInputSource();

            Assert.False(source.IsDriving);
            Assert.Equal(0f, source.HeliYaw);

            source.SetVehicleAxes(
                steer: 0.3f, throttle: -0.6f,
                new HelicopterAxes(yaw: 0.1f, collective: 0.2f, roll: 0.3f, pitch: 0.4f));

            Assert.True(source.IsDriving);
            Assert.Equal(0.1f, source.HeliYaw);
            Assert.Equal(0.2f, source.HeliCollective);
            Assert.Equal(0.3f, source.HeliRoll);
            Assert.Equal(0.4f, source.HeliPitch);

            // CarInput() is (MoveX, MoveZ) and Car.FixedUpdate reads .x as the steering target,
            // .y as throttle. Swapping these produces a car that steers with the accelerator.
            Assert.Equal(0.3f, source.MoveX);
            Assert.Equal(-0.6f, source.MoveZ);
        }

        [Fact]
        public void ClearingTheAxesCentresThemAndReturnsMoveToTheInputFrame()
        {
            var source = new NetInputSource();
            source.SetFrame(InputFrame.FromFloats(0.8f, -0.4f, 90f, 0f, InputButtons.None));

            source.SetVehicleAxes(
                steer: 1f, throttle: 1f,
                new HelicopterAxes(yaw: 1f, collective: 1f, roll: 1f, pitch: 1f));

            source.ClearVehicleAxes();

            // This is what the server applies when the hold window expires: the driver coasts to
            // a stop rather than holding full throttle forever.
            Assert.False(source.IsDriving);
            Assert.Equal(0f, source.HeliYaw);
            Assert.Equal(0f, source.HeliCollective);
            Assert.Equal(0f, source.HeliRoll);
            Assert.Equal(0f, source.HeliPitch);

            Assert.Equal(0.8f, source.MoveX, 1);
            Assert.Equal(-0.4f, source.MoveZ, 1);
        }

        [Fact]
        public void LookDeltaStaysZeroOnANetworkSource()
        {
            // Genuinely unrepresentable, not a workaround: C_INPUT carries an absolute yaw and a
            // per-frame mouse delta is a different quantity. The four helicopter axes are the
            // answer to that, and they do not change this.
            var source = new NetInputSource();
            source.SetVehicleAxes(1f, 1f, new HelicopterAxes(1f, 1f, 1f, 1f));

            Assert.Equal(0f, source.LookDeltaX);
            Assert.Equal(0f, source.LookDeltaY);
        }

        [Fact]
        public void ANullSourcePresentsNeutralAxes()
        {
            Assert.Equal(0f, NullInputSource.Instance.HeliYaw);
            Assert.Equal(0f, NullInputSource.Instance.HeliCollective);
            Assert.Equal(0f, NullInputSource.Instance.HeliRoll);
            Assert.Equal(0f, NullInputSource.Instance.HeliPitch);

            HelicopterAxes fromNull = HelicopterAxes.From(NullInputSource.Instance);
            Assert.Equal(0f, fromNull.Yaw);
            Assert.Equal(0f, fromNull.Pitch);
        }

        [Fact]
        public void ReadingTheAxesOffASourceUsesTheSameFourMembers()
        {
            var source = new NetInputSource();
            source.SetVehicleAxes(0f, 0f, new HelicopterAxes(1f, 2f, 3f, 4f));

            HelicopterAxes axes = HelicopterAxes.From(source);

            Assert.Equal(1f, axes.Yaw);
            Assert.Equal(2f, axes.Collective);
            Assert.Equal(3f, axes.Roll);
            Assert.Equal(4f, axes.Pitch);
        }
    }
}
