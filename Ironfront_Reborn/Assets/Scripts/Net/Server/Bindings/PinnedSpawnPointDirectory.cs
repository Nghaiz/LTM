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
    /// <b>The failure this can cause, and who reports it.</b> Eligibility is still the inner
    /// directory's answer, so pinning a point whose <c>SpawnPoint.owner</c> names one team hands
    /// the other team no eligible slot at all. That returns -1 from <c>ChooseSpawnIndex</c> and
    /// trips the existing "no eligible spawn point" warning in <c>MoveToSpawnPoint</c> — the one
    /// added after X-12 cost two investigations. This class deliberately does not paper over it
    /// with a fallback to sampling: a run that quietly stopped being deterministic is exactly
    /// the thing X-22 is about.
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

        /// <param name="inner">The real directory. Eligibility and positions still come from it.</param>
        /// <param name="index">The only slot this directory will report eligible.</param>
        public PinnedSpawnPointDirectory(ISpawnPointDirectory inner, int index)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "a pinned spawn index is a slot, never the -1 that "
                    + "ChooseSpawnIndex returns for 'no eligible point'");
            }

            _inner = inner;
            PinnedIndex = index;
        }

        /// <summary>The slot this directory pins. Reported so the harness can log it.</summary>
        public int PinnedIndex { get; }

        /// <inheritdoc />
        public int Count => _inner.Count;

        /// <inheritdoc />
        /// <remarks>
        /// Both halves matter. The index test is what removes the sampling; the inner call is
        /// what keeps the team rule, so this narrows the choice without ever widening it.
        /// </remarks>
        public bool IsEligible(int index, int team)
            => index == PinnedIndex && _inner.IsEligible(index, team);

        /// <inheritdoc />
        public Vector3 GetSpawnPosition(int index) => _inner.GetSpawnPosition(index);
    }
}
