using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The owner's ruling of 2026-09-29: a round plays by the original game's rules and no others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A death scores for the victim's opponents, one point times the capture points they hold,
    /// their base included. A team wins at a 200-point lead, or when the other side holds no
    /// spawn point. Every point on the map can be taken, bases too: the original's
    /// <c>canBeCaptured</c> is true on all six Dustbowl and all five Island points.
    /// </para>
    /// <para>
    /// #363 added a territory award (the side holding more points earned the difference every
    /// 5 s) and a 20-minute clock. The live Dustbowl server logged the result on 2026-09-29: red
    /// at 130 with 121 of it from the award, after seven deaths in the whole round, and the owner
    /// read the award's +4 a tick as a kill worth four. Both are gone; these tests keep them gone.
    /// Our own scenes had also switched <c>canBeCaptured</c> off on all four bases, so an attacker
    /// could never take one and elimination could never happen.
    /// </para>
    /// </remarks>
    public sealed class OriginalMatchRulesTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static readonly ActorPresence[] NoActors = Array.Empty<ActorPresence>();

        private static MatchRules Rules() => new MatchRules
        {
            MinPlayersToStart = 2,
            WarmupSeconds     = 1f,
            PostMatchSeconds  = 1f,
            VictoryPoints     = 200,
        };

        /// <summary>
        /// Dustbowl's six points in the owner's example: blue down to its base, red holding its own
        /// base and the four points between.
        /// </summary>
        private static MatchStateMachine RedHoldsFiveOfSix()
        {
            var match = new MatchStateMachine(
                Rules(),
                new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f),
                new CapturePointState(1, new Vec3(500f, 0f, 0f), 10f),
                new CapturePointState(2, new Vec3(1000f, 0f, 0f), 10f),
                new CapturePointState(3, new Vec3(1500f, 0f, 0f), 10f),
                new CapturePointState(4, new Vec3(2000f, 0f, 0f), 10f),
                new CapturePointState(5, new Vec3(2500f, 0f, 0f), 10f));

            match.AdoptOpeningOwner(0, -1f);    // blue's base
            for (int i = 1; i < 6; i++) match.AdoptOpeningOwner(i, +1f);   // red's base and four more
            return match;
        }

        private static MatchStateMachine Playing()
        {
            MatchStateMachine match = RedHoldsFiveOfSix();
            Advance(match, 1.5f);
            Assert.Equal(MatchPhase.Playing, match.Phase);
            return match;
        }

        private static void Advance(MatchStateMachine match, float seconds)
        {
            int ticks = (int)Math.Ceiling(seconds / Tick);
            for (int i = 0; i < ticks; i++) match.Tick(Tick, 2, NoActors);
        }

        [Fact]
        public void AKillIsWorthOnePointPerCapturePointTheScorersHoldTheirBaseIncluded()
        {
            MatchStateMachine match = Playing();

            match.ReportDeath(TeamId.Team0);
            Assert.True(match.Score1 == 5,
                $"A blue death scored {match.Score1} for red, holding five points with its base. "
                + "The original's multiplier is the scoring team's whole flag count.");

            match.ReportDeath(TeamId.Team1);
            Assert.Equal(1, match.Score0);
        }

        [Fact]
        public void HoldingMoreGroundScoresNothingUntilSomebodyDies()
        {
            MatchStateMachine match = Playing();

            Advance(match, 600f);

            Assert.True(match.Score0 == 0 && match.Score1 == 0,
                $"Ten minutes without a death moved the score to {match.Score0} / {match.Score1}. "
                + "In the original only a death scores; holding ground multiplies the kill.");
            Assert.Equal(MatchPhase.Playing, match.Phase);
        }

        [Fact]
        public void APlayingRoundHasNoClockAndDoesNotEndOnOne()
        {
            MatchStateMachine match = Playing();
            for (int i = 0; i < 30; i++) match.ReportDeath(TeamId.Team0);   // red 150 ahead, short of 200

            Advance(match, 45f * 60f);

            Assert.Equal(0f, match.PhaseSecondsRemaining);
            Assert.Equal(0, match.ToMessage().PhaseSecondsRemaining);
            Assert.True(match.Phase == MatchPhase.Playing,
                "A round ended on a clock. The original ends on a 200-point lead or on a side "
                + "losing every spawn point, never on time.");
        }

        /// <summary>
        /// Every capture point on both shipped maps can change hands, bases included.
        /// </summary>
        [Theory]
        [InlineData("Dustbowl", 6)]
        [InlineData("Island", 5)]
        public void EveryCapturePointOnTheMapCanBeCaptured(string map, int points)
        {
            string scene = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scenes", map + ".unity"));

            string[] flags = Regex.Matches(scene, @"^  canBeCaptured: (\d)\s*$", RegexOptions.Multiline)
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.Equal(points, flags.Length);
            Assert.True(flags.All(f => f == "1"),
                $"{map}: {flags.Count(f => f == "0")} of {points} capture points are locked. The "
                + "original lets every point be taken, bases included, and elimination depends on it.");
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
