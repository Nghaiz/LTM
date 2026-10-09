using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Playtest 2026-10-03, item 3d: a client in the background logged a prediction-clock warning
    /// on most frames. A drop warns with focus and is summed into one line without it.
    /// </summary>
    public sealed class TickDropReportTests
    {
        [Test]
        public void ADropWithFocusIsAWarningAtOnce()
        {
            var report = new TickDropReport();

            string warning = report.Record(3, 140f, focused: true);

            StringAssert.Contains("dropped 3 tick(s) after a 140 ms frame", warning);
            Assert.IsNull(report.TakeBackgroundSummary(focused: true));
        }

        [Test]
        public void AFocusedDropSaysHowMuchOfTheFrameWasSpentBetweenFrames()
        {
            // Phase P35, finding 3: a focused long frame spent mostly outside the game loop. The
            // release log has to say so, or the next one is as unattributable as the first.
            string warning = new TickDropReport().Record(9, 412f, focused: true, outsideLoopMs: 380f);

            StringAssert.Contains("dropped 9 tick(s) after a 412 ms frame (380 ms of it between frames", warning);
        }

        [Test]
        public void DropsInTheBackgroundWaitForFocusAndAreSummedOnce()
        {
            var report = new TickDropReport();

            Assert.IsNull(report.Record(5, 250f, focused: false));
            Assert.IsNull(report.Record(12, 480f, focused: false));
            Assert.IsNull(report.TakeBackgroundSummary(focused: false), "summarised before focus came back");

            string summary = report.TakeBackgroundSummary(focused: true);
            StringAssert.Contains("dropped 17 tick(s) over 2 long frame(s) (longest 480 ms)", summary);
            Assert.IsNull(report.TakeBackgroundSummary(focused: true), "the summary repeated");
        }
    }
}
