using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-53 — the map's opening capture-point ownership, and the end/reset loop discarding it
    /// produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What went wrong.</b> <c>MatchController</c> built each <c>CapturePointState</c> from
    /// position, radius and capture speed and never read the authored owner, so every point on
    /// every map started NEUTRAL on the server. The host wrote that neutrality onto every scene
    /// spawn point, <c>CountSpawnPointsOwnedBy</c> answered 0 for BOTH teams, and
    /// <c>ApplyElimination</c> read "no spawn points" as "wiped out" one second into
    /// <c>Playing</c> — every round. The deployed server drew and reset 34 times in four hours
    /// and never played a match; to a connected player the world is rebuilt underfoot every
    /// forty-odd seconds, which reads as falling through the ground.
    /// </para>
    /// <para>
    /// <b>Both halves are pinned here because fixing either alone leaves the loop.</b> Seeding
    /// without <c>Reset</c> plays round 1 and collapses from round 2; <c>Reset</c> without
    /// seeding collapses round 1. The two are separate assertions on purpose.
    /// </para>
    /// </remarks>
    public sealed class OpeningOwnershipTests
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

        private static CapturePointState[] TwoBasesAndAMiddle() => new[]
        {
            new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f),
            new CapturePointState(1, new Vec3(50f, 0f, 0f), 10f),
            new CapturePointState(2, new Vec3(100f, 0f, 0f), 10f),
        };

        private static void Advance(MatchStateMachine match, float seconds, int humans)
        {
            int ticks = (int)Math.Ceiling(seconds / Tick);
            for (int i = 0; i < ticks; i++) match.Tick(Tick, humans, NoActors);
        }

        /// <summary>Dustbowl's shape: team 0 a base, team 1 a base, the rest neutral.</summary>
        private static MatchStateMachine WithOpeningBases(out CapturePointState[] points)
        {
            points = TwoBasesAndAMiddle();
            var match = new MatchStateMachine(FastRules(), points);
            match.AdoptOpeningOwner(0, -1f);   // team 0
            match.AdoptOpeningOwner(2, +1f);   // team 1
            return match;
        }

        // ------------------------------------------------------------------- the seed

        [Fact]
        public void AdoptingAnOpeningOwnerGivesTheTeamThePointImmediately()
        {
            MatchStateMachine match = WithOpeningBases(out CapturePointState[] points);

            Assert.Equal(TeamId.Team0, points[0].OwningTeam);
            Assert.Equal(TeamId.None,  points[1].OwningTeam);
            Assert.Equal(TeamId.Team1, points[2].OwningTeam);
        }

        /// <summary>
        /// Without adoption every point is neutral — the state the host actually shipped. Kept
        /// as the control case: without it the assertions above would pass on a build where
        /// adoption did nothing, because a neutral point and an unadopted one look alike from
        /// anywhere except <c>OwningTeam</c>.
        /// </summary>
        [Fact]
        public void WithoutAdoptionEveryPointIsNeutral()
        {
            CapturePointState[] points = TwoBasesAndAMiddle();
            _ = new MatchStateMachine(FastRules(), points);

            Assert.All(points, p => Assert.Equal(TeamId.None, p.OwningTeam));
        }

        /// <summary>
        /// The opening state must be broadcast. Marking it clean would render both bases
        /// neutral on every client that joined before somebody walked onto one.
        /// </summary>
        [Fact]
        public void AdoptionMarksTheMatchStateDirty()
        {
            var match = new MatchStateMachine(FastRules(), TwoBasesAndAMiddle());

            match.AdoptOpeningOwner(0, -1f);

            Assert.True(match.MatchStateIsDirty);
        }

        // ------------------------------------------------------------------- the reset

        [Fact]
        public void ResetReturnsAPointToItsOpeningOwnerRatherThanToNeutral()
        {
            CapturePointState point = new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f);
            point.AdoptOpeningOwner(-1f);

            point.Reset();

            Assert.Equal(TeamId.Team0, point.OwningTeam);
            Assert.Equal(CapturePointMessage.PackOwner(-1f), point.LastSentQ);
        }

        /// <summary>A point nobody adopted still resets to neutral, exactly as it always did.</summary>
        [Fact]
        public void AnUnadoptedPointStillResetsToNeutral()
        {
            CapturePointState point = new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f);

            point.Reset();

            Assert.Equal(TeamId.None, point.OwningTeam);
        }

        // ------------------------------------------------------- the loop, both directions

        /// <summary>
        /// The defect itself, end to end: with both teams holding a base, a round runs past the
        /// elimination grace period instead of ending in a draw one second in.
        /// </summary>
        [Fact]
        public void AMatchWithABasePerTeamKeepsPlayingPastTheGracePeriod()
        {
            MatchStateMachine match = WithOpeningBases(out _);
            match.SetSpawnPointCounts(1, 1);

            Advance(match, 1.2f, humans: 2);           // WaitingForPlayers -> Warmup
            Advance(match, 1.2f, humans: 2);           // Warmup -> Playing
            Assert.Equal(MatchPhase.Playing, match.Phase);

            Advance(match, 3f, humans: 2);             // well past EliminationGraceSeconds

            Assert.Equal(MatchPhase.Playing, match.Phase);
            Assert.True(match.Tickets0 > 0, "team 0 still has tickets");
            Assert.True(match.Tickets1 > 0, "team 1 still has tickets");
        }

        /// <summary>
        /// And the direction that was live in production: no base for either team ends the
        /// round in a draw within the grace period, and says so out loud rather than looping in
        /// silence.
        /// </summary>
        [Fact]
        public void NoBaseForEitherTeamEndsTheRoundAndIsAnnounced()
        {
            var match = new MatchStateMachine(FastRules(), TwoBasesAndAMiddle());
            int announced = 0;
            match.BothTeamsEliminated += () => announced++;
            match.SetSpawnPointCounts(0, 0);

            Advance(match, 1.2f, humans: 2);
            Advance(match, 1.2f, humans: 2);
            Advance(match, 3f,   humans: 2);

            Assert.Equal(0, match.Tickets0);
            Assert.Equal(0, match.Tickets1);
            Assert.Equal(1, announced);
        }
    }
}
