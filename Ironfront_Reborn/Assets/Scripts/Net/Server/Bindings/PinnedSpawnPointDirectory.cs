using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// An <see cref="ISpawnPointDirectory"/> that narrows each team to an ordered ROTATION of
    /// slots, one of which <c>ServerCombatBridge.ChooseSpawnIndex</c> reports back at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why narrowing, and not a seed.</b> Ledger row <b>X-22</b>: four lane-B runs opened at
    /// 1,078 m, 940 m, ~940 m and adjacent, so checks 1, 2, 4 and 13 rode a coin flip and a
    /// failure could not be told from a run that never got close enough to try.
    /// <c>LaneBHarness</c> ALREADY calls <c>UnityEngine.Random.InitState</c> with a pinned seed,
    /// and that did not fix it — a seed fixes the draw SEQUENCE, not which draw a given player
    /// lands on. Three clients join over a real socket at times nobody controls, so
    /// <c>MoveToSpawnPoint</c> is reached at a different point in that sequence every run.
    /// </para>
    /// <para>
    /// Narrowing the candidate set sidesteps the sequence entirely. Reservoir sampling keeps
    /// whichever candidate wins <c>Random.Range(0, candidates) == 0</c>; with one candidate that
    /// range has a single value, so the pinned slot is returned whatever the RNG is doing. The
    /// draw is still consumed, so the rest of the run's stream is unchanged — this pins the
    /// spawn without perturbing anything else the seed governs.
    /// </para>
    /// <para>
    /// <b>One slot pinned puts every same-team player in each other's fire — ledger X-28.</b> A
    /// single index means every body placed for that team lands on the exact same point, so two
    /// or three same-team clients spawn stacked and start shooting through one another before
    /// any check that names an enemy has a chance to matter. A ROTATION — an ordered list of
    /// slots per team, advanced one placement at a time — spreads successive placements across
    /// distinct points while staying exactly as deterministic as the single-slot pin: the same
    /// sequence of placements always draws the same sequence of slots, in the same order, no
    /// RNG involved on either side. A single-element rotation (the original pin) is simply the
    /// rotation with nowhere else to go.
    /// </para>
    /// <para>
    /// <b>Every player spawns on an authored point</b> when a rotation is pinned, which is the
    /// intent: adjacent (or close, for a multi-slot rotation) bodies make the 95 s approach in
    /// <c>tools/lane-b/combat-driver.json</c> a formality rather than the thing the run is
    /// really testing.
    /// </para>
    /// <para>
    /// <b>The failure this causes on the shipping map, and when you learn about it.</b>
    /// Eligibility is still the inner directory's answer, so pinning a point whose
    /// <c>SpawnPoint.owner</c> names one team hands the other team no eligible slot at all —
    /// <c>ChooseSpawnIndex</c> returns -1 and that actor is never placed. That is ledger
    /// <b>X-63</b>, and it is why the option stopped pinning runs and started voiding them.
    /// </para>
    /// <para>
    /// <b>How many indices are actually hazardous, measured rather than asserted.</b> This
    /// remark has now been wrong twice in opposite directions, so both readings are recorded.
    /// It first read "On Dustbowl EVERY spawn point is team-owned, so <b>any</b> single pinned
    /// index starves one side"; that was false as authored data (P19 § 1.2). It was then
    /// corrected to "2 of 6 are hazardous", which was true of the authored owners but rested on
    /// an eligibility rule that has since been fixed. The spawn points ARE the capture points —
    /// <c>CapturePoint : SpawnPoint</c> is the only subclass and <c>ActorManager.spawnPoints</c>
    /// is <c>FindObjectsOfType&lt;SpawnPoint&gt;()</c> — and their authored owners are:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Dustbowl, 6 points:</b> Oasis <c>0</c>, Fortress <c>1</c>, and Bridge, Town, Outpost
    /// and Mine all <c>-1</c>. <b>2 of 6</b> are team-owned.
    /// </description></item>
    /// <item><description>
    /// <b>Island, 5 points:</b> Backside <c>0</c>, Landing <c>1</c>, and Farm, Fort and Beach
    /// all <c>-1</c>. <b>2 of 5</b> are team-owned.
    /// </description></item>
    /// </list>
    /// <para>
    /// <c>IsEligible</c> is now <c>point.owner == team</c>
    /// (<c>IronfrontNetBindings.ActorManagerSpawnPoints.IsEligible</c>), because
    /// <c>owner == -1</c> on a capture point means NEUTRAL rather than "any team" — that
    /// misreading is what dropped deploying players onto contested flags on 2026-09-04, and
    /// <c>ActorManager.RandomSpawnPointForTeam</c> had required <c>owner == team</c> all along.
    /// So on the shipping maps <b>every</b> index is now hazardous, for one reason or the other:
    /// pinning a base starves the other side, and pinning a neutral flag starves both. The
    /// refusal below is unchanged and still catches all of them, because it asks
    /// <c>IsEligible</c> rather than counting anything — which is the whole reason it survived a
    /// semantic change to the rule it consults. <b>Nothing here was re-tuned</b>: the per-team
    /// rotation and the construction-time refusal are right whether the hazardous fraction is a
    /// third or all of them. What a lane-B operator has to know is narrower than it sounds —
    /// <c>IRONFRONT_LANEB_SPAWN_INDEX</c> must name a rotation per team out of that team's OWN
    /// points, and on Dustbowl each side owns exactly one.
    /// </para>
    /// <para>
    /// <b>A rotation per team, and a refusal at construction.</b> The option still exists
    /// because what X-22 needed it for has not gone away; it now takes an ordered list of
    /// indices per team, so each side gets slots it may actually use, spread across distinct
    /// points rather than stacked on one (X-28). And the starvation is detected when the
    /// directory is BUILT rather than when an actor fails to spawn ninety seconds in — a run
    /// that voids itself at the top costs a minute, and one that voids itself in the middle
    /// costs the whole run plus the reading of it. Every slot in every team's rotation is
    /// checked, not merely the first: a rotation with one bad slot buried three deep would
    /// otherwise starve a placement in the middle of a run instead of at the top.
    /// </para>
    /// <para>
    /// This class still does not paper over anything with a fallback to sampling: a run that
    /// quietly stopped being deterministic is exactly the thing X-22 is about. It refuses
    /// instead.
    /// </para>
    /// <para>
    /// Diagnostics scaffolding. Nothing constructs it except <c>LaneBHarness</c>, behind
    /// <c>IRONFRONT_LANEB_SPAWN_INDEX</c>, so an ordinary session and a shipped server never see
    /// it. It lives here rather than in <c>Net/Diagnostics/</c> only so the EditMode suite that
    /// already drives <c>ChooseSpawnIndex</c> can reach it.
    /// </para>
    /// </remarks>
    public sealed class PinnedSpawnPointDirectory : ISpawnPointDirectory
    {
        private readonly ISpawnPointDirectory _inner;

        /// <summary>
        /// One ordered rotation of slots per team, indexed by team number. A one-element
        /// rotation is what the original single-slot pin has always meant.
        /// </summary>
        private readonly int[][] _rotationByTeam;

        /// <summary>
        /// How far each team's rotation has advanced, indexed by team number. Advanced only by
        /// <see cref="GetSpawnPosition"/> — see that method's own remark for why that call, and
        /// only that call, is the right moment.
        /// </summary>
        private readonly int[] _cursorByTeam;

        /// <param name="inner">The real directory. Eligibility and positions still come from it.</param>
        /// <param name="index">The slot pinned for every team.</param>
        public PinnedSpawnPointDirectory(ISpawnPointDirectory inner, int index)
            : this(inner, new[] { new[] { index }, new[] { index } })
        {
        }

        /// <param name="inner">The real directory. Eligibility and positions still come from it.</param>
        /// <param name="pinnedByTeam">
        /// One slot per team, indexed by team number. X-63: a single slot shared by both teams
        /// cannot work on a map whose every spawn point is team-owned.
        /// </param>
        public PinnedSpawnPointDirectory(ISpawnPointDirectory inner, int[] pinnedByTeam)
            : this(inner, PerTeamSingleSlotRotations(pinnedByTeam))
        {
        }

        /// <param name="inner">The real directory. Eligibility and positions still come from it.</param>
        /// <param name="rotationByTeam">
        /// One ORDERED rotation of slots per team, indexed by team number. Ledger X-28: a
        /// rotation of more than one slot spreads successive same-team placements across
        /// distinct points instead of stacking them on one. X-63 still applies to every slot in
        /// every team's rotation, not merely the first one drawn.
        /// </param>
        public PinnedSpawnPointDirectory(ISpawnPointDirectory inner, int[][] rotationByTeam)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (rotationByTeam == null || rotationByTeam.Length == 0)
            {
                throw new ArgumentException(
                    "at least one team's rotation is required", nameof(rotationByTeam));
            }

            for (int team = 0; team < rotationByTeam.Length; team++)
            {
                int[] rotation = rotationByTeam[team];
                if (rotation == null || rotation.Length == 0)
                {
                    throw new ArgumentException(
                        $"team {team}'s rotation has no slots", nameof(rotationByTeam));
                }

                for (int slotIndex = 0; slotIndex < rotation.Length; slotIndex++)
                {
                    if (rotation[slotIndex] >= 0) continue;

                    throw new ArgumentOutOfRangeException(
                        nameof(rotationByTeam), rotation[slotIndex],
                        $"team {team}'s rotation slot {slotIndex} is never the -1 that "
                        + "ChooseSpawnIndex returns for 'no eligible point'");
                }
            }

            _inner = inner;
            _rotationByTeam = rotationByTeam;
            _cursorByTeam = new int[rotationByTeam.Length];

            RefuseIfAnyTeamIsStarved();
        }

        private static int[][] PerTeamSingleSlotRotations(int[] pinnedByTeam)
        {
            if (pinnedByTeam == null || pinnedByTeam.Length == 0)
            {
                // Deferred to the shared constructor's own check so there is exactly one
                // message for "no rotation at all", regardless of which overload was called.
                return Array.Empty<int[]>();
            }

            var rotations = new int[pinnedByTeam.Length][];
            for (int team = 0; team < pinnedByTeam.Length; team++)
            {
                rotations[team] = new[] { pinnedByTeam[team] };
            }

            return rotations;
        }

        /// <summary>
        /// Throws when a team's rotation names a slot that team may not spawn on.
        /// </summary>
        /// <remarks>
        /// <b>X-63, and the whole point of doing it here.</b> Without this the starvation shows
        /// up as one actor silently never placed, minutes into a run, in a warning inside a
        /// server log nobody reads until the artifact turns out to be ungradeable.
        /// <b>2 of Dustbowl's 6 spawn points and 2 of Island's 5 are team-owned</b> (the class
        /// remark lists them) and the rest are neutral, so under <c>owner == team</c> eligibility
        /// EVERY shipping index starves somebody: a base starves the other side, a neutral flag
        /// starves both. This asks <c>IsEligible</c> rather than counting, so the refusal is
        /// exactly as correct now as it was under either of the two counts the class remark has
        /// carried. <b>Every slot in every team's rotation is checked</b>, not only the first one
        /// drawn — a bad slot three deep in a rotation would otherwise starve a placement in the
        /// middle of a run rather than refuse at the top.
        /// </remarks>
        private void RefuseIfAnyTeamIsStarved()
        {
            for (int team = 0; team < _rotationByTeam.Length; team++)
            {
                int[] rotation = _rotationByTeam[team];

                for (int slotIndex = 0; slotIndex < rotation.Length; slotIndex++)
                {
                    int slot = rotation[slotIndex];
                    if (_inner.IsEligible(slot, team)) continue;

                    throw new ArgumentException(
                        $"spawn slot {slot} (rotation position {slotIndex}) is not eligible for "
                        + $"team {team}, so that team would never be placed and the run would "
                        + "grade nothing. A slot is eligible only for the team that OWNS it: two "
                        + "of Dustbowl's six spawn points and two of Island's five name a team, "
                        + "one each, and every other point is neutral (owner -1) and eligible for "
                        + "nobody (ledger X-63). Pass one rotation per team, out of that team's "
                        + "own points, e.g. IRONFRONT_LANEB_SPAWN_INDEX=3,5.",
                        nameof(_rotationByTeam));
                }
            }
        }

        /// <summary>Team 0's current slot. Kept for callers and logs that pin one index for both.</summary>
        public int PinnedIndex => PinnedIndexFor(0);

        /// <summary>
        /// The slot <paramref name="team"/>'s rotation is currently sitting on, or -1 for an
        /// unknown team. Advances only when <see cref="GetSpawnPosition"/> consumes it.
        /// </summary>
        public int PinnedIndexFor(int team)
        {
            if (team < 0 || team >= _rotationByTeam.Length) return -1;

            int[] rotation = _rotationByTeam[team];
            return rotation[_cursorByTeam[team] % rotation.Length];
        }

        /// <inheritdoc />
        public int Count => _inner.Count;

        /// <inheritdoc />
        /// <remarks>
        /// Both halves matter. The index test is what removes the sampling; the inner call is
        /// what keeps the team rule, so this narrows the choice without ever widening it.
        /// </remarks>
        public bool IsEligible(int index, int team)
            => index == PinnedIndexFor(team) && _inner.IsEligible(index, team);

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <b>The rotation advance point, and it is not a guess.</b>
        /// <c>ServerCombatBridge.MoveToSpawnPoint</c> calls this exactly once per placement, on
        /// the winning index <c>ChooseSpawnIndex</c> already resolved — never during selection
        /// itself, which <c>SpawnPointSelectionTests.ChoosingAPointDoesNotAskAnyPointForItsPosition</c>
        /// pins. Advancing here, and only here, means a rotation moves exactly once per body
        /// actually placed — not once per candidate sampled, and not once per frame the pin
        /// happens to be asked whether it is still pinned.
        /// </para>
        /// <para>
        /// <b>Which team's cursor moves is read back off the value itself.</b> This method takes
        /// an index, not a team — <see cref="ISpawnPointDirectory"/> does not carry one — so the
        /// team advanced is whichever one's CURRENT slot equals <paramref name="index"/>. That is
        /// unambiguous in the ordinary case: <paramref name="index"/> only ever arrives here as
        /// the return of <c>ChooseSpawnIndex(this, team)</c>, and eligibility already requires
        /// <c>index == PinnedIndexFor(team)</c>, so it names that team's current slot and no
        /// other team's — <b>unless</b> two teams' rotations happen to sit on the very same
        /// index at once (both -1-owned, both authored to visit it on the same placement), in
        /// which case the first matching team advances and the rest stand still for this call.
        /// That tie is a rare, self-inflicted authoring choice rather than a hazard this class
        /// papers over.
        /// </para>
        /// </remarks>
        public Vector3 GetSpawnPosition(int index)
        {
            AdvanceRotationOf(index);
            return _inner.GetSpawnPosition(index);
        }

        private void AdvanceRotationOf(int index)
        {
            for (int team = 0; team < _rotationByTeam.Length; team++)
            {
                if (PinnedIndexFor(team) != index) continue;

                _cursorByTeam[team]++;
                return;
            }
        }
    }
}
