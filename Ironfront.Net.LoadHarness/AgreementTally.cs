using System;
using System.Globalization;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>
    /// Sorts every cross-client entity comparison into divergence, staleness, or neither,
    /// and splits the divergences by shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Extracted from <c>Program.CompareClients</c> so it can be tested without a run.</b>
    /// Two defects lived in the old inline version — it could not tell divergence from
    /// staleness (X-35) and it could not tell a quantizer edge from real disagreement
    /// (X-40) — and both were only visible in a 120 s eight-client capture. A counter that
    /// decides a verdict has to be reachable by a unit test.
    /// </para>
    /// <para>
    /// <b>The update-tick rule, stated once.</b> Two entries that differ are DIVERGENT only
    /// when both were last updated at the same server tick: the server then told two
    /// clients different things about one entity at one moment. Different update ticks mean
    /// one client holds a newer copy, which is interest management working. Equal values
    /// are not classified at all, whatever their ticks.
    /// </para>
    /// </remarks>
public sealed class AgreementTally
    {
        public int Pairs;
        public int Ticks;

        private int _entities;
        private int _sameTick;
        private int _divergences;
        private int _stale;
        private int _unclassified;
        private int _oneUnitOneAxis;
        private int _substantive;
        private string? _firstDivergence;
        private string? _firstSubstantive;
        private string? _firstStale;

        public void Classify(
            uint tick, string kind, ushort id, int otherClient,
            short mineX, short mineY, short mineZ, uint mineUpdatedAt,
            short theirsX, short theirsY, short theirsZ, uint theirsUpdatedAt)
        {
            _entities++;

            bool known = mineUpdatedAt != 0 && theirsUpdatedAt != 0;
            bool sameTick = mineUpdatedAt == theirsUpdatedAt;
            if (sameTick && known) _sameTick++;

            if (mineX == theirsX && mineY == theirsY && mineZ == theirsZ) return;

            string where = string.Format(
                CultureInfo.InvariantCulture,
                "tick {0} {1} {2}: client 0 ({3},{4},{5}) updated at {6} vs client {7} "
                + "({8},{9},{10}) updated at {11}",
                tick, kind, id, mineX, mineY, mineZ, mineUpdatedAt,
                otherClient, theirsX, theirsY, theirsZ, theirsUpdatedAt);

            if (!known)
            {
                // Unreachable if the provenance tracking is right, so it is surfaced rather
                // than absorbed into whichever counter happens to sit nearby.
                _unclassified++;
                return;
            }

            if (!sameTick)
            {
                _stale++;
                _firstStale ??= where;
                return;
            }

            _divergences++;
            _firstDivergence ??= where;

            if (IsOneUnitOnOneAxis(mineX, mineY, mineZ, theirsX, theirsY, theirsZ))
            {
                _oneUnitOneAxis++;
                return;
            }

            _substantive++;
            _firstSubstantive ??= where;
        }

        /// <summary>
        /// Whether two positions differ by exactly one quantizer step on exactly one axis.
        /// </summary>
        /// <remarks>
        /// The benign shape in X-40, and what a rounding boundary looks like when two
        /// encodes of nearly the same float land either side of it. Deliberately exact
        /// rather than a tolerance: "within a few units" would absorb the very population
        /// this split exists to isolate.
        /// </remarks>
        private static bool IsOneUnitOnOneAxis(
            short aX, short aY, short aZ, short bX, short bY, short bZ)
        {
            int dx = Math.Abs(aX - bX);
            int dy = Math.Abs(aY - bY);
            int dz = Math.Abs(aZ - bZ);
            int axesDiffering = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
            return axesDiffering == 1 && dx <= 1 && dy <= 1 && dz <= 1;
        }

        public HarnessReport.AgreementBlock ToBlock()
            => new HarnessReport.AgreementBlock
            {
                ClientPairsCompared = Pairs,
                TicksCompared = Ticks,
                EntitiesCompared = _entities,
                SameTickComparisons = _sameTick,
                Divergences = _divergences,
                StaleComparisons = _stale,
                UnclassifiedComparisons = _unclassified,
                DivergencesOneUnitOneAxis = _oneUnitOneAxis,
                DivergencesSubstantive = _substantive,
                FirstDivergence = _firstDivergence,
                FirstSubstantiveDivergence = _firstSubstantive,
                FirstStale = _firstStale,
            };
    }
}
