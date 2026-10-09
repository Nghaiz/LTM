using System;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The owner's fall-damage scale (2026-10-06): nothing from a 3 m free fall, instant death at
    /// full health from 20 m, in between by the impact's energy -- so just short of 20 m leaves
    /// 1 HP -- and a body that hits faster than its height alone, thrown by a force, pays for the
    /// extra speed.
    /// </summary>
    public sealed class FallDamageTests
    {
        private const float OneG = 9.81f;
        private static float PlayerGravity => -MovementCore.Gravity;

        private static float FreeFall(float metres, float gravity) => (float)Math.Sqrt(2.0 * gravity * metres);

        [Theory]
        [InlineData(0f)]
        [InlineData(1.5f)]
        [InlineData(3f)]
        public void AFallOfThreeMetresOrLessCostsNothing(float metres)
        {
            Assert.Equal(0f, FallDamage.ForDrop(metres));
            Assert.Equal(0f, FallDamage.ForImpact(FreeFall(metres, OneG), OneG), 3);
        }

        [Theory]
        [InlineData(20f)]
        [InlineData(25f)]
        [InlineData(60f)]
        public void AFallOfTwentyMetresOrMoreKillsAFullHealthSoldier(float metres)
        {
            Assert.True(FallDamage.ForDrop(metres) >= FallDamage.FullHealth - 0.001f);
            Assert.True(FallDamage.ForImpact(FreeFall(metres, OneG), OneG) >= FallDamage.FullHealth - 0.01f);
        }

        [Fact]
        public void JustShortOfTwentyMetresLeavesOneHealth()
        {
            float left = FallDamage.FullHealth - FallDamage.ForImpact(FreeFall(19.83f, PlayerGravity), PlayerGravity);

            Assert.InRange(left, 0.9f, 1.1f);
            Assert.True(FallDamage.ForDrop(19.99f) < FallDamage.FullHealth, "19.99 m killed outright");
        }

        [Theory]
        [InlineData(5f, 11.76f)]
        [InlineData(11.5f, 50f)]
        [InlineData(15f, 70.59f)]
        public void InBetweenEachMetreOfFreeFallCostsTheSame(float metres, float damage)
        {
            Assert.Equal(damage, FallDamage.ForDrop(metres), 1);
            Assert.Equal(damage, FallDamage.ForImpact(FreeFall(metres, OneG), OneG), 1);
        }

        [Fact]
        public void BeingThrownHurtsMoreThanFallingTheSameHeight()
        {
            // A 10 m free fall, and the same body thrown down 10 m by a blast that started it at
            // 10 m/s: the second lands at 17.2 m/s and pays for it.
            float fell = FallDamage.ForImpact(FreeFall(10f, OneG), OneG);
            float thrown = FallDamage.ForImpact(FallDamage.LandingSpeed(10f, 10f, OneG), OneG);

            Assert.Equal(41.18f, fell, 1);
            Assert.True(thrown > fell + 25f, $"thrown {thrown:F1} vs fallen {fell:F1}");
        }

        [Fact]
        public void HittingAWallAtTwentyMetresASecondIsATwentyMetreFall()
        {
            Assert.True(FallDamage.ForImpact(20f, OneG) >= FallDamage.FullHealth);
            Assert.Equal(FallDamage.LethalImpactSpeed(OneG), FreeFall(20f, OneG), 3);
        }

        [Fact]
        public void ANormalJumpOnFlatGroundCostsNothing()
        {
            Assert.Equal(0f, FallDamage.ForImpact(MovementCore.JumpSpeed, PlayerGravity));
        }

        [Fact]
        public void EachBodysOwnGravityMakesTwentyMetresLethal()
        {
            // A ragdoll falls at 1 g, a player at 1.2 g: a 20 m free fall kills both.
            Assert.Equal(FallDamage.FullHealth, FallDamage.ForImpact(FreeFall(20f, OneG), OneG), 2);
            Assert.Equal(FallDamage.FullHealth, FallDamage.ForImpact(FreeFall(20f, PlayerGravity), PlayerGravity), 2);
        }

        [Fact]
        public void AnUpwardOrUnknownSpeedCostsNothing()
        {
            Assert.Equal(0f, FallDamage.ForImpact(-20f, OneG));
            Assert.Equal(0f, FallDamage.ForImpact(float.NaN, OneG));
            Assert.Equal(0f, FallDamage.ForDrop(float.NaN));
        }

        [Fact]
        public void OnFlatGroundTheImpactIsTheFallItself()
        {
            Assert.Equal(12f, FallDamage.ImpactAlongNormal(12f, 6.5f, 0f, 0f, 1f, 0f), 4);
        }

        [Fact]
        public void RunningDownASlopeOnlyThePartIntoItHits()
        {
            // The 2026-10-07 playtest: a jump down Forest Lake's hills landed at 10-12 m/s and cost
            // 7-19 health. Running down a 30-degree slope at 6.5 m/s, the slope leans toward +X.
            float tilt = 30f * (float)Math.PI / 180f;
            float nx = (float)Math.Sin(tilt), ny = (float)Math.Cos(tilt);

            float impact = FallDamage.ImpactAlongNormal(12f, 6.5f, 0f, nx, ny, 0f);

            Assert.Equal(12f * ny - 6.5f * nx, impact, 3);
            Assert.True(FallDamage.ForImpact(12f, PlayerGravity) > 15f, "the old reading of the same landing");
            Assert.Equal(0f, FallDamage.ForImpact(impact, PlayerGravity));
        }

        [Fact]
        public void RunningIntoARisingSlopeHitsHarder()
        {
            float tilt = 30f * (float)Math.PI / 180f;
            float impact = FallDamage.ImpactAlongNormal(12f, -6.5f, 0f, (float)Math.Sin(tilt), (float)Math.Cos(tilt), 0f);

            Assert.True(impact > 12f * (float)Math.Cos(tilt));
        }

        [Fact]
        public void ADropOntoASlopeStillHitsAlmostAllOfItsFall()
        {
            // A fall from a helicopter with nothing across: only the slope's own tilt softens it.
            float tilt = 20f * (float)Math.PI / 180f;
            float lethal = FallDamage.LethalImpactSpeed(PlayerGravity);

            float impact = FallDamage.ImpactAlongNormal(lethal * 1.2f, 0f, 0f, (float)Math.Sin(tilt), (float)Math.Cos(tilt), 0f);

            Assert.True(FallDamage.ForImpact(impact, PlayerGravity) >= FallDamage.FullHealth);
        }

        [Fact]
        public void AWallOrAMissingNormalLeavesTheFallAsItWas()
        {
            Assert.Equal(12f, FallDamage.ImpactAlongNormal(12f, 6.5f, 0f, 1f, 0f, 0f));
            Assert.Equal(12f, FallDamage.ImpactAlongNormal(12f, 6.5f, 0f, 0f, 0f, 0f));
            Assert.Equal(0f, FallDamage.ImpactAlongNormal(0f, 6.5f, 0f, 0f, 1f, 0f));
        }
    }
}
