using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// An <see cref="ISpawnPointDirectory"/> that reports exactly one slot eligible, so that
    /// <c>ServerCombatBridge.ChooseSpawnIndex</c> has nothing left to sample between.
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
    /// <b>Every player spawns on the same point</b> when one index is pinned, which is the
    /// intent: adjacent bodies make the 95 s approach in <c>tools/lane-b/combat-driver.json</c>
    /// a formality rather than the thing the run is really testing.
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
    /// remark used to read "On Dustbowl EVERY spawn point is team-owned, so <b>any</b> single
    /// pinned index starves one side". That is false, and it was false for as long as it stood
    /// (P19 § 1.2). The spawn points ARE the capture points — <c>CapturePoint : SpawnPoint</c>
    /// is the only subclass and <c>ActorManager.spawnPoints</c> is
    /// <c>FindObjectsOfType&lt;SpawnPoint&gt;()</c> — and their authored owners are:
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
    /// <c>IsEligible</c> is <c>point.owner &lt; 0 || point.owner == team</c>
    /// (<c>IronfrontNetBindings.cs:583-589</c>): <c>owner &lt; 0</c> means "any team", so a pin
    /// on one of those four Dustbowl indices or three Island indices <b>starves nobody</b>. The
    /// hazard is real and it is narrow — 2 of 6 indices, not 6 of 6 — and the refusal below
    /// still catches it, because it asks <c>IsEligible</c> rather than counting anything.
    /// <b>Nothing here was re-tuned on the strength of the corrected count</b>: the per-team pin
    /// and the construction-time refusal are both right whether the hazardous fraction is a
    /// third or all of them.
    /// </para>
    /// <para>
    /// <b>A pin per team, and a refusal at construction.</b> The option still exists because
    /// what X-22 needed it for has not gone away; it now takes one index per team, so each side
    /// gets a slot it may actually use. And the starvation is detected when the directory is
    /// BUILT rather than when an actor fails to spawn ninety seconds in — a run that voids
    /// itself at the top costs a minute, and one that voids itself in the middle costs the whole
    /// run plus the reading of it.
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
        private readonly int[] _pinnedByTeam;

        /// <param name="inner">The real directory. Eligibility and positions still come from it.</param>
        /// <param name="index">The slot pinned for every team.</param>
        public PinnedSpawnPointDirectory(ISpawnPointDirectory inner, int index)
            : this(inner, new[] { index, index })
        {
        }

        /// <param name="inner">The real directory. Eligibility and positions still come from it.</param>
        /// <param name="pinnedByTeam">
        /// One slot per team, indexed by team number. X-63: a single slot shared by both teams
        /// cannot work on a map whose every spawn point is team-owned.
        /// </param>
        public PinnedSpawnPointDirectory(ISpawnPointDirectory inner, int[] pinnedByTeam)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (pinnedByTeam == null || pinnedByTeam.Length == 0)
                throw new ArgumentException("at least one team's slot is required", nameof(pinnedByTeam));

            for (int team = 0; team < pinnedByTeam.Length; team++)
            {
                if (pinnedByTeam[team] >= 0) continue;

                throw new ArgumentOutOfRangeException(
                    nameof(pinnedByTeam), pinnedByTeam[team],
                    $"team {team}'s pinned spawn index is a slot, never the -1 that "
                    + "ChooseSpawnIndex returns for 'no eligible point'");
            }

            _inner = inner;
            _pinnedByTeam = pinnedByTeam;

            RefuseIfAnyTeamIsStarved();
        }

        /// <summary>
        /// Throws when a team's pinned slot is not one that team may spawn on.
        /// </summary>
        /// <remarks>
        /// <b>X-63, and the whole point of doing it here.</b> Without this the starvation shows
        /// up as one actor silently never placed, minutes into a run, in a warning inside a
        /// server log nobody reads until the artifact turns out to be ungradeable.
        /// <b>2 of Dustbowl's 6 spawn points and 2 of Island's 5 are team-owned</b> (the class
        /// remark lists them), so pinning one index starves a side on a third of the shipping
        /// indices rather than on all of them. This asks <c>IsEligible</c> rather than counting,
        /// so the refusal is exactly as correct at 2-of-6 as the old remark assumed it was at
        /// 6-of-6.
        /// </remarks>
        private void RefuseIfAnyTeamIsStarved()
        {
            for (int team = 0; team < _pinnedByTeam.Length; team++)
            {
                int slot = _pinnedByTeam[team];
                if (_inner.IsEligible(slot, team)) continue;

                throw new ArgumentException(
                    $"spawn slot {slot} is not eligible for team {team}, so that team would "
                    + "never be placed and the run would grade nothing. Two of Dustbowl's six "
                    + "spawn points and two of Island's five name a team; the rest are owner -1 "
                    + "and eligible for both (ledger X-63). Pass one slot per team, e.g. "
                    + "IRONFRONT_LANEB_SPAWN_INDEX=3,7, or pin an owner -1 index.",
                    nameof(_pinnedByTeam));
            }
        }

        /// <summary>Team 0's slot. Kept for callers and logs that pin one index for both.</summary>
        public int PinnedIndex => _pinnedByTeam[0];

        /// <summary>The slot pinned for <paramref name="team"/>, or -1 for an unknown team.</summary>
        public int PinnedIndexFor(int team)
            => team >= 0 && team < _pinnedByTeam.Length ? _pinnedByTeam[team] : -1;

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
        public Vector3 GetSpawnPosition(int index) => _inner.GetSpawnPosition(index);
    }
}
