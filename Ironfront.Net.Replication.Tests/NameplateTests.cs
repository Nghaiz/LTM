using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// When a name and health bar float over a head, and how large. Playtest 2026-09-28,
    /// feature 1.
    /// </summary>
    public sealed class NameplateTests
    {
        [Fact]
        public void ATeammate_IsShownThroughCover_ButDimmed()
        {
            Assert.Equal(1f, NameplateRules.Opacity(teammate: true, distance: 40f, covered: false));
            Assert.Equal(
                NameplateRules.CoveredTeammateOpacity,
                NameplateRules.Opacity(teammate: true, distance: 40f, covered: true));
        }

        /// <summary>An enemy plate through a wall would be a wallhack the game handed out.</summary>
        [Fact]
        public void AnEnemy_IsShownOnlyInSight()
        {
            Assert.Equal(1f, NameplateRules.Opacity(teammate: false, distance: 40f, covered: false));
            Assert.Equal(0f, NameplateRules.Opacity(teammate: false, distance: 40f, covered: true));
            Assert.Equal(0f, NameplateRules.Opacity(teammate: false, distance: 5f, covered: true));
        }

        [Fact]
        public void EachSide_HasItsOwnRange()
        {
            Assert.Equal(0f, NameplateRules.Opacity(false, NameplateRules.EnemyRange + 1f, false));
            Assert.True(NameplateRules.Opacity(true, NameplateRules.EnemyRange + 1f, false) > 0f);
            Assert.Equal(0f, NameplateRules.Opacity(true, NameplateRules.TeammateRange + 1f, false));
        }

        [Fact]
        public void APlate_FadesOverTheLastStretchOfItsRange()
        {
            float range = NameplateRules.EnemyRange;
            float fadeFrom = range * (1f - NameplateRules.FadeShare);

            Assert.Equal(1f, NameplateRules.Opacity(false, fadeFrom, false));
            Assert.InRange(NameplateRules.Opacity(false, (fadeFrom + range) * 0.5f, false), 0.45f, 0.55f);
            Assert.Equal(0f, NameplateRules.Opacity(false, range, false), 3);
        }

        [Fact]
        public void APlate_ShrinksWithDistance_ButStaysReadable()
        {
            Assert.Equal(1f, NameplateRules.Scale(3f));
            Assert.Equal(1f, NameplateRules.Scale(NameplateRules.FullSizeDistance));
            Assert.True(NameplateRules.Scale(50f) < 1f);
            Assert.True(NameplateRules.Scale(50f) > NameplateRules.Scale(80f));
            Assert.Equal(NameplateRules.MinScale, NameplateRules.Scale(500f));
        }

        [Fact]
        public void APlate_SitsAboveTheHeadForEveryPose()
        {
            float clearance = NameplateRules.HeadClearance;

            Assert.Equal(MovementCore.StandHeight + clearance, NameplateRules.AnchorHeight(false, false, false), 3);
            Assert.Equal(NameplateRules.CrouchedHeadHeight + clearance, NameplateRules.AnchorHeight(true, false, false), 3);
            Assert.Equal(NameplateRules.ProneHeadHeight + clearance, NameplateRules.AnchorHeight(false, true, false), 3);
            Assert.Equal(NameplateRules.SeatedHeadHeight + clearance, NameplateRules.AnchorHeight(true, false, true), 3);

            // The crouched collider is far below a kneeling head; a plate placed on it would sit
            // in the player's face.
            Assert.True(NameplateRules.CrouchedHeadHeight > MovementCore.CrouchHeight);
        }

        [Theory]
        [InlineData(100, 1f)]
        [InlineData(50, 0.5f)]
        [InlineData(0, 0f)]
        [InlineData(180, 1f)]
        public void Health_IsAShareOfOneHundred(byte health, float share)
            => Assert.Equal(share, NameplateRules.Health01(health), 3);
    }
}
