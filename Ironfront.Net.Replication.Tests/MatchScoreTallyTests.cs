using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Checklist A13's detector: a match that ends after N deaths reports N rows with the right
    /// killer attribution. Phase P6 task 3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Attribution is asserted by identity, not by row count.</b> A count assertion is
    /// satisfied by any N rows — the tally could credit every kill to the wrong player and still
    /// produce exactly N of them. <see cref="AMatchReportsEachKillAgainstItsOwnKiller"/> names
    /// which actor is expected to hold which number, which is what makes the mutation below
    /// fail; see <c>pinned-baseline-test-companion.md</c> for why identity beats cardinality.
    /// </para>
    /// <para>
    /// <b>Observed RED before it was believed.</b> Crediting the kill to the victim instead of
    /// the killer in <c>MatchScoreTally.RecordDeath</c> leaves every row-count assertion green
    /// and fails only the identity one.
    /// </para>
    /// </remarks>
    public class MatchScoreTallyTests
    {
        private const ushort Alice = 3;
        private const ushort Bob   = 4;
        private const ushort Carol = 5;

        [Fact]
        public void AMatchReportsEachKillAgainstItsOwnKiller()
        {
            var tally = new MatchScoreTally();

            // Alice kills Bob twice; Bob kills Carol once; Carol kills nobody and dies once.
            tally.RecordDeath(victimActorId: Bob,   killerActorId: Alice);
            tally.RecordDeath(victimActorId: Bob,   killerActorId: Alice);
            tally.RecordDeath(victimActorId: Carol, killerActorId: Bob);

            Assert.Equal(3, tally.DeathsRecorded);

            // BY NAME. Mutating RecordDeath to credit the victim would keep the three counts
            // above and every one of these would move.
            Assert.Equal(2, tally.KillsOf(Alice));
            Assert.Equal(0, tally.DeathsOf(Alice));

            Assert.Equal(1, tally.KillsOf(Bob));
            Assert.Equal(2, tally.DeathsOf(Bob));

            Assert.Equal(0, tally.KillsOf(Carol));
            Assert.Equal(1, tally.DeathsOf(Carol));
        }

        /// <summary>
        /// A death nobody caused is a death and not a kill — and it is counted as such rather
        /// than dropped, so a tally that missed one and a match full of falls look different.
        /// </summary>
        [Fact]
        public void AnEnvironmentDeathScoresNoKill()
        {
            var tally = new MatchScoreTally();

            tally.RecordDeath(Alice, DeathMessage.EnvironmentKiller);

            Assert.Equal(1, tally.DeathsOf(Alice));
            Assert.Equal(0, tally.KillsOf(Alice));
            Assert.Equal(1, tally.DeathsRecorded);
            Assert.Equal(1, tally.UnattributedDeaths);
            Assert.Equal(0, tally.OutOfRangeIds);
        }

        /// <summary>
        /// A scoreboard must not be climbable by dying. Killer equal to victim is what a fall,
        /// an own grenade or a rolled vehicle produces.
        /// </summary>
        [Fact]
        public void ASuicideScoresNoKill()
        {
            var tally = new MatchScoreTally();

            tally.RecordDeath(Alice, Alice);

            Assert.Equal(1, tally.DeathsOf(Alice));
            Assert.Equal(0, tally.KillsOf(Alice));
            Assert.Equal(1, tally.UnattributedDeaths);
        }

        /// <summary>
        /// An id outside the actor space is counted and dropped, never thrown — one bad damage
        /// path must not take the tick loop down for everybody else.
        /// </summary>
        [Fact]
        public void AnOutOfRangeIdIsCountedRatherThanThrown()
        {
            var tally = new MatchScoreTally();

            tally.RecordDeath((ushort)ProtocolConstants.MAX_ACTORS, Alice);
            Assert.Equal(1, tally.OutOfRangeIds);
            Assert.Equal(0, tally.DeathsRecorded);
            Assert.Equal(0, tally.KillsOf(Alice));

            // A victim in range with a killer out of it still records the death: somebody died,
            // and refusing to count that because the attribution was junk would lose the half of
            // the fact that is not in doubt.
            tally.RecordDeath(Bob, (ushort)ProtocolConstants.MAX_ACTORS);
            Assert.Equal(1, tally.DeathsOf(Bob));
            Assert.Equal(2, tally.OutOfRangeIds);
        }

        /// <summary>
        /// A round that reports and then resets starts the next one at zero — the leak that only
        /// shows up on the second and third match of a server nobody is watching.
        /// </summary>
        [Fact]
        public void ClearEmptiesTheTallyForTheNextRound()
        {
            var tally = new MatchScoreTally();

            tally.RecordDeath(Bob, Alice);
            tally.Clear();

            Assert.Equal(0, tally.KillsOf(Alice));
            Assert.Equal(0, tally.DeathsOf(Bob));
            Assert.Equal(0, tally.DeathsRecorded);
            Assert.True(tally.IsUntouched(Alice));
            Assert.True(tally.IsUntouched(Bob));
        }

        /// <summary>
        /// Acceptance 2: a match with no kills reports an EMPTY list, not rows of zeroes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This asserts the predicate <c>ServerMasterReporter.CollectScores</c> filters on,
        /// which is the closest a CI test can get to that method — the reporter itself is a
        /// <c>MonoBehaviour</c> and unreachable from here. The filter is the whole of the
        /// behaviour: everything else in that method is a copy.
        /// </para>
        /// <para>
        /// The reasoning it protects is the one the pre-P6 comment recorded and this phase kept:
        /// the master stores what it is given, and rows of all-zero scores are indistinguishable
        /// from a match where nobody scored.
        /// </para>
        /// </remarks>
        [Fact]
        public void APlayerWhoNeitherKilledNorDiedIsOmittedRatherThanReportedAtZero()
        {
            var tally = new MatchScoreTally();

            tally.RecordDeath(Bob, Alice);

            var reported = new List<ushort>();
            foreach (ushort actorId in new[] { Alice, Bob, Carol })
                if (!tally.IsUntouched(actorId)) reported.Add(actorId);

            // Carol was in the match and did nothing. She is not a row.
            Assert.Equal(new[] { Alice, Bob }, reported);

            // And a match in which nobody scored produces no rows at all.
            tally.Clear();
            Assert.True(tally.IsUntouched(Alice));
            Assert.True(tally.IsUntouched(Bob));
            Assert.True(tally.IsUntouched(Carol));
        }
    }
}
