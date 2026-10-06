using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The closed-form flight every peer computes: quadratic drag along the bore, gravity down,
    /// and the barrel tilted to the sights' zero. Checked against a numerical integration and
    /// against the cartridges' published numbers.
    /// </summary>
    public sealed class RoundBallisticsTests
    {
        private static RoundBallistics Round(byte id) => WeaponCatalog.For(id).Round;

        [Fact]
        public void TheClosedFormMatchesANumericalIntegrationOfTheDrag()
        {
            RoundBallistics rifle = Round(WeaponIds.RK44);
            double v = rifle.MuzzleVelocity, s = 0, dt = 1e-5;
            for (double t = 0; t < 1.0; t += dt)
            {
                s += v * dt;
                v -= rifle.DragPerMetre * v * v * dt;
            }

            Assert.Equal((float)s, rifle.AlongBore(1f), 0);
            Assert.Equal((float)v, rifle.SpeedAt(1f), 0);
        }

        [Fact]
        public void TimeToTravelIsTheInverseOfTheDistanceFlown()
        {
            RoundBallistics sniper = Round(WeaponIds.SL_DEFENDER);

            Assert.Equal(0.7f, sniper.TimeToTravel(sniper.AlongBore(0.7f)), 4);
        }

        [Theory]
        [InlineData(WeaponIds.RK44, 274f, 482f)]          // 7.62x39 M43
        [InlineData(WeaponIds.SL_DEFENDER, 274f, 651f)]   // 7.62x51 M118LR
        [InlineData(WeaponIds.SIND7, 91.4f, 305f)]        // 9x19 124 gr
        [InlineData(WeaponIds.EAGLE_76, 45.7f, 309f)]     // 00 buckshot
        public void EachRoundKeepsTheSpeedItsCartridgeIsPublishedAt(byte id, float metres, float speed)
        {
            RoundBallistics round = Round(id);

            Assert.InRange(round.SpeedAt(round.TimeToTravel(metres)), speed * 0.97f, speed * 1.03f);
        }

        [Fact]
        public void TheRoundCrossesTheSightLineAtTheZero()
        {
            RoundBallistics rifle = Round(WeaponIds.RK44);

            Assert.Equal(0f, rifle.DropBelowSight(rifle.ZeroMetres), 3);
            Assert.True(rifle.DropBelowSight(50f) < 0f, "the round should still be rising through the sight line");
            Assert.True(rifle.DropBelowSight(200f) > 0f);
        }

        [Theory]
        [InlineData(WeaponIds.SL_DEFENDER, 300f, 0.5f, 0.7f)]   // .308 175 gr, 100 m zero
        [InlineData(WeaponIds.RK44, 300f, 0.85f, 1.15f)]        // 7.62x39, 100 m zero
        [InlineData(WeaponIds.SIND7, 100f, 0.25f, 0.45f)]       // 9 mm, 25 m zero
        public void TheDropAtRangeIsTheCartridgesOwn(byte id, float metres, float least, float most)
        {
            Assert.InRange(Round(id).DropBelowSight(metres), least, most);
        }

        [Fact]
        public void TheLaunchTiltsUpByTheZeroElevationAndNothingElse()
        {
            RoundBallistics sniper = Round(WeaponIds.SL_DEFENDER);
            var level = new Vec3(0f, 0f, 1f);

            Vec3 launch = sniper.LaunchDirection(in level);

            Assert.Equal(1f, launch.Magnitude, 5);
            Assert.Equal(0f, launch.X, 6);
            Assert.Equal(Math.Tan(sniper.ZeroElevation), launch.Y / launch.Z, 5);

            var up = new Vec3(0f, 1f, 0f);
            Vec3 vertical = sniper.LaunchDirection(in up);
            Assert.Equal(1f, vertical.Y, 5);
        }

        [Fact]
        public void ThePositionFollowsTheBoreThenFallsUnderGravity()
        {
            RoundBallistics rifle = Round(WeaponIds.RK44);
            var launch = new Vec3(0f, 0f, 1f);

            Vec3 at = rifle.PositionAt(Vec3.Zero, in launch, 0.5f);

            Assert.Equal(rifle.AlongBore(0.5f), at.Z, 3);
            Assert.Equal(-0.5f * RoundBallistics.Gravity * 0.25f, at.Y, 3);
        }

        [Fact]
        public void NoMuzzleVelocityIsNoFlight()
        {
            RoundBallistics none = default;
            var aim = new Vec3(0f, 0f, 1f);

            Assert.False(none.IsBallistic);
            Assert.Equal(0f, none.ZeroElevation);
            Assert.Equal(aim, none.LaunchDirection(in aim));
        }
    }
}
