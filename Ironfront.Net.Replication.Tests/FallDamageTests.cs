using System;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A landing hurts by the energy it carries: nothing from a drop a soldier jumps down unhurt,
    /// death from about four storeys (owner request 2026-10-06, a bail-out from a high helicopter).
    /// </summary>
    public sealed class FallDamageTests
    {
        private static float ImpactFromRealDrop(float metres) => (float)Math.Sqrt(2.0 * FallDamage.RealGravity * metres);

        [Fact]
        public void AJumpLandsUnhurt()
        {
            Assert.Equal(0f, FallDamage.ForImpact(MovementCore.JumpSpeed));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1.5f)]
        [InlineData(3f)]
        public void ADropASoldierJumpsDownFromCostsNothing(float metres)
        {
            Assert.Equal(0f, FallDamage.ForImpact(ImpactFromRealDrop(metres)));
        }

        [Fact]
        public void FourStoreysKillAFullHealthSoldier()
        {
            Assert.True(FallDamage.ForImpact(ImpactFromRealDrop(FallDamage.LethalDropMetres)) >= FallDamage.FullHealth - 0.01f);
            Assert.True(FallDamage.ForImpact(ImpactFromRealDrop(30f)) > FallDamage.FullHealth);
        }

        [Fact]
        public void DamageGrowsWithTheHeightFallenPastTheSafeDrop()
        {
            // Energy above the safe landing, so equal steps of height cost equal health:
            // 6 m is a quarter of the way from 3 m to 15 m, 9 m half.
            Assert.Equal(25f, FallDamage.ForImpact(ImpactFromRealDrop(6f)), 2);
            Assert.Equal(50f, FallDamage.ForImpact(ImpactFromRealDrop(9f)), 2);
        }

        [Fact]
        public void TheThresholdsAreRealDropsUnderRealGravity()
        {
            Assert.Equal(7.67f, FallDamage.SafeImpactSpeed, 2);
            Assert.Equal(17.16f, FallDamage.LethalImpactSpeed, 2);
        }

        [Fact]
        public void AnUpwardOrUnknownSpeedCostsNothing()
        {
            Assert.Equal(0f, FallDamage.ForImpact(-20f));
            Assert.Equal(0f, FallDamage.ForImpact(float.NaN));
        }

        [Fact]
        public void TheGamesOwnGravityMakesAFortyMetreBailOutFatal()
        {
            // MovementCore falls at 1.2 g: a player who leaves a helicopter 40 m up lands at
            // sqrt(2 * 11.77 * 40) = 30.7 m/s.
            float landing = (float)Math.Sqrt(2.0 * -MovementCore.Gravity * 40.0);
            Assert.True(FallDamage.ForImpact(landing) > FallDamage.FullHealth);
        }
    }
}
