using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-03 tasks 1 and 3: the match state machine, tickets, and win conditions.
    /// </summary>
    public sealed class MatchLifecycleTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static readonly ActorPresence[] NoActors = Array.Empty<ActorPresence>();

        private static MatchRules FastRules() => new MatchRules
        {
            MinPlayersToStart = 2,
            WarmupSeconds     = 1f,
            PostMatchSeconds  = 1f,
            VictoryPoints     = 5,
        };

        /// <summary>
        /// A machine on a map shaped like the shipped ones: one base per team, one neutral point
        /// in the middle.
        /// </summary>
        /// <remarks>
        /// <b>Every scoring test needs this and the phase-transition tests do not.</b> A kill is
        /// worth <c>ConquestScoreRule.Award(points, flags)</c>, and the multiplier is the
        /// identity function -- so on a machine with NO capture points every kill is worth zero
        /// and no round can ever end. That is the rule faithfully, not a defect: both shipped
        /// maps open one point per side, which is what this reproduces.
        /// </remarks>
        private static MatchStateMachine OneBaseEach(MatchRules? rules = null)
        {
            var match = new MatchStateMachine(
                rules ?? FastRules(),
                new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f),
                new CapturePointState(1, new Vec3(500f, 0f, 0f), 10f),
                new CapturePointState(2, new Vec3(1000f, 0f, 0f), 10f));

            match.AdoptOpeningOwner(0, -1f);   // team 0's base
            match.AdoptOpeningOwner(2, +1f);   // team 1's base
            return match;
        }

        private static void Advance(MatchStateMachine match, float seconds, int humans)
        {
            int ticks = (int)Math.Ceiling(seconds / Tick);
            for (int i = 0; i < ticks; i++) match.Tick(Tick, humans, NoActors);
        }

        // ------------------------------------------------------------------ phase transitions

        [Fact]
        public void AMatchWaitsUntilItHasEnoughHumans()
        {
            var match = new MatchStateMachine(FastRules());

            match.Tick(Tick, humanPlayerCount: 1, NoActors);
            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);

            match.Tick(Tick, humanPlayerCount: 2, NoActors);
            Assert.Equal(MatchPhase.Warmup, match.Phase);
        }

        [Fact]
        public void BotsDoNotCountTowardStarting()
        {
            var match = new MatchStateMachine(FastRules());

            // 32 bots present on the capture-point list, zero humans connected. The bots are
            // very much in the world; they are simply not who the round is waiting for.
            var bots = new ActorPresence[32];
            for (int i = 0; i < bots.Length; i++)
                bots[i] = new ActorPresence(Vec3.Zero, (byte)(i % 2), isAlive: true);

            match.Tick(Tick, humanPlayerCount: 0, bots);

            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);
        }

        [Fact]
        public void WarmupRunsDownAndOpensTheRound()
        {
            var match = new MatchStateMachine(FastRules());

            match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Warmup, match.Phase);

            Advance(match, 0.5f, 2);
            Assert.Equal(MatchPhase.Warmup, match.Phase);

            Advance(match, 0.6f, 2);
            Assert.Equal(MatchPhase.Playing, match.Phase);
        }

        [Fact]
        public void WarmupFallsBackWhenThePlayersLeaveAgain()
        {
            var match = new MatchStateMachine(FastRules());

            match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Warmup, match.Phase);

            match.Tick(Tick, 1, NoActors);

            // Starting a round for one player because two were briefly connected produces a
            // match that ends before anybody can join it, and then resets, repeatedly.
            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);
        }

        [Fact]
        public void ALiveRoundIsNotAbandonedWhenTheHumansLeave()
        {
            var match = new MatchStateMachine(FastRules());
            Advance(match, 1.2f, 2);
            Assert.Equal(MatchPhase.Playing, match.Phase);

            Advance(match, 1f, 0);

            // The bots are still fighting and the master is still owed a GS_MATCH_ENDED. A
            // round that silently evaporates leaves the server advertising a state it is not in.
            Assert.Equal(MatchPhase.Playing, match.Phase);
        }

        // ------------------------------------------------------------------ scoring

        /// <summary>
        /// The direction P11 reversed. A death awards the victim's OPPONENT; the victim's own
        /// side is not touched, in either direction.
        /// </summary>
        [Fact]
        public void ADeathScoresForTheTeamOppositeTheVictim()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);

            match.ReportDeath(TeamId.Team0);

            Assert.Equal(0, match.Score0);
            Assert.Equal(1, match.Score1);
        }

        /// <summary>
        /// The friendly-fire penalty, which is economic rather than mechanical. Nothing in the
        /// scoring path reads the killer, so a team-kill is indistinguishable from any other
        /// death and credits the enemy -- which is the point, and is why there is no gate.
        /// </summary>
        [Fact]
        public void ATeamKillScoresForTheEnemyBecauseNothingReadsTheKiller()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);

            // ReportDeath takes the VICTIM's team and takes nothing else. There is no argument
            // a team-kill could differ in, so this test is the shape of the guarantee: if a
            // killer ever entered the signature, this call would stop compiling.
            match.ReportDeath(TeamId.Team1);

            Assert.Equal(1, match.Score0);
            Assert.Equal(0, match.Score1);
        }

        /// <summary>Ascending means ascending: no path here subtracts.</summary>
        [Fact]
        public void NeitherSideEverLosesPoints()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);

            match.ReportDeath(TeamId.Team1);       // team 0 scores
            int score0 = match.Score0;

            match.ReportDeath(TeamId.Team0);       // team 1 scores
            Advance(match, 2f, 2);                 // and time alone moves nothing

            Assert.Equal(score0, match.Score0);
            Assert.Equal(1, match.Score1);
        }

        [Fact]
        public void DeathsOutsideTheRoundScoreNothing()
        {
            MatchStateMachine match = OneBaseEach();

            match.Tick(Tick, 2, NoActors);              // Warmup
            match.ReportDeath(TeamId.Team0);
            Assert.Equal(0, match.Score1);

            Advance(match, 1.2f, 2);                    // Playing
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);
            match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Ended, match.Phase);

            // A kill landing during the post-match scoreboard must not move the score the
            // scoreboard is displaying.
            match.ReportDeath(TeamId.Team0);
            Assert.Equal(5, match.Score1);
        }

        /// <summary>
        /// Victory is a MARGIN. Both sides trading kills evenly never ends the round, however
        /// high the numbers go -- which is precisely what the ticket rule could not express.
        /// </summary>
        [Fact]
        public void ALevelScoreNeverEndsTheRoundHoweverHighItGets()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);

            for (int i = 0; i < 20; i++)
            {
                match.ReportDeath(TeamId.Team0);
                match.ReportDeath(TeamId.Team1);
                match.Tick(Tick, 2, NoActors);
            }

            Assert.Equal(20, match.Score0);
            Assert.Equal(20, match.Score1);
            Assert.Equal(MatchPhase.Playing, match.Phase);
        }

        [Fact]
        public void LeadingByTheVictoryMarginEndsTheMatchAndNamesTheWinner()
        {
            MatchStateMachine match = OneBaseEach();
            byte winner = 200;
            match.MatchEnded += team => winner = team;

            Advance(match, 1.2f, 2);
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);

            // The win condition is evaluated by the tick, not by ReportDeath. Ending the match
            // from inside a damage callback would raise MatchEnded partway through a tick, with
            // capture points already advanced.
            match.Tick(Tick, 2, NoActors);

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(TeamId.Team1, winner);
            Assert.Equal(TeamId.Team1, match.ToMessage().WinningTeam);
        }

        [Fact]
        public void AnUndecidedMatchReportsNoWinner()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);

            Assert.Equal(MatchPhase.Playing, match.Phase);
            Assert.Equal(TeamId.None, match.ToMessage().WinningTeam);
        }

        // ------------------------------------------------- flags as a multiplier, not a bleed

        /// <summary>
        /// What replaced the ticket bleed. Holding more ground does not drain the other side;
        /// it makes every one of your own kills worth more, which is the offline game's answer
        /// and now the only one.
        /// </summary>
        [Fact]
        public void HoldingMorePointsMakesEveryKillWorthMore()
        {
            var rules = FastRules();
            rules.VictoryPoints = 500;               // high enough that nothing ends mid-test
            MatchStateMachine match = OneBaseEach(rules);
            Advance(match, 1.2f, 2);

            // One base each: a kill is worth exactly 1 to either side.
            match.ReportDeath(TeamId.Team1);
            Assert.Equal(1, match.Score0);

            // Team 0 walks onto the neutral middle point and takes it. Two points now.
            var occupants = new[]
            {
                new ActorPresence(new Vec3(500f, 0f, 0f), TeamId.Team0, isAlive: true),
            };
            for (int i = 0; i < (int)(6f / Tick); i++) match.Tick(Tick, 2, occupants);
            Assert.Equal(TeamId.Team0, match.CapturePoints[1].OwningTeam);

            match.ReportDeath(TeamId.Team1);
            Assert.Equal(3, match.Score0);           // 1 + (1 x 2 flags)

            // And team 1, still on one base, is unaffected by team 0's holdings.
            match.ReportDeath(TeamId.Team0);
            Assert.Equal(1, match.Score1);
        }

        /// <summary>
        /// The multiplier is the identity function, so a team holding nothing scores nothing.
        /// That is § 1.1 property 3 -- the rule, not a hazard -- and it is what makes losing
        /// every point unrecoverable by trading kills.
        /// </summary>
        [Fact]
        public void ATeamHoldingNoPointsScoresNothingForItsKills()
        {
            var match = new MatchStateMachine(
                FastRules(), new CapturePointState(0, Vec3.Zero, 10f));
            match.AdoptOpeningOwner(0, +1f);         // team 1 holds the only point
            Advance(match, 1.2f, 2);

            match.ReportDeath(TeamId.Team1);         // team 0 kills, and holds nothing

            Assert.Equal(0, match.Score0);
        }

        /// <summary>
        /// An even split is even. Under the ticket rule this asserted that nobody bled; under
        /// the margin rule it asserts the stronger thing -- both sides earn at the same rate,
        /// so an equal exchange stays level and the round does not resolve itself.
        /// </summary>
        [Fact]
        public void AnEvenSplitScoresBothSidesAtTheSameRate()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);

            match.ReportDeath(TeamId.Team0);
            match.ReportDeath(TeamId.Team1);
            Advance(match, 6f, 2);

            Assert.Equal(1, match.Score0);
            Assert.Equal(1, match.Score1);
            Assert.Equal(MatchPhase.Playing, match.Phase);
        }

        // ------------------------------------------------------------------ reset

        [Fact]
        public void TheRoundResetsItselfAndComesBackToZero()
        {
            MatchStateMachine match = OneBaseEach();
            int resets = 0;
            match.ResetRequested += () => resets++;

            Advance(match, 1.2f, 2);
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);
            match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Ended, match.Phase);

            Advance(match, 1.1f, 0);        // post-match timer, then the one-tick Resetting

            Assert.Equal(1, resets);
            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);
            Assert.Equal(0, match.Score0);
            Assert.Equal(0, match.Score1);
        }

        [Fact]
        public void FiveMatchesBackToBackAllResolveAndAllReset()
        {
            MatchStateMachine match = OneBaseEach();
            int resets = 0;
            var winners = new List<byte>();
            match.ResetRequested += () => resets++;
            match.MatchEnded += winners.Add;

            for (int round = 0; round < 5; round++)
            {
                Advance(match, 1.2f, 2);
                Assert.Equal(MatchPhase.Playing, match.Phase);

                for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);
                match.Tick(Tick, 2, NoActors);
                Assert.Equal(MatchPhase.Ended, match.Phase);

                Advance(match, 1.1f, 2);

                // The state the phase-03 trap-1 audit is looking for: every round starts from
                // the same place, not from wherever the last one left off.
                Assert.Equal(0, match.Score0);
                Assert.Equal(0, match.Score1);

                // And the bases come back, or round two would open with both sides at a zero
                // multiplier and never end.
                Assert.Equal(TeamId.Team0, match.CapturePoints[0].OwningTeam);
                Assert.Equal(TeamId.None,  match.CapturePoints[1].OwningTeam);
                Assert.Equal(TeamId.Team1, match.CapturePoints[2].OwningTeam);
            }

            Assert.Equal(5, resets);
            Assert.Equal(5, match.CompletedMatches);
            Assert.All(winners, w => Assert.Equal(TeamId.Team1, w));
        }

        [Fact]
        public void ResettingLastsExactlyOneTick()
        {
            MatchStateMachine match = OneBaseEach();
            var seen = new List<MatchPhase>();
            match.PhaseChanged += seen.Add;

            Advance(match, 1.2f, 2);
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);
            Advance(match, 1.2f, 2);

            // Resetting is observable — a client that renders a phase it never sees would be
            // fine, but a phase the server can get stuck in would not.
            Assert.Contains(MatchPhase.Resetting, seen);
            Assert.NotEqual(MatchPhase.Resetting, match.Phase);
        }

        // ------------------------------------------------------------------ broadcast policy

        [Fact]
        public void APhaseChangeIsAlwaysWorthBroadcasting()
        {
            var match = new MatchStateMachine(FastRules());
            match.Tick(Tick, 2, NoActors);

            Assert.True(match.MatchStateIsDirty);
            match.MarkMatchStateSent();
            Assert.False(match.MatchStateIsDirty);
        }

        [Fact]
        public void AQuietRoundStillSendsOncePerSecond()
        {
            var match = new MatchStateMachine(FastRules());
            Advance(match, 1.2f, 2);
            match.MarkMatchStateSent();

            // Half a second of nothing happening: no message.
            Advance(match, 0.5f, 2);
            Assert.False(match.MatchStateIsDirty);

            Advance(match, 0.6f, 2);
            Assert.True(match.MatchStateIsDirty);
        }

        /// <summary>
        /// A kill is worth a message; a quiet tick is not.
        /// </summary>
        /// <remarks>
        /// This replaced <c>ABleedTooSmallToChangeTheDisplayedNumberSendsNothing</c>, which
        /// tested that a continuous 0.5/s bleed did not flag a message thirty times a second to
        /// report a number that had not moved. P11 deleted the bleed, so nothing accrues
        /// between ticks any more and the sub-integer case it guarded cannot arise -- the score
        /// moves only on a discrete event. The surviving obligation is that the discrete event
        /// IS sent, and that silence stays silent.
        /// </remarks>
        [Fact]
        public void AKillIsWorthAMessageAndAQuietTickIsNot()
        {
            MatchStateMachine match = OneBaseEach();
            var occupants = new[] { new ActorPresence(Vec3.Zero, TeamId.Team0, true) };

            Advance(match, 1.2f, 2);
            match.MarkMatchStateSent();

            int dirtyTicks = 0;
            for (int i = 0; i < (int)(0.9f / Tick); i++)
            {
                match.Tick(Tick, 2, occupants);
                if (match.MatchStateIsDirty) dirtyTicks++;
                match.MarkMatchStateSent();
            }

            Assert.Equal(0, dirtyTicks);

            match.ReportDeath(TeamId.Team0);
            Assert.True(match.MatchStateIsDirty, "a kill moved the score and said nothing");
        }

        // ------------------------------------------------------------------ the wire message

        [Theory]
        [InlineData(MatchPhase.WaitingForPlayers)]
        [InlineData(MatchPhase.Warmup)]
        [InlineData(MatchPhase.Playing)]
        [InlineData(MatchPhase.Ended)]
        [InlineData(MatchPhase.Resetting)]
        public void EveryPhaseSurvivesTheWire(MatchPhase phase)
        {
            var sent = new MatchStateMessage(phase, 137, 42, 9, 12, 200);
            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];

            Assert.Equal(MatchStateMessage.Size, sent.Write(buffer));
            Assert.True(MatchStateMessage.TryParse(buffer, out MatchStateMessage received));

            Assert.Equal(phase, received.Phase);
            Assert.Equal(137, received.Score0);
            Assert.Equal(42, received.Score1);
            Assert.Equal(9, received.PhaseSecondsRemaining);
            Assert.Equal(12, received.HumanPlayerCount);
            Assert.Equal(200, received.VictoryPoints);
        }

        [Fact]
        public void AnUndefinedPhaseByteIsRejectedRatherThanCastThrough()
        {
            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];
            new MatchStateMessage(MatchPhase.Playing, 1, 1, 0, 2, 200).Write(buffer);
            buffer[0] = 99;

            // A HUD stuck in a phase that does not exist is far harder to diagnose than a
            // rejected message.
            Assert.False(MatchStateMessage.TryParse(buffer, out _));
        }

        [Fact]
        public void ATruncatedMatchStateIsRejected()
        {
            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];
            new MatchStateMessage(MatchPhase.Playing, 1, 1, 0, 2, 200).Write(buffer);

            Assert.False(MatchStateMessage.TryParse(buffer.Slice(0, MatchStateMessage.Size - 1), out _));
        }

        [Fact]
        public void TheWinnerIsDerivedRatherThanStoredSoItCannotDisagreeWithTheScore()
        {
            // No winner field on the wire at all, so a message claiming team 0 won on a lower
            // score than team 1 is not expressible.
            var ended = new MatchStateMessage(MatchPhase.Ended, 0, 88, 12, 4, 88);
            Assert.Equal(TeamId.Team1, ended.WinningTeam);

            var drawn = new MatchStateMessage(MatchPhase.Ended, 7, 7, 12, 4, 88);
            Assert.Equal(TeamId.None, drawn.WinningTeam);

            var playing = new MatchStateMessage(MatchPhase.Playing, 0, 88, 0, 4, 88);
            Assert.Equal(TeamId.None, playing.WinningTeam);

            // The half the ticket rule could not express, and the reason P11 sends
            // victoryPoints: 88 against 0 is a commanding lead and NOT a win, because the
            // margin is 200. The old Tickets0 > Tickets1 would have named team 1 the winner of
            // a round nobody had won.
            var ahead = new MatchStateMessage(MatchPhase.Ended, 0, 88, 12, 4, 200);
            Assert.Equal(TeamId.None, ahead.WinningTeam);
        }

        [Fact]
        public void TheMessageReportsWhatTheMachineHolds()
        {
            var match = new MatchStateMachine(FastRules());
            match.Tick(Tick, 3, NoActors);

            MatchStateMessage message = match.ToMessage();

            Assert.Equal(MatchPhase.Warmup, message.Phase);
            Assert.Equal(0, message.Score0);
            Assert.Equal(3, message.HumanPlayerCount);
            Assert.True(message.PhaseSecondsRemaining >= 1);

            // The margin travels with the numbers it scales, or the client cannot draw the bar.
            Assert.Equal(5, message.VictoryPoints);
        }

        [Fact]
        public void ForceResetSkipsThePostMatchTimer()
        {
            MatchStateMachine match = OneBaseEach();
            Advance(match, 1.2f, 2);
            match.ReportDeath(TeamId.Team0);

            match.ForceReset();

            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);
            Assert.Equal(0, match.Score0);
            Assert.Equal(0, match.Score1);
        }

        [Fact]
        public void ANegativeDeltaIsRejectedRatherThanRunningTheClockBackwards()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new MatchStateMachine(FastRules()).Tick(-1f, 2, NoActors));
    }
}
