using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-85 — every match ended about half a second into <see cref="MatchPhase.Playing"/>,
    /// seven times in one playtest, always "winner team 0" at 200-0 that nobody earned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism, as it was.</b> A capture point used to count as held only past a 0.9
    /// ownership threshold, so it flipped <c>OwningTeam</c> from the team that held it to
    /// <see cref="TeamId.None"/> about half a second into a one-body attack — long before it
    /// could flip to the other team — and <see cref="MatchStateMachine.ApplyElimination"/> read
    /// that tick's "zero spawn points" as a genuine wipe-out, ending the round on the spot.
    /// <see cref="CapturePointState"/> now follows the original game's rule instead: a point
    /// stays its owner's until its control reaches zero and then changes hands outright, so the
    /// anchor is lost only when it is actually captured. These tests keep pinning both halves --
    /// a partial attack never ends the round, a real capture still does. Every prior elimination test in <c>ObjectiveAuthorityTests</c> feeds
    /// <see cref="MatchStateMachine.SetSpawnPointCounts"/> hand-written integers and is
    /// structurally blind to this — the integers never pass through the threshold that produces
    /// them in production. This suite drives the real capture arithmetic instead, through
    /// <see cref="CapturePointState.Tick"/> and <see cref="MatchStateMachine.CapturePoints"/>,
    /// the same path <c>MatchController.FixedUpdate</c> reads before calling
    /// <see cref="MatchStateMachine.SetSpawnPointCounts"/> — a test shaped like the ones that
    /// already existed would have missed this exact defect a second time.
    /// </para>
    /// <para>
    /// <b>Two capture points, not one</b> — team 0's own anchor is planted 1000 units away from
    /// every actor in this suite and its capture speed is left at the default, so it never moves
    /// and team 0's held-point count is a stable 1 throughout. Only team 1's anchor is contested.
    /// A single-point setup would have made team 0's count zero from the first tick of
    /// <see cref="MatchPhase.Playing"/> for an unrelated reason (no base of its own at all), and
    /// conflated that with the crossing defect this suite exists to isolate.
    /// </para>
    /// </remarks>
    public sealed class EliminationDwellTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>
        /// Team 1's only anchor is planted here, adopted at full ownership (+1). Contested by
        /// the lone team-0 attacker at <see cref="Vec3.Zero"/>.
        /// </summary>
        private const float ContestedRadius = 30f;

        /// <summary>
        /// Team 0's own anchor. Far enough from every actor in this suite (radius 10 at 1000
        /// units out) that it never sees a body and never moves.
        /// </summary>
        private static readonly Vec3 StaticAnchorPosition = new Vec3(1000f, 0f, 0f);

        /// <summary>
        /// When a lone attacker captures the contested anchor: one step a second at 0.2 each,
        /// and it takes SIX -- five steps of 0.2f leave the control at 3e-8 in float arithmetic,
        /// which is not yet zero, exactly as it would not be in the original.
        /// </summary>
        private const float CaptureSeconds = 6f;

        private static MatchStateMachine BuildTwoAnchorMatch(
            float eliminationDwellSeconds, out CapturePointState contested)
        {
            var staticAnchor = new CapturePointState(0, StaticAnchorPosition, radius: 10f, captureSpeed: 0.2f);
            contested = new CapturePointState(1, Vec3.Zero, radius: ContestedRadius, captureSpeed: 0.2f);

            var rules = new MatchRules
            {
                MinPlayersToStart      = 1,
                WarmupSeconds          = 0f,
                EliminationGraceSeconds = 1f,
                EliminationDwellSeconds = eliminationDwellSeconds,
            };

            var machine = new MatchStateMachine(rules, staticAnchor, contested);
            machine.AdoptOpeningOwner(0, -1f); // team 0's own base
            machine.AdoptOpeningOwner(1, +1f); // team 1's only base

            for (int i = 0; i < 4000 && machine.Phase != MatchPhase.Playing; i++)
                TickWithCensus(machine, ReadOnlySpan<ActorPresence>.Empty);
            Assert.Equal(MatchPhase.Playing, machine.Phase);

            // Past the round-opening grace window, with both anchors still fully held --
            // nothing here is testing the grace window itself.
            int graceTicks = (int)(rules.EliminationGraceSeconds / Tick) + 4;
            for (int i = 0; i < graceTicks; i++)
                TickWithCensus(machine, ReadOnlySpan<ActorPresence>.Empty);
            Assert.Equal(MatchPhase.Playing, machine.Phase);

            return machine;
        }

        /// <summary>
        /// Reports the held-spawn-point census read off <see cref="MatchStateMachine.CapturePoints"/>
        /// and then ticks -- the same order <c>MatchController.FixedUpdate</c> uses
        /// (<c>ReportSpawnPointCounts()</c> before <c>_match.Tick(...)</c>), so this suite drives
        /// elimination through the real ownership threshold instead of a hand-fed integer.
        /// </summary>
        private static void TickWithCensus(MatchStateMachine machine, ReadOnlySpan<ActorPresence> actors)
        {
            int owned0 = 0, owned1 = 0;
            for (int i = 0; i < machine.CapturePoints.Count; i++)
            {
                byte owner = machine.CapturePoints[i].OwningTeam;
                if (owner == TeamId.Team0) owned0++;
                else if (owner == TeamId.Team1) owned1++;
            }

            machine.SetSpawnPointCounts(owned0, owned1);
            machine.Tick(Tick, 1, actors);
        }

        /// <summary>
        /// Feeds one team-0 attacker, alone, inside the contested anchor's radius for
        /// <paramref name="seconds"/> of simulated time -- the census of Dustbowl's X-85
        /// playtest.
        /// </summary>
        private static void FeedOneBodyAdvantage(MatchStateMachine machine, float seconds)
        {
            var actors = new[] { new ActorPresence(Vec3.Zero, TeamId.Team0, isAlive: true) };
            var span = new ReadOnlySpan<ActorPresence>(actors);

            int ticks = (int)Math.Ceiling(seconds / Tick);
            for (int i = 0; i < ticks && machine.Phase == MatchPhase.Playing; i++)
                TickWithCensus(machine, span);
        }

        /// <summary>
        /// The regression X-85 was: a one-body attack on team 1's only anchor must not end the
        /// round before the anchor is captured. Under the old threshold it read as lost 0.55s in;
        /// under the original rule it is still team 1's until its control reaches zero.
        /// </summary>
        [Fact]
        public void AMomentaryOwnershipCrossingDoesNotEliminateWithinTheDwellWindow()
        {
            // The shipped MatchController default (see MatchController._eliminationDwellSeconds),
            // not MatchRules' own zero -- this test is asserting the PRODUCTION behaviour, not
            // merely the library's permissive default that the older, hand-fed-integer tests
            // depend on staying instant.
            MatchStateMachine machine = BuildTwoAnchorMatch(eliminationDwellSeconds: 5f, out _);

            int endings = 0;
            machine.MatchEnded += _ => endings++;

            // 1.5s: one ownership step, control 1 -> 0.8, the anchor still team 1's.
            FeedOneBodyAdvantage(machine, seconds: 1.5f);

            Assert.Equal(MatchPhase.Playing, machine.Phase);
            Assert.Equal(0, endings);
            Assert.Equal(0, machine.Score0);
        }

        /// <summary>
        /// The dwell requirement is not a disguised way of turning elimination off: a team that
        /// is genuinely wiped out -- the anchor stays lost for the FULL dwell duration, not one
        /// tick -- still loses the round.
        /// </summary>
        [Fact]
        public void HoldingTheAnchorLostForTheFullDwellStillEliminates()
        {
            const float dwell = 0.2f; // kept short so the test does not spend real ticks proving nothing new
            MatchStateMachine machine = BuildTwoAnchorMatch(eliminationDwellSeconds: dwell, out _);

            byte winner = TeamId.Team1;
            machine.MatchEnded += team => winner = team;

            // Capture it -- see CaptureSeconds for when it changes hands -- and then hold it
            // well past the dwell window: a genuine loss of the anchor.
            FeedOneBodyAdvantage(machine, seconds: CaptureSeconds + dwell + 0.5f);

            Assert.Equal(MatchPhase.Ended, machine.Phase);
            Assert.Equal(TeamId.Team0, winner);
            Assert.Equal(machine.VictoryPoints, machine.Score0);
        }

        /// <summary>
        /// <see cref="MatchRules.EliminationDwellSeconds"/> defaults to instant (0), matching
        /// every elimination behaviour <c>ObjectiveAuthorityTests</c> already pins for a
        /// <see cref="MatchRules"/> built with no dwell set -- this change adds an OPT-IN
        /// continuous-hold requirement, it does not change what a bare <see cref="MatchRules"/>
        /// does. <c>MatchController</c> is the one place that opts in, with its own serialized
        /// default in the 5-10s range.
        /// </summary>
        [Fact]
        public void EliminationDwellDefaultsToInstantSoExistingRulesStayUnchanged()
        {
            Assert.Equal(0f, MatchRules.Default.EliminationDwellSeconds);

            MatchStateMachine machine = BuildTwoAnchorMatch(eliminationDwellSeconds: 0f, out _);

            int endings = 0;
            machine.MatchEnded += _ => endings++;

            // The very first tick after the anchor changes hands must end it immediately -- the
            // pre-existing, already-pinned instant behaviour.
            FeedOneBodyAdvantage(machine, seconds: CaptureSeconds + 0.1f);

            Assert.Equal(MatchPhase.Ended, machine.Phase);
            Assert.Equal(1, endings);
        }
    }
}
