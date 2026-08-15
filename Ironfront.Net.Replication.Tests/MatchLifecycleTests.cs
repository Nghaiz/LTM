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
            StartTickets      = 5,
        };

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

        // ------------------------------------------------------------------ tickets

        [Fact]
        public void ADeathCostsTheDyingTeamATicket()
        {
            var match = new MatchStateMachine(FastRules());
            Advance(match, 1.2f, 2);

            int before = match.Tickets0;
            match.ReportDeath(TeamId.Team0);

            Assert.Equal(before - 1, match.Tickets0);
            Assert.Equal(5, match.Tickets1);
        }

        [Fact]
        public void DeathsOutsideTheRoundCostNothing()
        {
            var match = new MatchStateMachine(FastRules());

            match.Tick(Tick, 2, NoActors);              // Warmup
            match.ReportDeath(TeamId.Team0);
            Assert.Equal(5, match.Tickets0);

            Advance(match, 1.2f, 2);                    // Playing
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team1);
            match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Ended, match.Phase);

            // A kill landing during the post-match scoreboard must not move the score the
            // scoreboard is displaying.
            match.ReportDeath(TeamId.Team0);
            Assert.Equal(5, match.Tickets0);
        }

        [Fact]
        public void TicketsNeverGoNegative()
        {
            var match = new MatchStateMachine(FastRules());
            Advance(match, 1.2f, 2);

            for (int i = 0; i < 50; i++) match.ReportDeath(TeamId.Team0);

            Assert.Equal(0, match.Tickets0);
        }

        [Fact]
        public void RunningOutOfTicketsEndsTheMatchAndNamesTheWinner()
        {
            var match = new MatchStateMachine(FastRules());
            byte winner = 200;
            match.MatchEnded += team => winner = team;

            Advance(match, 1.2f, 2);
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);

            // The win condition is evaluated by the tick, not by ReportDeath. Ending the match
            // from inside a damage callback would raise MatchEnded partway through a tick, with
            // capture points already advanced and tickets not yet drained.
            match.Tick(Tick, 2, NoActors);

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(TeamId.Team1, winner);
            Assert.Equal(TeamId.Team1, match.ToMessage().WinningTeam);
        }

        [Fact]
        public void AnUndecidedMatchReportsNoWinner()
        {
            var match = new MatchStateMachine(FastRules());
            Advance(match, 1.2f, 2);

            Assert.Equal(MatchPhase.Playing, match.Phase);
            Assert.Equal(TeamId.None, match.ToMessage().WinningTeam);
        }

        // ------------------------------------------------------------------ ticket bleed

        [Fact]
        public void HoldingMorePointsBleedsTheOtherSide()
        {
            var point = new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: 10f);
            var rules = FastRules();
            rules.StartTickets = 50;
            var match = new MatchStateMachine(rules, point);

            var occupants = new[]
            {
                new ActorPresence(Vec3.Zero, TeamId.Team0, isAlive: true),
            };

            // Six seconds, not two: Tickets1 rounds UP, so at 0.5 tickets a second the count
            // does not visibly move until a whole ticket has gone. Asserting on the float would
            // test the accumulator; asserting on the count tests what the scoreboard shows.
            for (int i = 0; i < (int)(6f / Tick); i++) match.Tick(Tick, 2, occupants);

            Assert.Equal(TeamId.Team0, point.OwningTeam);
            Assert.Equal(50, match.Tickets0);
            Assert.True(match.Tickets1 < 50, $"team 1 should be bleeding, has {match.Tickets1}");
        }

        [Fact]
        public void AnEvenSplitBleedsNobody()
        {
            var a = new CapturePointState(0, Vec3.Zero, 10f, captureSpeed: 10f);
            var b = new CapturePointState(1, new Vec3(100f, 0f, 0f), 10f, captureSpeed: 10f);
            var rules = FastRules();
            rules.StartTickets = 50;
            var match = new MatchStateMachine(rules, a, b);

            var occupants = new[]
            {
                new ActorPresence(Vec3.Zero, TeamId.Team0, true),
                new ActorPresence(new Vec3(100f, 0f, 0f), TeamId.Team1, true),
            };

            for (int i = 0; i < (int)(6f / Tick); i++) match.Tick(Tick, 2, occupants);

            Assert.Equal(TeamId.Team0, a.OwningTeam);
            Assert.Equal(TeamId.Team1, b.OwningTeam);
            Assert.Equal(50, match.Tickets0);
            Assert.Equal(50, match.Tickets1);
        }

        // ------------------------------------------------------------------ reset

        [Fact]
        public void TheRoundResetsItselfAndComesBackToFullTickets()
        {
            var match = new MatchStateMachine(FastRules());
            int resets = 0;
            match.ResetRequested += () => resets++;

            Advance(match, 1.2f, 2);
            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);
            match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Ended, match.Phase);

            Advance(match, 1.1f, 0);        // post-match timer, then the one-tick Resetting

            Assert.Equal(1, resets);
            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);
            Assert.Equal(5, match.Tickets0);
            Assert.Equal(5, match.Tickets1);
        }

        [Fact]
        public void FiveMatchesBackToBackAllResolveAndAllReset()
        {
            var point = new CapturePointState(0, Vec3.Zero, 10f);
            var match = new MatchStateMachine(FastRules(), point);
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
                Assert.Equal(5, match.Tickets0);
                Assert.Equal(5, match.Tickets1);
                Assert.Equal(0f, point.Owner);
            }

            Assert.Equal(5, resets);
            Assert.Equal(5, match.CompletedMatches);
            Assert.All(winners, w => Assert.Equal(TeamId.Team1, w));
        }

        [Fact]
        public void ResettingLastsExactlyOneTick()
        {
            var match = new MatchStateMachine(FastRules());
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

        [Fact]
        public void ABleedTooSmallToChangeTheDisplayedNumberSendsNothing()
        {
            // The whole point of the integer test in DrainTickets: the float bleeds every tick
            // but the wire carries whole tickets, so flagging on the float would put a reliable
            // message on channel 2 thirty times a second to say a number that has not changed.
            var point = new CapturePointState(0, Vec3.Zero, 10f, captureSpeed: 10f);
            var rules = FastRules();
            rules.StartTickets = 200;
            rules.BleedPerPointPerSecond = 0.5f;      // one ticket every two seconds
            var match = new MatchStateMachine(rules, point);

            var occupants = new[] { new ActorPresence(Vec3.Zero, TeamId.Team0, true) };

            Advance(match, 1.2f, 2);
            for (int i = 0; i < (int)(1f / Tick); i++) match.Tick(Tick, 2, occupants);
            match.MarkMatchStateSent();

            int dirtyTicks = 0;
            for (int i = 0; i < (int)(0.9f / Tick); i++)
            {
                match.Tick(Tick, 2, occupants);
                if (match.MatchStateIsDirty) dirtyTicks++;
                match.MarkMatchStateSent();
            }

            // At most one whole ticket can change inside 0.9 s at this bleed rate, so at most
            // one of those 27 ticks is worth a message.
            Assert.True(dirtyTicks <= 1, $"{dirtyTicks} dirty ticks in 0.9 s of quiet bleed");
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
            var sent = new MatchStateMessage(phase, 137, 42, 9, 12);
            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];

            Assert.Equal(MatchStateMessage.Size, sent.Write(buffer));
            Assert.True(MatchStateMessage.TryParse(buffer, out MatchStateMessage received));

            Assert.Equal(phase, received.Phase);
            Assert.Equal(137, received.Tickets0);
            Assert.Equal(42, received.Tickets1);
            Assert.Equal(9, received.PhaseSecondsRemaining);
            Assert.Equal(12, received.HumanPlayerCount);
        }

        [Fact]
        public void AnUndefinedPhaseByteIsRejectedRatherThanCastThrough()
        {
            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];
            new MatchStateMessage(MatchPhase.Playing, 1, 1, 0, 2).Write(buffer);
            buffer[0] = 99;

            // A HUD stuck in a phase that does not exist is far harder to diagnose than a
            // rejected message.
            Assert.False(MatchStateMessage.TryParse(buffer, out _));
        }

        [Fact]
        public void ATruncatedMatchStateIsRejected()
        {
            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];
            new MatchStateMessage(MatchPhase.Playing, 1, 1, 0, 2).Write(buffer);

            Assert.False(MatchStateMessage.TryParse(buffer.Slice(0, MatchStateMessage.Size - 1), out _));
        }

        [Fact]
        public void TheWinnerIsDerivedRatherThanStoredSoItCannotDisagreeWithTheScore()
        {
            // No winner field on the wire at all, so a message claiming team 0 won with fewer
            // tickets than team 1 is not expressible.
            var ended = new MatchStateMessage(MatchPhase.Ended, 0, 88, 12, 4);
            Assert.Equal(TeamId.Team1, ended.WinningTeam);

            var drawn = new MatchStateMessage(MatchPhase.Ended, 7, 7, 12, 4);
            Assert.Equal(TeamId.None, drawn.WinningTeam);

            var playing = new MatchStateMessage(MatchPhase.Playing, 0, 88, 0, 4);
            Assert.Equal(TeamId.None, playing.WinningTeam);
        }

        [Fact]
        public void TheMessageReportsWhatTheMachineHolds()
        {
            var match = new MatchStateMachine(FastRules());
            match.Tick(Tick, 3, NoActors);

            MatchStateMessage message = match.ToMessage();

            Assert.Equal(MatchPhase.Warmup, message.Phase);
            Assert.Equal(5, message.Tickets0);
            Assert.Equal(3, message.HumanPlayerCount);
            Assert.True(message.PhaseSecondsRemaining >= 1);
        }

        [Fact]
        public void ForceResetSkipsThePostMatchTimer()
        {
            var match = new MatchStateMachine(FastRules());
            Advance(match, 1.2f, 2);
            match.ReportDeath(TeamId.Team0);

            match.ForceReset();

            Assert.Equal(MatchPhase.WaitingForPlayers, match.Phase);
            Assert.Equal(5, match.Tickets0);
        }

        [Fact]
        public void ANegativeDeltaIsRejectedRatherThanRunningTheClockBackwards()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new MatchStateMachine(FastRules()).Tick(-1f, 2, NoActors));
    }
}
