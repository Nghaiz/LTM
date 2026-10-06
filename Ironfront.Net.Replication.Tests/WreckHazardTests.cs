using System;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A wreck's blast and a burning vehicle's fire hurt by distance, from real blast and burn
    /// data (owner ruling 2026-10-06: "explosions and fires must hurt what is around them, more the
    /// closer it stands").
    /// </summary>
    public sealed class WreckHazardTests
    {
        [Fact]
        public void TheOverpressureFollowsTheMillsFitToTnt()
        {
            // Z = 1 m/kg^(1/3): 1772 - 114 + 108 = 1766 kPa.
            Assert.Equal(1766f, WreckHazard.OverpressureKpa(1f, 1f), 0);
            Assert.Equal(247f, WreckHazard.OverpressureKpa(2f, 1f), 0);
        }

        [Fact]
        public void AChargeEightTimesBiggerReachesTwiceAsFar()
        {
            Assert.Equal(WreckHazard.OverpressureKpa(5f, 3f), WreckHazard.OverpressureKpa(10f, 24f), 2);
            Assert.Equal(2f * WreckHazard.BlastReach(3f), WreckHazard.BlastReach(24f), 2);
        }

        [Fact]
        public void BlastDamageRunsFromHarmlessToLethalOnTheLogOfThePressure()
        {
            Assert.Equal(0f, WreckHazard.BlastDamageAt(WreckHazard.HarmlessKpa));
            Assert.Equal(100f, WreckHazard.BlastDamageAt(WreckHazard.LethalKpa), 3);
            Assert.Equal(100f, WreckHazard.BlastDamageAt(5000f), 3);
            // Halfway on a log scale is the geometric mean.
            float midway = (float)Math.Sqrt(WreckHazard.HarmlessKpa * WreckHazard.LethalKpa);
            Assert.Equal(50f, WreckHazard.BlastDamageAt(midway), 2);
        }

        [Fact]
        public void AJeepsThreeKilogramBlastKillsWithinThreeMetresAndHurtsToTwentyTwo()
        {
            Assert.InRange(WreckHazard.LethalRadius(3f), 2.9f, 3.3f);
            Assert.InRange(WreckHazard.BlastReach(3f), 21f, 23f);
            Assert.Equal(100f, WreckHazard.BlastDamage(2.5f, 3f, shielded: false), 3);
            Assert.InRange(WreckHazard.BlastDamage(8f, 3f, shielded: false), 30f, 45f);
            Assert.Equal(0f, WreckHazard.BlastDamage(25f, 3f, shielded: false));
        }

        [Fact]
        public void DamageFallsOffWithDistanceAndAWallTakesMostOfIt()
        {
            float previous = float.MaxValue;
            for (float d = 2f; d <= 30f; d += 2f)
            {
                float damage = WreckHazard.BlastDamage(d, 10f, shielded: false);
                Assert.True(damage <= previous, $"damage rose at {d} m");
                previous = damage;
            }
            Assert.True(WreckHazard.BlastDamage(8f, 10f, shielded: true) < WreckHazard.BlastDamage(8f, 10f, shielded: false) * 0.75f);
        }

        [Fact]
        public void AFireKillsQuicklyUpCloseAndNotAtAll()
        {
            // A 1 MW fire: 80 kW/m^2 at 1 m kills in about seven seconds, 20 kW/m^2 at 2 m in
            // about forty, and past 5.6 m the heat is bearable.
            Assert.InRange(100f / WreckHazard.FireDamagePerSecond(1f, 1f), 6f, 8f);
            Assert.InRange(100f / WreckHazard.FireDamagePerSecond(2f, 1f), 38f, 50f);
            Assert.InRange(WreckHazard.FireReach(1f), 5.5f, 5.8f);
            Assert.Equal(0f, WreckHazard.FireDamagePerSecond(6f, 1f));
        }

        [Fact]
        public void NoChargeAndNoFireHurtNobody()
        {
            Assert.Equal(0f, WreckHazard.BlastDamage(1f, 0f, shielded: false));
            Assert.Equal(0f, WreckHazard.BlastReach(0f));
            Assert.Equal(0f, WreckHazard.FireDamagePerSecond(1f, 0f));
        }
    }
}
