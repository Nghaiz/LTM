using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Deep water kills, once, and only after the limit. X-90.
    /// </summary>
    /// <remarks>
    /// The rule's whole surface is a bool and a delta, which is why it lives on the engine-free
    /// side: an Editor is not needed to prove when it fires, and CI can prove it on every PR.
    /// What CI cannot prove is that the Unity side hands it the right bool -- that is
    /// <c>ActorGameplaySource.IsSubmerged</c> passing through <c>Actor.inWater</c>, the same
    /// field the shipped gameplay already reads.
    /// </remarks>
    public sealed class DrowningClockTests
    {
        private const float Limit = DrowningClock.DefaultSecondsUnderwater;

        [Fact]
        public void AHeadUnderWaterDrownsWhenTheLimitPasses()
        {
            var clock = new DrowningClock();

            // One tick short: alive.
            Assert.False(clock.Tick(submerged: true, Limit - 0.1f));
            Assert.False(clock.HasDrowned);

            Assert.True(clock.Tick(submerged: true, 0.2f));
            Assert.True(clock.HasDrowned);
        }

        /// <summary>
        /// The latch, and the reason it exists: without it a corpse under water emits a death
        /// every tick for as long as it stays down.
        /// </summary>
        [Fact]
        public void ItDrownsExactlyOncePerSubmersion()
        {
            var clock = new DrowningClock();

            Assert.True(clock.Tick(true, Limit));

            for (int i = 0; i < 100; i++)
            {
                Assert.False(clock.Tick(true, 1f));
            }
        }

        [Fact]
        public void SurfacingResetsRatherThanPauses()
        {
            var clock = new DrowningClock();

            clock.Tick(true, Limit - 0.5f);
            clock.Tick(submerged: false, 0.1f);

            Assert.Equal(0f, clock.SubmergedSeconds);

            // The seconds banked before surfacing must not carry into the next dip. Partial
            // credit would drown a player on a later, shorter dive for reasons invisible to them.
            Assert.False(clock.Tick(true, 1f));
            Assert.False(clock.HasDrowned);
        }

        [Fact]
        public void ResetRearmsTheClockSoARespawnCanDrownAgain()
        {
            var clock = new DrowningClock();
            Assert.True(clock.Tick(true, Limit));

            clock.Reset();

            Assert.False(clock.HasDrowned);
            Assert.True(clock.Tick(true, Limit));
        }

        /// <summary>
        /// A paused or rewound clock must not kill anybody.
        /// </summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(-100f)]
        public void ANonPositiveDeltaAdvancesNothing(float delta)
        {
            var clock = new DrowningClock();

            for (int i = 0; i < 1000; i++)
            {
                Assert.False(clock.Tick(true, delta));
            }

            Assert.Equal(0f, clock.SubmergedSeconds);
        }

        [Fact]
        public void DryLandNeverDrowns()
        {
            var clock = new DrowningClock();

            for (int i = 0; i < 1000; i++)
            {
                Assert.False(clock.Tick(submerged: false, 1f));
            }
        }

        /// <summary>
        /// A non-positive limit falls back to the default rather than killing on the first tick.
        /// </summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        public void ANonsenseLimitFallsBackToTheDefault(float limit)
        {
            var clock = new DrowningClock(limit);

            Assert.Equal(DrowningClock.DefaultSecondsUnderwater, clock.Limit);
            Assert.False(clock.Tick(true, 0.1f));
        }
    }
}
