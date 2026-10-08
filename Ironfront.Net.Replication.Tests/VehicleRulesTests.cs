using Ironfront.Net.Replication.Ai;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Vehicles used with a purpose (phase P28, part 3): the right squads take them, for trips
    /// worth taking, and a tank fights from a distance instead of driving into the defenders.
    /// </summary>
    public sealed class VehicleRulesTests
    {
        private const float Far = 400f;
        private const float Near = 60f;

        [Fact]
        public void ASquadAttackingAFarFlag_TakesATransportNearIt()
            => Assert.True(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.Attack, Far, 40f));

        [Fact]
        public void ASquadAttackingANearFlag_Walks()
            => Assert.False(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.Attack, Near, 10f));

        [Fact]
        public void ATankIsWorthTakingToANearFight_WhenItIsAtHand()
            => Assert.True(VehicleRules.ShouldBoard(VehicleKind.Armour, SquadRole.Attack, Near, 20f));

        [Theory]
        [InlineData(VehicleKind.Transport)]
        [InlineData(VehicleKind.Armour)]
        [InlineData(VehicleKind.Aircraft)]
        [InlineData(VehicleKind.Boat)]
        public void ASquadHoldingAFlag_StaysOnFoot(VehicleKind kind)
            => Assert.False(VehicleRules.ShouldBoard(kind, SquadRole.Defend, Near, 5f));

        /// <summary>
        /// Phase P32: a squad respawned at an HQ and sent to hold a flag across the map takes the
        /// jeep beside it instead of walking. Only standing at (near) the flag keeps it on foot.
        /// </summary>
        [Theory]
        [InlineData(VehicleKind.Transport)]
        [InlineData(VehicleKind.Armour)]
        [InlineData(VehicleKind.Aircraft)]
        public void ASquadSentToHoldAFarFlag_RidesThere(VehicleKind kind)
            => Assert.True(VehicleRules.ShouldBoard(kind, SquadRole.Defend, Far, 5f));

        [Fact]
        public void TheDefenceRideLine_IsTheAttackRideLine()
        {
            Assert.False(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.Defend, VehicleRules.RideDistance - 1f, 5f));
            Assert.True(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.Defend, VehicleRules.RideDistance, 5f));
        }

        [Theory]
        [InlineData(VehicleKind.Transport)]
        [InlineData(VehicleKind.Armour)]
        public void ASquadSneakingRoundTheSide_StaysOnFoot(VehicleKind kind)
            => Assert.False(VehicleRules.ShouldBoard(kind, SquadRole.Flank, Far, 5f));

        [Fact]
        public void NoVehicleIsWorthADetourLongerThanHalfTheTrip()
        {
            float trip = 300f;
            float limit = trip * VehicleRules.MaxDetourShare + VehicleRules.DetourSlack;
            Assert.True(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.Attack, trip, limit));
            Assert.False(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.Attack, trip, limit + 1f));
            Assert.False(VehicleRules.ShouldBoard(VehicleKind.Armour, SquadRole.Attack, Near, 120f));
        }

        [Fact]
        public void ASquadWithNoJob_KeepsTheOriginalsTakeWhatIsNear()
        {
            Assert.True(VehicleRules.ShouldBoard(VehicleKind.Transport, SquadRole.None, float.PositiveInfinity, 140f));
            Assert.True(VehicleRules.ShouldBoard(VehicleKind.Aircraft, SquadRole.None, float.PositiveInfinity, 140f));
        }

        [Fact]
        public void ATankWithATargetInRange_StopsToFire()
            => Assert.True(VehicleRules.HoldStandoff(VehicleKind.Armour, true, VehicleRules.ArmourStandoff - 1f));

        [Fact]
        public void ATankWithATargetBeyondRange_OrNone_DrivesOn()
        {
            Assert.False(VehicleRules.HoldStandoff(VehicleKind.Armour, true, VehicleRules.ArmourStandoff + 1f));
            Assert.False(VehicleRules.HoldStandoff(VehicleKind.Armour, false, 10f));
        }

        [Fact]
        public void ACarDoesNotStopForItsGunner()
            => Assert.False(VehicleRules.HoldStandoff(VehicleKind.Transport, true, 20f));

        [Fact]
        public void ASquadPrefersAHelicopterThenATankThenATransport()
        {
            var kinds = new[] { VehicleKind.Transport, VehicleKind.Boat, VehicleKind.Armour, VehicleKind.Aircraft };
            System.Array.Sort(kinds, (a, b) => VehicleRules.ComparePreference(a, 1000f, 50f, b, 1000f, 50f));
            Assert.Equal(new[] { VehicleKind.Aircraft, VehicleKind.Armour, VehicleKind.Transport, VehicleKind.Boat }, kinds);
        }

        [Fact]
        public void TheBetterKindWinsOverANearerOne()
            => Assert.True(VehicleRules.ComparePreference(
                VehicleKind.Armour, 2000f, 120f, VehicleKind.Transport, 1000f, 5f) < 0);

        [Fact]
        public void AmongTransportsAJeepComesBeforeAQuadBike()
            => Assert.True(VehicleRules.ComparePreference(
                VehicleKind.Transport, 1000f, 60f, VehicleKind.Transport, 400f, 10f) < 0);

        [Fact]
        public void TwoOfTheSameVehicleAreTakenNearestFirst()
            => Assert.True(VehicleRules.ComparePreference(
                VehicleKind.Transport, 1000f, 10f, VehicleKind.Transport, 1000f, 60f) < 0);
    }
}
