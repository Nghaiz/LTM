using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Playtest 2026-10-03, item 3f: routine A* failures filled the server log with error lines,
    /// up to three per failed search. They are counted by reason and reported once a minute.
    /// </summary>
    public sealed class PathFailureSummaryTests
    {
        private const string EndPoint = "Couldn't find a close node to the end point";
        private const string StartPoint = "Couldn't find a close node to the start point";

        [Test]
        public void FailuresInAMinuteBecomeOneLineByReason()
        {
            var summary = new PathFailureSummary();
            Assert.IsNull(summary.TakeDueSummary(10f), "the first call only opens the window");

            summary.Record(EndPoint);
            summary.Record(EndPoint);
            summary.Record("Error: " + StartPoint + ".\nmore detail");

            Assert.IsNull(summary.TakeDueSummary(65f), "reported before the minute was up");

            string line = summary.TakeDueSummary(71f);
            StringAssert.Contains("3 bot path search(es) failed in the last 61 s", line);
            StringAssert.Contains("2 x \"" + EndPoint + "\"", line);
            StringAssert.Contains("1 x \"" + StartPoint + "\"", line);

            Assert.IsNull(summary.TakeDueSummary(500f), "the same failures were reported twice");
        }

        /// <remarks>
        /// A* calls Record on its worker thread. The first version read Time.realtimeSinceStartup
        /// there, which throws off the main thread and ended the pathfinding thread for good.
        /// </remarks>
        [Test]
        public void FailuresRecordedFromManyThreadsAreAllCounted()
        {
            var summary = new PathFailureSummary();
            summary.TakeDueSummary(0f);

            System.Threading.Tasks.Parallel.For(0, 1000, _ => summary.Record(EndPoint));

            StringAssert.Contains("1000 bot path search(es)", summary.TakeDueSummary(60f));
        }

        [Test]
        public void AQuietMinuteReportsNothing()
        {
            Assert.IsNull(new PathFailureSummary().TakeDueSummary(1000f));
        }

        [Test]
        public void ACancelledSearchIsNotAFailure()
        {
            Assert.IsTrue(PathFailureSummary.IsCancellation("Canceled path because a new one was requested"));
            Assert.IsFalse(PathFailureSummary.IsCancellation(EndPoint));
        }
    }
}
