using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The territory award and the time limit: playtest 2026-09-28, bug 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Island round of 2026-09-28 took 29 minutes to reach the 200-point margin, 702 to 903,
    /// with its capture points split three-two and swapping sides. Under the original rule a
    /// point is worth something only when somebody dies, so its lead grew about seven points a
    /// minute. Both rules are off in a bare <see cref="MatchRules"/>, as the original is, and the
    /// live server opts in through <c>MatchController</c> (5 s, 1200 s).
    /// </para>
    /// </remarks>
    public sealed class TerritoryAwardAndTimeLimitTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static readonly ActorPresence[] NoActors = Array.Empty<ActorPresence>();

        // ------------------------------------------------------------------ the shared rule

        [Theory]
        [InlineData(3, 2, 1)]
        [InlineData(4, 1, 3)]
        [InlineData(2, 3, 0)]
        [InlineData(2, 2, 0)]
        [InlineData(0, 0, 0)]
        public void ATeamEarnsThePointsItHoldsBeyondTheOthers(int flags, int otherFlags, int award)
        {
            Assert.Equal(award, ConquestScoreRule.TerritoryAward(flags, otherFlags));
        }

        [Fact]
        public void TheLeaderIsTheTeamAheadAndNobodyWhenLevel()
        {
            Assert.Equal(TeamId.Team0, ConquestScoreRule.Leader(120, 119));
            Assert.Equal(TeamId.Team1, ConquestScoreRule.Leader(0, 1));
            Assert.Equal(TeamId.None, ConquestScoreRule.Leader(300, 300));
        }

        // ------------------------------------------------------------------ the territory award

        [Fact]
        public void HoldingMorePointsScoresTheDifferenceEveryInterval()
        {
            // Team 0 holds its base and the middle, team 1 its base: two against one.
            MatchStateMachine match = Playing(new MatchRules { TerritoryAwardSeconds = 5f }, middleOwner: -1f);

            Advance(match, 20.5f);

            Assert.Equal(4, match.Score0);   // one point, four intervals
            Assert.Equal(0, match.Score1);
            Assert.Equal(4, match.TerritoryPointsAwarded);
        }

        [Fact]
        public void AnEvenSplitScoresNothing()
        {
            MatchStateMachine match = Playing(new MatchRules { TerritoryAwardSeconds = 5f }, middleOwner: 0f);

            Advance(match, 30f);

            Assert.Equal(0, match.Score0);
            Assert.Equal(0, match.Score1);
        }

        [Fact]
        public void TheFirstAwardLandsOneIntervalIntoTheRound()
        {
            MatchStateMachine match = Playing(new MatchRules { TerritoryAwardSeconds = 5f }, middleOwner: -1f);

            Advance(match, 4.5f);
            Assert.Equal(0, match.Score0);

            Advance(match, 1f);
            Assert.Equal(1, match.Score0);
        }

        [Fact]
        public void WithoutTheRuleOnlyKillsScore()
        {
            // The original: the same two-against-one hold, a minute of it, and no award.
            MatchStateMachine match = Playing(new MatchRules(), middleOwner: -1f);

            Advance(match, 60f);

            Assert.Equal(0, match.Score0);
            Assert.Equal(0, match.TerritoryPointsAwarded);
        }

        [Fact]
        public void TheAwardCanWinTheRoundOnItsOwn()
        {
            // Holding ground is enough by itself: 200 points at one a second.
            MatchStateMachine match = Playing(
                new MatchRules { TerritoryAwardSeconds = 1f, VictoryPoints = 20 }, middleOwner: -1f);

            Advance(match, 21f);

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(TeamId.Team0, match.ToMessage().WinningTeam);
        }

        // ------------------------------------------------------------------ the time limit

        [Fact]
        public void ALimitedRoundCountsDownOnTheWire()
        {
            MatchStateMachine match = Playing(new MatchRules { TimeLimitSeconds = 60f }, middleOwner: 0f);

            Assert.InRange(match.ToMessage().PhaseSecondsRemaining, 59, 60);

            Advance(match, 10f);

            Assert.InRange(match.ToMessage().PhaseSecondsRemaining, 50, 51);   // whole seconds, rounded up
        }

        [Fact]
        public void AnUnlimitedRoundSendsNoClock()
        {
            MatchStateMachine match = Playing(new MatchRules(), middleOwner: 0f);

            Advance(match, 10f);

            Assert.Equal(0, match.ToMessage().PhaseSecondsRemaining);
        }

        [Fact]
        public void AtTheLimitTheTeamAheadWinsAndEveryClientCanSaySo()
        {
            MatchStateMachine match = Playing(new MatchRules { TimeLimitSeconds = 60f }, middleOwner: 0f);

            match.ReportDeath(TeamId.Team1);   // one kill for team 0, worth its one base
            Advance(match, 61f);

            MatchStateMessage message = match.ToMessage();

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(1, match.RoundsEndedByTimeLimit);

            // The margin rule is what a released 1.0 client reads the winner from, so the leader
            // is raised to exactly the margin rather than ending on a lead it cannot see.
            Assert.Equal(TeamId.Team0, message.WinningTeam);
            Assert.Equal(match.Score1 + match.VictoryPoints, match.Score0);
        }

        [Fact]
        public void ALevelScoreAtTheLimitPlaysOnUntilTheNextPoint()
        {
            MatchStateMachine match = Playing(new MatchRules { TimeLimitSeconds = 60f }, middleOwner: 0f);

            Advance(match, 70f);

            Assert.Equal(MatchPhase.Playing, match.Phase);
            Assert.Equal(0, match.RoundsEndedByTimeLimit);

            match.ReportDeath(TeamId.Team0);   // team 1 scores
            match.Tick(Tick, 2, NoActors);

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(TeamId.Team1, match.ToMessage().WinningTeam);
        }

        [Fact]
        public void TheNextRoundStartsWithAFullClock()
        {
            var rules = new MatchRules
            {
                TimeLimitSeconds = 60f,
                PostMatchSeconds = 1f,
                WarmupSeconds = 1f,
                MinPlayersToStart = 2,
            };
            MatchStateMachine match = Playing(rules, middleOwner: 0f);

            match.ReportDeath(TeamId.Team1);
            Advance(match, 61f);          // ended by the clock
            Advance(match, 1.2f);         // post-match, reset, waiting
            Advance(match, 1.2f);         // warmup and back into a round

            Assert.Equal(MatchPhase.Playing, match.Phase);
            Assert.InRange(match.ToMessage().PhaseSecondsRemaining, 58, 60);
        }

        // ------------------------------------------------------------------ the client's clock

        [Fact]
        public void ALimitedRoundShowsItsClockOnTheClient()
        {
            var model = new MatchStateModel();
            model.Apply(new MatchStateMessage(MatchPhase.Playing, 40, 38, 1200, 2, 200), nowSeconds: 10f);

            Assert.True(model.HasTimer);
            Assert.Equal(1199.5f, model.SecondsRemaining(10.5f), 3);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// A round in progress on a three-point map: team 0's base, a middle point whose owner is
        /// <paramref name="middleOwner"/> (-1 team 0, +1 team 1, 0 neutral), team 1's base.
        /// </summary>
        private static MatchStateMachine Playing(MatchRules rules, float middleOwner)
        {
            if (rules.WarmupSeconds > 1f) rules.WarmupSeconds = 1f;

            var match = new MatchStateMachine(
                rules,
                new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f),
                new CapturePointState(1, new Vec3(500f, 0f, 0f), 10f),
                new CapturePointState(2, new Vec3(1000f, 0f, 0f), 10f));

            match.AdoptOpeningOwner(0, -1f);
            match.AdoptOpeningOwner(1, middleOwner);
            match.AdoptOpeningOwner(2, +1f);

            match.Tick(Tick, 2, NoActors);   // waiting -> warmup
            while (match.Phase != MatchPhase.Playing) match.Tick(Tick, 2, NoActors);

            return match;
        }

        private static void Advance(MatchStateMachine match, float seconds)
        {
            int ticks = (int)Math.Ceiling(seconds / Tick);
            for (int i = 0; i < ticks; i++) match.Tick(Tick, 2, NoActors);
        }
    }
}
