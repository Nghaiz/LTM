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
    /// <b>The mechanism, in one sentence.</b> A capture point crossing
    /// <see cref="CapturePointMessage.OwnedThreshold"/> flips <c>OwningTeam</c> from the team
    /// that held it to <see cref="TeamId.None"/> for exactly one tick before it can flip to the
    /// other team outright — a one-body advantage does that crossing in about half a second at
    /// the map's own capture speed — and <see cref="MatchStateMachine.ApplyElimination"/> read
    /// that single tick's "zero spawn points" as a genuine wipe-out, ending the round on the
    /// spot. Every prior elimination test in <c>ObjectiveAuthorityTests</c> feeds
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
        /// <paramref name="seconds"/> of simulated time -- the exact census that pushes
        /// Fortress from <c>OwnerQ 100</c> to <c>89</c> in Dustbowl's own playtest log.
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
        /// The regression this change exists for. Team 1's anchor crosses
        /// <see cref="CapturePointMessage.OwnedThreshold"/> in well under a second at this
        /// capture speed and headcount -- 0.11 / 0.2 = 0.55s from full ownership down to the
        /// threshold -- and that single-tick crossing must not end the round on its own.
        /// </summary>
        /// <remarks>
        /// Fails today (before <see cref="MatchStateMachine"/>'s dwell requirement) at
        /// approximately 0.55s of simulated time, with <c>Score0</c> jumping straight to the
        /// victory margin. Passes once elimination requires the zero reading to hold for
        /// <see cref="MatchRules.EliminationDwellSeconds"/> continuously.
        /// </remarks>
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

            // 1.5s: comfortably past the ~0.55s crossing, nowhere near the 5s dwell.
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

            // Cross the threshold (~0.55s) and then hold the advantage well past the dwell
            // window on top of that -- a genuine, sustained loss of the anchor.
            FeedOneBodyAdvantage(machine, seconds: 0.55f + dwell + 0.5f);

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

            // The very first tick that reads the crossing must end it immediately -- the
            // pre-existing, already-pinned instant behaviour.
            FeedOneBodyAdvantage(machine, seconds: 0.6f);

            Assert.Equal(MatchPhase.Ended, machine.Phase);
            Assert.Equal(1, endings);
        }
    }
}
