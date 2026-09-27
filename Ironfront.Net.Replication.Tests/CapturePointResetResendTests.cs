using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A match reset reaches the clients that stayed connected through it.
    /// </summary>
    /// <remarks>
    /// Measured on the Azure Island server on 2026-09-28: the server reset its flags to 1 / 1 at
    /// the end of a round while a client that stayed connected kept drawing 3 / 2 -- flags, flag
    /// poles and score multiplier -- into the next round. <c>PerformReset</c> cleared the dirty
    /// list after resetting the points, and points only tick while a round is played, so nothing
    /// ever reported the reset. Joiners were unaffected: they receive every point on join.
    /// </remarks>
    public sealed class CapturePointResetResendTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static readonly ActorPresence[] NoActors = Array.Empty<ActorPresence>();

        private static MatchStateMachine OneBaseEach()
        {
            var match = new MatchStateMachine(
                new MatchRules
                {
                    MinPlayersToStart = 2,
                    WarmupSeconds     = 1f,
                    PostMatchSeconds  = 1f,
                    VictoryPoints     = 5,
                },
                new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f),
                new CapturePointState(1, new Vec3(500f, 0f, 0f), 10f),
                new CapturePointState(2, new Vec3(1000f, 0f, 0f), 10f));

            match.AdoptOpeningOwner(0, -1f);
            match.AdoptOpeningOwner(2, +1f);
            return match;
        }

        /// <summary>
        /// One tick followed by what <c>MatchController.BroadcastDirtyCapturePoints</c> does:
        /// every dirty point is sent and marked sent. Returns the ids sent.
        /// </summary>
        private static List<byte> TickAndSend(MatchStateMachine match, ReadOnlySpan<ActorPresence> actors)
        {
            match.Tick(Tick, 2, actors);

            var sent = new List<byte>();
            foreach (byte id in match.DirtyCapturePoints)
            {
                Assert.True(match.TryGetPoint(id, out CapturePointState? point));
                point!.MarkSent();
                sent.Add(id);
            }

            return sent;
        }

        private static void Run(MatchStateMachine match, float seconds, ActorPresence[] actors)
        {
            int ticks = (int)Math.Ceiling(seconds / Tick);
            for (int i = 0; i < ticks; i++) TickAndSend(match, actors);
        }

        [Fact]
        public void TheRoundAfterAResetOpensWithEveryPointResent()
        {
            MatchStateMachine match = OneBaseEach();
            Run(match, 1.2f, NoActors);
            Assert.Equal(MatchPhase.Playing, match.Phase);

            // Team 0 takes the middle point, so the round ends somewhere the reset moves away from.
            var onTheMiddle = new[] { new ActorPresence(new Vec3(500f, 0f, 0f), TeamId.Team0, isAlive: true) };
            Run(match, 2f, onTheMiddle);
            Assert.Equal(TeamId.Team0, match.CapturePoints[1].OwningTeam);

            for (int i = 0; i < 5; i++) match.ReportDeath(TeamId.Team0);
            TickAndSend(match, NoActors);
            Assert.Equal(MatchPhase.Ended, match.Phase);

            List<byte>? sentAtReset = null;
            for (int i = 0; i < 200 && sentAtReset == null; i++)
            {
                MatchPhase before = match.Phase;
                List<byte> sent = TickAndSend(match, NoActors);
                if (before == MatchPhase.Resetting || (before == MatchPhase.Ended && match.Phase == MatchPhase.WaitingForPlayers))
                    sentAtReset = sent;
            }

            Assert.NotNull(sentAtReset);
            Assert.Equal(new byte[] { 0, 1, 2 }, sentAtReset);
            Assert.Equal(TeamId.None, match.CapturePoints[1].OwningTeam);

            // Sent once, not every tick after.
            Assert.Empty(TickAndSend(match, NoActors));
        }

        [Fact]
        public void AForceResetBetweenTicksIsSentOnTheNextTick()
        {
            MatchStateMachine match = OneBaseEach();
            Run(match, 1.2f, NoActors);

            // ServerTickLoop calls this when the last player releases the room: between ticks,
            // and the next tick opens by clearing the dirty list.
            match.ForceReset();

            Assert.Equal(new byte[] { 0, 1, 2 }, TickAndSend(match, NoActors));
            Assert.Empty(TickAndSend(match, NoActors));
        }

        [Fact]
        public void AResetThatFailedToSendIsOfferedAgain()
        {
            MatchStateMachine match = OneBaseEach();
            Run(match, 1.2f, NoActors);
            match.ForceReset();

            // No transport this tick: nothing is marked sent.
            match.Tick(Tick, 2, NoActors);
            Assert.Equal(3, match.DirtyCapturePoints.Count);

            Assert.Equal(new byte[] { 0, 1, 2 }, TickAndSend(match, NoActors));
        }

        [Fact]
        public void AResetPointIsDueUntilItIsSent()
        {
            var point = new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f);
            point.MarkSent();
            Assert.False(point.ResendDue);

            point.Reset();
            Assert.True(point.ResendDue);

            point.MarkSent();
            Assert.False(point.ResendDue);
        }
    }
}
