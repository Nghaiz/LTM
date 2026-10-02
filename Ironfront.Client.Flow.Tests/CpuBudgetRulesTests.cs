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
    }
}
