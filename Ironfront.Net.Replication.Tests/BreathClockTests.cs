using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The breath a body has in water (owner ruling 2026-09-29): it drains in water, surface or
    /// not, refills on land, and hurts only once it is gone.
    /// </summary>
    public sealed class BreathClockTests
    {
        [Fact]
        public void ABodyStartsWithAFullBreath()
        {
            var breath = new BreathClock();
            Assert.Equal(BreathClock.CapacitySeconds, breath.Remaining);
            Assert.Equal(1f, breath.Fraction);
            Assert.False(breath.IsEmpty);
        }

        [Fact]
        public void TheBreathDrainsInWaterAndCostsNothingWhileAnyIsLeft()
        {
            var breath = new BreathClock();
            Assert.Equal(0f, breath.Tick(inWater: true, deltaSeconds: 10f));
            Assert.Equal(BreathClock.CapacitySeconds - 10f, breath.Remaining, 3);

            Assert.Equal(0f, breath.Tick(inWater: true, deltaSeconds: BreathClock.CapacitySeconds - 10f));
            Assert.True(breath.IsEmpty, "a body that used every second of its breath still has some");
        }

        [Fact]
        public void OnceTheBreathIsGoneEverySecondInWaterHurts()
        {
            var breath = new BreathClock();
            breath.Tick(inWater: true, deltaSeconds: BreathClock.CapacitySeconds);

            Assert.Equal(BreathClock.DamagePerSecond, breath.Tick(inWater: true, deltaSeconds: 1f), 3);
            Assert.Equal(BreathClock.DamagePerSecond * 2.5f, breath.Tick(inWater: true, deltaSeconds: 2.5f), 3);
        }

        [Fact]
        public void OnlyThePartOfAStepPastTheBreathHurts()
        {
            var breath = new BreathClock();
            breath.Tick(inWater: true, deltaSeconds: BreathClock.CapacitySeconds - 0.5f);

            Assert.Equal(BreathClock.DamagePerSecond * 0.5f, breath.Tick(inWater: true, deltaSeconds: 1f), 3);
        }

        [Fact]
        public void LandFillsTheBreathAgainAndNeverPastFull()
        {
            var breath = new BreathClock();
            breath.Tick(inWater: true, deltaSeconds: BreathClock.CapacitySeconds);

            Assert.Equal(0f, breath.Tick(inWater: false, deltaSeconds: BreathClock.RefillSeconds * 0.5f));
            Assert.Equal(0.5f, breath.Fraction, 3);

            breath.Tick(inWater: false, deltaSeconds: BreathClock.RefillSeconds * 10f);
            Assert.Equal(1f, breath.Fraction, 3);
        }

        [Fact]
        public void TheBreathIsLongEnoughToSwimAHundredMetresOfWaterAndNotAThousand()
        {
            // The design rule, not a restatement of the constants: land within a reasonable
            // distance is reachable at swimming speed, and camping in open water is not survivable.
            float reach = Movement.MovementCore.SwimSpeed
                          * (BreathClock.CapacitySeconds + 100f / BreathClock.DamagePerSecond);
            Assert.InRange(reach, 90f, 150f);
        }

        [Fact]
        public void ARespawnedBodyBreathesFully()
        {
            var breath = new BreathClock();
            breath.Tick(inWater: true, deltaSeconds: BreathClock.CapacitySeconds + 3f);
            breath.Reset();
            Assert.Equal(1f, breath.Fraction);
        }
    }
}
