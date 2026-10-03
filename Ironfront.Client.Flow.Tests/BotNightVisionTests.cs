using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// When a bot wears its goggles at night (phase P32): the players' battery, spent on fights and
    /// on the last stretch to the objective.
    /// </summary>
    public sealed class BotNightVisionTests
    {
        [Fact]
        public void AFightPutsTheGogglesOn()
        {
            var goggles = new BotNightVision(45f);
            goggles.Tick(0.1f, inFight: true, nearObjective: false);
            Assert.True(goggles.IsOn);
        }

        [Fact]
        public void QuietTakesThemOffAfterAWhile()
        {
            var goggles = new BotNightVision(45f);
            goggles.Tick(0.1f, inFight: true, nearObjective: false);

            goggles.Tick(BotNightVision.QuietSecondsBeforeOff - 1f, inFight: false, nearObjective: false);
            Assert.True(goggles.IsOn, "a pause in a fight is not the end of it");

            goggles.Tick(1.5f, inFight: false, nearObjective: false);
            Assert.False(goggles.IsOn);
        }

        [Fact]
        public void TheApproachLeavesAReserveForTheFight()
        {
            var goggles = new BotNightVision(30f);
            goggles.Tick(0.1f, inFight: false, nearObjective: true);
            Assert.True(goggles.IsOn);

            // Walking in on the goggles until the reserve is reached: they come off.
            for (int i = 0; i < 400 && goggles.Battery.Fraction > BotNightVision.ApproachReserve; i++)
                goggles.Tick(0.1f, inFight: false, nearObjective: true);
            for (int i = 0; i < 70; i++)
                goggles.Tick(0.1f, inFight: false, nearObjective: true);
            Assert.False(goggles.IsOn);
            Assert.True(goggles.Battery.Fraction >= BotNightVision.ApproachReserve - 0.05f);

            // The fight it walks into still finds them charged.
            goggles.Tick(0.1f, inFight: true, nearObjective: true);
            Assert.True(goggles.IsOn);
        }

        [Fact]
        public void AnEmptyBatteryCannotBeWorn()
        {
            var goggles = new BotNightVision(10f);
            goggles.Tick(10.5f, inFight: true, nearObjective: false);
            Assert.False(goggles.IsOn);
            goggles.Tick(0.1f, inFight: true, nearObjective: false);
            Assert.False(goggles.IsOn, "below the switch-on share it stays off");
        }

        [Fact]
        public void ARefillIsAFreshBattery()
        {
            var goggles = new BotNightVision(20f);
            goggles.Tick(15f, inFight: true, nearObjective: false);
            goggles.Refill();
            Assert.False(goggles.IsOn);
            Assert.Equal(1f, goggles.Battery.Fraction);
        }
    }
}
