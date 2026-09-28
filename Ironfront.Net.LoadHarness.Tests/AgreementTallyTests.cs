using Ironfront.Net.LoadHarness;
using Xunit;

namespace Ironfront.Net.LoadHarness.Tests
{
    /// <summary>
    /// X-35 and X-40 — the two questions lane A's agreement number could not tell apart.
    /// </summary>
    /// <remarks>
    /// X-35: divergence versus staleness. Two clients differing at the same capture tick have
    /// diverged only if both entries were last UPDATED at the same server tick; otherwise one
    /// simply holds a newer copy of a world that is still moving, which is interest management
    /// working. X-40: within the divergences, a one-unit step on one axis is the quantizer's
    /// own edge and a wholly different value is not, and the old report recorded only the first
    /// difference of either kind so the mix was unknown.
    /// </remarks>
    public sealed class AgreementTallyTests
    {
        [Fact]
        public void EqualValuesAreNotCountedAtAllHoweverOldTheyAre()
        {
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 1580,
                                              26150, 688, 23377, 1200);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(1, block.EntitiesCompared);
            Assert.Equal(0, block.Divergences);
            Assert.Equal(0, block.StaleComparisons);
        }

        [Fact]
        public void DifferentUpdateTicksAreStalenessNotDivergence()
        {
            // Vehicle 4's settling sequence, which is the row X-35 was filed on: over the run
            // clients 0, 2 and 5 record its Y descending 152 -> 689 -> 688 -> ... -> 668 while
            // the others are elsewhere in that sequence. The first difference the old counter
            // reported was exactly this, and it called it a divergence.
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 1580,
                                              26150, 689, 23377, 1502);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(0, block.Divergences);
            Assert.Equal(1, block.StaleComparisons);
            Assert.Equal(0, block.SameTickComparisons);
            Assert.Contains("updated at 1580", block.FirstStale);
            Assert.Contains("updated at 1502", block.FirstStale);
        }

        [Fact]
        public void TheSameTwoValuesAtTheSameUpdateTickAreADivergence()
        {
            // The mutation R3.2 owes: force both entries onto one update tick, change nothing
            // else, and the count must move out of staleness and into divergence. If it does
            // not, the split is decoration.
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 1580,
                                              26150, 689, 23377, 1580);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(1, block.Divergences);
            Assert.Equal(0, block.StaleComparisons);
            Assert.Equal(1, block.SameTickComparisons);
        }

        // ------------------------------------------------------------------ shape, X-40

        [Fact]
        public void AOneUnitStepOnOneAxisIsTheQuantizerEdgeAndIsCountedSeparately()
        {
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 1580,
                                              26150, 689, 23377, 1580);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(1, block.DivergencesOneUnitOneAxis);
            Assert.Equal(0, block.DivergencesSubstantive);
            Assert.Null(block.FirstSubstantiveDivergence);
        }

        [Fact]
        public void AOneUnitStepOnTwoAxesIsNotTheQuantizerEdge()
        {
            // Two axes each landing on the far side of a rounding boundary at the same tick is
            // not what "the quantizer edge" describes, and treating it as benign is how a real
            // population gets absorbed into the one that was dismissed.
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 1580,
                                              26151, 689, 23377, 1580);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(0, block.DivergencesOneUnitOneAxis);
            Assert.Equal(1, block.DivergencesSubstantive);
        }

        [Fact]
        public void ATwoUnitStepOnOneAxisIsNotTheQuantizerEdge()
        {
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 1580,
                                              26150, 690, 23377, 1580);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(0, block.DivergencesOneUnitOneAxis);
            Assert.Equal(1, block.DivergencesSubstantive);
        }

        [Fact]
        public void AWhollyDifferentPositionIsSubstantive()
        {
            var tally = new AgreementTally();

            tally.Classify(1589, "actor", 41, 3, 25776, 692, 23259, 1580,
                                              18335, 974, 21439, 1580);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(1, block.DivergencesSubstantive);
            Assert.Equal(0, block.DivergencesOneUnitOneAxis);
            Assert.Contains("actor 41", block.FirstSubstantiveDivergence);
        }

        // ------------------------------------------------------------------ the unknown case

        [Fact]
        public void AMissingUpdateTickIsReportedRatherThanGuessedAt()
        {
            // Update tick 0 means no position has ever arrived for an entry that nonetheless
            // exists in a decoded snapshot, which the provenance tracking should make
            // unreachable. Counting it as staleness would hide a bug in the very mechanism
            // this split depends on, so it gets its own line.
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 0,
                                              26150, 689, 23377, 1580);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(1, block.UnclassifiedComparisons);
            Assert.Equal(0, block.Divergences);
            Assert.Equal(0, block.StaleComparisons);
            Assert.Equal(0, block.SameTickComparisons);
        }

        [Fact]
        public void TwoUnknownTicksThatMatchAreStillNotCountedAsSameTick()
        {
            // Both zero compares equal, and a naive same-tick test would call two entries that
            // have never been updated the strongest possible evidence of agreement.
            var tally = new AgreementTally();

            tally.Classify(1589, "vehicle", 4, 6, 26150, 688, 23377, 0,
                                              26150, 689, 23377, 0);

            HarnessReport.AgreementBlock block = tally.ToBlock();
            Assert.Equal(0, block.SameTickComparisons);
            Assert.Equal(1, block.UnclassifiedComparisons);
        }
    }
}
