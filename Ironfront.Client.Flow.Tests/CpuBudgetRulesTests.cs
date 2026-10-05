using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The client takes a fair share of the machine (owner report 2026-10-03: the game took so much
    /// CPU the machine lagged; the title screen ran at 819 fps with v-sync off).
    /// </summary>
    public sealed class CpuBudgetRulesTests
    {
        [Theory]
        [InlineData(31, 4)]   // a 32-thread Ryzen 9 7945HX: Unity's default pool, cut to the cap
        [InlineData(15, 4)]
        [InlineData(4, 4)]
        [InlineData(3, 3)]    // a 4-thread CPU keeps the 3 workers Unity gave it
        [InlineData(1, 1)]
        public void TheClientPoolIsCappedAndNeverGrown(int current, int expected)
        {
            Assert.Equal(expected, CpuBudgetRules.CappedJobWorkers(current, CpuBudgetRules.ClientJobWorkers));
        }

        [Theory]
        [InlineData(165.0, 165)]
        [InlineData(143.856, 144)]
        [InlineData(60.0, 60)]
        [InlineData(59.94, 60)]
        [InlineData(30.0, 60)]    // a slow or misreported display still gets 60
        [InlineData(0.0, 60)]     // no refresh rate reported
        [InlineData(double.NaN, 60)]
        public void WithoutVSyncTheFrameRateStopsAtTheDisplaysRefresh(double refreshHz, int expected)
        {
            Assert.Equal(expected, CpuBudgetRules.ForegroundFrameCap(refreshHz));
        }

        [Theory]
        [InlineData(0, 240.0, 240)]   // the default: the display's refresh, as before the setting
        [InlineData(60, 240.0, 60)]
        [InlineData(144, 240.0, 144)]
        [InlineData(240, 144.0, 240)] // a fixed limit is the player's, whatever the display
        [InlineData(-1, 240.0, -1)]   // unlimited is Unity's -1
        [InlineData(30, 240.0, 240)]  // not offered: the default, not a rate nobody chose
        public void ThePlayersLimitBecomesTheTargetFrameRate(int limit, double refreshHz, int expected)
        {
            Assert.Equal(expected, CpuBudgetRules.FrameCapFor(limit, refreshHz));
        }

        [Theory]
        [InlineData(0, -1, -1)]      // no situation: the player's own
        [InlineData(0, 144, 144)]
        [InlineData(60, -1, 60)]     // the menus under no limit
        [InlineData(60, 240, 60)]
        [InlineData(60, 30, 30)]     // never above a lower limit the player chose
        [InlineData(15, 144, 15)]    // loading
        public void ASituationCapsWithoutRaisingThePlayersLimit(int situationCap, int playerCap, int expected)
        {
            Assert.Equal(expected, CpuBudgetRules.CapUnder(situationCap, playerCap));
        }

        [Fact]
        public void EveryOfferedLimitHasARowAndAnythingElseFallsBackToTheDefault()
        {
            for (int i = 0; i < CpuBudgetRules.FrameRateLimits.Length; i++)
                Assert.Equal(i, CpuBudgetRules.FrameRateLimitIndex(CpuBudgetRules.FrameRateLimits[i]));
            Assert.Equal(0, CpuBudgetRules.FrameRateLimitIndex(30));
            Assert.Equal(new[] { 0, 60, 90, 120, 144, 240, -1 }, CpuBudgetRules.FrameRateLimits);
            Assert.Equal("MAX FPS: DISPLAY (240)", CpuBudgetRules.FrameRateLimitLabel(0, 240.0));
            Assert.Equal("MAX FPS: 60", CpuBudgetRules.FrameRateLimitLabel(60, 240.0));
            Assert.Equal("MAX FPS: UNLIMITED", CpuBudgetRules.FrameRateLimitLabel(-1, 240.0));
            Assert.True(CpuBudgetRules.MenuFrameCap <= 60, "the menus are capped at a rate a menu needs");
        }
    }
}
