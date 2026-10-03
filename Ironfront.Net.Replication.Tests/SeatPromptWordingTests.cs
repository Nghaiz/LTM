using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    public sealed class SeatPromptWordingTests
    {
        [Theory]
        [InlineData("jeep", VehicleKind.Car, "JEEP")]
        [InlineData("jeep (3)", VehicleKind.Car, "JEEP")]
        [InlineData("quadbike(Clone)", VehicleKind.Car, "QUAD BIKE")]
        [InlineData("rhib 2", VehicleKind.Boat, "RHIB")]
        [InlineData("helicopter", VehicleKind.Helicopter, "HELICOPTER")]
        [InlineData("tank", VehicleKind.Tank, "TANK")]
        [InlineData("armoured_truck", VehicleKind.Car, "ARMOURED TRUCK")]
        [InlineData("", VehicleKind.Boat, "BOAT")]
        [InlineData(null, VehicleKind.Car, "VEHICLE")]
        [InlineData("(Clone)", VehicleKind.Tank, "TANK")]
        public void VehicleNamesReadAsWords(string? objectName, VehicleKind kind, string expected)
        {
            Assert.Equal(expected, SeatPromptWording.VehicleName(objectName, kind));
        }

        [Theory]
        [InlineData(VehicleKind.Car, 0, 4, "DRIVE THE JEEP")]
        [InlineData(VehicleKind.Helicopter, 0, 4, "FLY THE JEEP")]
        [InlineData(VehicleKind.Boat, 0, 4, "PILOT THE JEEP")]
        [InlineData(VehicleKind.Tank, 1, 2, "BOARD THE JEEP")]
        [InlineData(VehicleKind.Car, 4, 4, "THE JEEP IS FULL")]
        [InlineData(VehicleKind.Car, 0, 0, "THE JEEP IS FULL")]
        public void TheActionNamesWhatTheKeyWillDo(VehicleKind kind, int crew, int seats, string expected)
        {
            Assert.Equal(expected, SeatPromptWording.Action(kind, "JEEP", crew, seats));
        }

        [Theory]
        [InlineData(0, 4, false, "4 SEATS  ·  EMPTY")]
        [InlineData(0, 1, false, "1 SEAT")]
        [InlineData(2, 4, false, "2 / 4 SEATS TAKEN")]
        [InlineData(1, 2, true, "ENEMY CREW ABOARD")]
        [InlineData(0, 0, false, "")]
        public void TheDetailCountsTheSeats(int crew, int seats, bool enemy, string expected)
        {
            Assert.Equal(expected, SeatPromptWording.Detail(crew, seats, enemy));
        }

        [Fact]
        public void AFullVehicleCannotBeBoarded()
        {
            Assert.True(SeatPromptWording.CanBoard(3, 4));
            Assert.False(SeatPromptWording.CanBoard(4, 4));
            Assert.False(SeatPromptWording.CanBoard(0, 0));
        }
    }
}
