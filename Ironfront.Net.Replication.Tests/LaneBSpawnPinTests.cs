using Ironfront.Net.Unity.Diagnostics;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// debt-closure phase 3D lane B — ledger <b>X-22</b>, second half: the spawn pin has to
    /// survive being asked before the scene has any spawn points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row this file exists for was closed once already, and the fix never took.</b>
    /// X-22 shipped <c>PinnedSpawnPointDirectory</c> plus five tests, all of which passed and
    /// none of which asked WHEN the pin is installed. It was installed from
    /// <c>LaneBHarness.OnSceneLoaded</c> and validated against a directory that reads
    /// <c>ActorManager.instance.spawnPoints</c> — an array filled by
    /// <c>ActorManager.StartGame()</c>, reached from <c>GameManager.OnLevelLoaded</c>, another
    /// subscriber to the same <c>sceneLoaded</c> event. So the harness asked first, read 0, and
    /// logged "outside the scene's 0 spawn point(s)" on a six-point map.
    /// </para>
    /// <para>
    /// Two runs then reported a pinned spawn and were not pinned: <c>x20-occlusion-01</c>
    /// (actors on points 2, 2, 1) and <c>x25-torso-aim-01</c> (5, 2, 0 — the pair 483 m apart).
    /// The X-20 report's "spawn pinned to slot 0" is that line, and it is wrong.
    /// </para>
    /// <para>
    /// <b>What is graded here is the distinction the old code did not have</b>: "not knowable
    /// yet" versus "knowably wrong". Everything else about the pin is
    /// <c>SpawnPointSelectionTests</c>'s, in EditMode, where the directory lives.
    /// </para>
    /// </remarks>
    public sealed class LaneBSpawnPinTests
    {
        // ------------------------------------------------------------------- not knowable yet

        /// <summary>
        /// An empty directory before the deadline is a RETRY and says nothing. This is the
        /// whole bug: the old code answered Failed here, on every run, on a six-point map.
        /// </summary>
        [Fact]
        public void AnEmptyDirectoryBeforeTheDeadlineIsRetriedSilently()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                "0", directoryInstalled: true, directoryCount: 0, final: false,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Retry, outcome);
            Assert.Null(message);
        }

        /// <summary>A directory that does not exist yet is equally not an answer yet.</summary>
        [Fact]
        public void NoDirectoryBeforeTheDeadlineIsRetriedSilently()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                "3", directoryInstalled: false, directoryCount: 0, final: false,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Retry, outcome);
            Assert.Null(message);
        }

        /// <summary>The same emptiness AT the deadline is a real failure, and is reported.</summary>
        [Fact]
        public void AnEmptyDirectoryAtTheDeadlineFailsAndNamesTheRow()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                "0", directoryInstalled: true, directoryCount: 0, final: true,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Failed, outcome);
            Assert.Contains("0 points at the ready line", message);
            Assert.Contains(LaneBSpawnPin.CoinFlipTail, message);
        }

        /// <summary>And so is a directory that never arrived.</summary>
        [Fact]
        public void NoDirectoryAtTheDeadlineFailsAndNamesTheRow()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                "3", directoryInstalled: false, directoryCount: 0, final: true,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Failed, outcome);
            Assert.Contains("no ISpawnPointDirectory", message);
        }

        // ----------------------------------------------------------------- knowably wrong now

        /// <summary>
        /// An index outside a NON-EMPTY directory is answered at once, before the deadline.
        /// </summary>
        /// <remarks>
        /// Retrying it would hold a typo back until the ready line and then report it as though
        /// it were the timing failure above — the same quiet the retry was added to remove.
        /// </remarks>
        [Fact]
        public void AnIndexOutsideANonEmptyDirectoryFailsImmediately()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                "9", directoryInstalled: true, directoryCount: 6, final: false,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Failed, outcome);
            Assert.Contains("outside the scene's 6 spawn point(s)", message);
        }

        /// <summary>A value that is not a number can never become one.</summary>
        /// <remarks>
        /// <c>"1,2"</c> USED to be listed here as nonsense and is now the per-team form
        /// (ledger X-63). Inverted rather than deleted: the case it pinned is still exercised,
        /// by <see cref="APerTeamPairPinsOneSlotEachWay"/> below, asserting the opposite outcome.
        /// Deleting it would have removed the only record that this input was ever considered.
        /// </remarks>
        [Theory]
        [InlineData("zero")]
        [InlineData("0.5")]
        [InlineData("1;2")]
        [InlineData("1,two")]
        public void AValueThatIsNotAnIntegerFailsImmediately(string raw)
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                raw, directoryInstalled: false, directoryCount: 0, final: false,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Failed, outcome);
            Assert.Contains(raw, message);
        }

        /// <summary>
        /// X-63: one slot per team, because every Dustbowl spawn point is team-owned and a
        /// single pinned index therefore starves one side and voids the run.
        /// </summary>
        [Fact]
        public void APerTeamPairPinsOneSlotEachWay()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.EvaluatePerTeam(
                "3,5", directoryInstalled: true, directoryCount: 6, final: true,
                out int[] slots, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Pinned, outcome);
            Assert.Null(message);
            Assert.Equal(new[] { 3, 5 }, slots);
        }

        /// <summary>
        /// The single-value form still means what it always meant: the same slot for every team.
        /// Correct on a map with neutral spawn points, and refused at construction by
        /// <c>PinnedSpawnPointDirectory</c> on one where it is not.
        /// </summary>
        [Fact]
        public void ASingleValueStillPinsBothTeamsToOneSlot()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.EvaluatePerTeam(
                "4", directoryInstalled: true, directoryCount: 6, final: true,
                out int[] slots, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Pinned, outcome);
            Assert.Null(message);
            Assert.Equal(new[] { 4, 4 }, slots);
        }

        /// <summary>
        /// A per-team pair is still bounds-checked on every element, not just the first.
        /// </summary>
        [Fact]
        public void ASecondTeamsSlotIsBoundsCheckedToo()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.EvaluatePerTeam(
                "1,99", directoryInstalled: true, directoryCount: 6, final: true,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Failed, outcome);
            Assert.Contains("outside the scene's 6 spawn point(s)", message);
        }

        /// <summary>Negative asks for a slot no directory can ever hold.</summary>
        [Fact]
        public void ANegativeIndexFailsImmediately()
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                "-2", directoryInstalled: true, directoryCount: 6, final: false,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Failed, outcome);
            Assert.Contains("negative", message);
        }

        // -------------------------------------------------------------------------- the happy

        /// <summary>A valid index against a populated directory pins, at either phase.</summary>
        [Theory]
        [InlineData("0", 0, false)]
        [InlineData("5", 5, false)]
        [InlineData("2", 2, true)]
        public void AValidIndexAgainstAPopulatedDirectoryPins(string raw, int expected, bool final)
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                raw, directoryInstalled: true, directoryCount: 6, final: final,
                out int index, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.Pinned, outcome);
            Assert.Equal(expected, index);
            Assert.Null(message);
        }

        /// <summary>
        /// No variable set means selection is left alone — the default, and the shape every
        /// non-lane-B process runs in.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnAbsentRequestLeavesSelectionAlone(string? raw)
        {
            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                raw, directoryInstalled: true, directoryCount: 6, final: true,
                out _, out string message);

            Assert.Equal(LaneBSpawnPin.Outcome.NotRequested, outcome);
            Assert.Null(message);
        }
    }
}
