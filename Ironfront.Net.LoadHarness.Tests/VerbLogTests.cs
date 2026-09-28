using System.Collections.Generic;
using Ironfront.Net.LoadHarness;
using Xunit;

namespace Ironfront.Net.LoadHarness.Tests
{
    /// <summary>
    /// The record check 11 is graded from. Ledger <b>X-34</b>.
    /// </summary>
    /// <remarks>
    /// B-11 sat at PARTIAL for two months on a run that counted 6,078 tick records and could
    /// not say whether any of them held a seat, a shot or a corpse. These tests pin the two
    /// properties that make this log a different kind of evidence: it names the verb, and it
    /// names the one that is still missing.
    /// </remarks>
    public class VerbLogTests
    {
        [Fact]
        public void AFreshLogIsMissingAllFour()
        {
            var log = new VerbLog();

            Assert.False(log.AllFour);
            Assert.Equal(
                new[] { HarnessVerb.Drive, HarnessVerb.Damage,
                        HarnessVerb.Burn, HarnessVerb.Death },
                log.Missing);
        }

        [Fact]
        public void TheFirstSightingKeepsTheStampAndLaterOnesOnlyCount()
        {
            var log = new VerbLog();

            log.Record(HarnessVerb.Damage, clientIndex: 0, atDecodedTick: 100, atMs: 10.0, "first");
            log.Record(HarnessVerb.Damage, clientIndex: 0, atDecodedTick: 200, atMs: 20.0, "second");

            VerbLog.Entry entry = log.First[HarnessVerb.Damage];

            Assert.Equal(100u, entry.AtDecodedTick);
            Assert.Equal("first", entry.Evidence);

            // The count is what separates one freak observation from a verb the run lived in.
            Assert.Equal(2, entry.Count);
        }

        [Fact]
        public void MissingNamesExactlyWhatDidNotFire()
        {
            var log = new VerbLog();
            log.Record(HarnessVerb.Drive, clientIndex: 0, 1, 1.0, "e");
            log.Record(HarnessVerb.Damage, clientIndex: 0, 2, 2.0, "e");
            log.Record(HarnessVerb.Death, clientIndex: 0, 3, 3.0, "e");

            // Acceptance criterion 1 grades on all four verbs OR names the one still missing,
            // so the log answers that question rather than leaving a caller to diff two lists.
            Assert.False(log.AllFour);
            Assert.Equal(new[] { HarnessVerb.Burn }, log.Missing);
        }

        [Fact]
        public void AllFourIsTrueOnlyWhenEveryVerbHasFired()
        {
            var log = new VerbLog();
            foreach (HarnessVerb verb in
                     new[] { HarnessVerb.Drive, HarnessVerb.Damage,
                             HarnessVerb.Burn, HarnessVerb.Death })
            {
                log.Record(verb, clientIndex: 0, 1, 1.0, "e");
            }

            Assert.True(log.AllFour);
            Assert.Empty(log.Missing);
        }

        [Fact]
        public void MergingKeepsTheEarliestSightingAndSumsTheCounts()
        {
            var mine = new VerbLog();
            var theirs = new VerbLog();

            mine.Record(HarnessVerb.Burn, clientIndex: 0, atDecodedTick: 500, atMs: 50.0, "mine");
            theirs.Record(HarnessVerb.Burn, clientIndex: 0, atDecodedTick: 200, atMs: 20.0, "theirs");
            theirs.Record(HarnessVerb.Burn, clientIndex: 0, atDecodedTick: 900, atMs: 90.0, "theirs again");

            mine.MergeFrom(theirs);

            VerbLog.Entry entry = mine.First[HarnessVerb.Burn];

            // Interest management sends different clients different entities, so the client
            // that first saw a burn is whichever one held that vehicle in its set. Reading one
            // client's log would grade check 11 on that accident.
            Assert.Equal(200u, entry.AtDecodedTick);
            Assert.Equal("theirs", entry.Evidence);
            Assert.Equal(3, entry.Count);
        }

        [Fact]
        public void MergingCarriesAVerbTheReceiverNeverSaw()
        {
            var mine = new VerbLog();
            var theirs = new VerbLog();
            theirs.Record(HarnessVerb.Death, clientIndex: 0, 42, 4.2, "S_DEATH victim=43");

            mine.MergeFrom(theirs);

            Assert.True(mine.First.ContainsKey(HarnessVerb.Death));
            Assert.Equal(42u, mine.First[HarnessVerb.Death].AtDecodedTick);
        }

        [Fact]
        public void TheObserverTravelsWithTheSighting()
        {
            var mine = new VerbLog();
            var theirs = new VerbLog();

            mine.Record(HarnessVerb.Drive, clientIndex: 2, atDecodedTick: 900, atMs: 90.0, "mine");
            theirs.Record(HarnessVerb.Drive, clientIndex: 6, atDecodedTick: 300, atMs: 30.0, "theirs");

            mine.MergeFrom(theirs);

            // A sighting that cannot name its observer cannot be correlated with anything, and
            // the merge is where the name would otherwise be lost: the run-level row keeps the
            // EARLIEST entry, so it has to keep that entry's client rather than the receiver's.
            Assert.Equal(6, mine.First[HarnessVerb.Drive].ObservedByClient);
        }

        [Fact]
        public void ATieOnTheTickIsBrokenByTheHarnessClock()
        {
            var mine = new VerbLog();
            var theirs = new VerbLog();

            mine.Record(HarnessVerb.Drive, clientIndex: 0, atDecodedTick: 300, atMs: 90.0, "mine");
            theirs.Record(HarnessVerb.Drive, clientIndex: 0, atDecodedTick: 300, atMs: 30.0, "theirs");

            mine.MergeFrom(theirs);

            // Two clients decode the same tick at different wall-clock moments. Tick alone
            // would leave an arbitrary winner, and a run-level line that named a different
            // client on each replay of one seed is not the reproducibility this harness is for.
            Assert.Equal("theirs", mine.First[HarnessVerb.Drive].Evidence);
        }
    }
}
