using System;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Which killfeed row draws which line, so each kill keeps its row as newer kills push it
    /// down. Playtest 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why rows follow kills.</b> A drawing layer that fills row N with line N re-types all
    /// five rows on every kill, and anything it animates plays again for kills that happened
    /// seconds ago. Keyed on <see cref="KillfeedEntry.Sequence"/> instead, a row slides in once,
    /// glides down as newer kills arrive and fades out when its kill leaves the model.
    /// </para>
    /// <para>
    /// <b>The rules.</b> A line whose kill a row already shows keeps that row. A new line takes
    /// the cheapest row still unclaimed: free first, then the faintest one fading out, and --
    /// when the feed was full -- the row of the kill the model just pushed out, which is leaving
    /// anyway. A row no line keeps leaves. Lines are matched before any new line takes a row, so
    /// the newest kill, which arrives first, cannot take a row an older line still owns.
    /// </para>
    /// <para>Engine-free, so the rules are tested here rather than read off a screenshot.</para>
    /// </remarks>
    public static class KillfeedRowAssignment
    {
        /// <param name="rowSequences">
        /// The kill each row shows: 0 for a free row, negative for a row that cannot be used.
        /// </param>
        /// <param name="rowReuseCost">
        /// How costly each row is to hand to a new kill; lower is taken first.
        /// </param>
        /// <param name="lineSequences">The pushed lines' kills, newest first; 0 for a line not sent.</param>
        /// <param name="lineRows">Out: the row each line is drawn in, or -1 when none was left.</param>
        /// <param name="rowKept">Out: whether each row draws a pushed line. A row not kept leaves.</param>
        public static void Assign(
            ReadOnlySpan<long> rowSequences, ReadOnlySpan<float> rowReuseCost,
            ReadOnlySpan<long> lineSequences, Span<int> lineRows, Span<bool> rowKept)
        {
            if (rowReuseCost.Length != rowSequences.Length || rowKept.Length != rowSequences.Length)
                throw new ArgumentException("One cost and one kept flag per row.");
            if (lineRows.Length != lineSequences.Length)
                throw new ArgumentException("One row per line.");

            rowKept.Clear();

            // A kill already on screen keeps its row.
            for (int line = 0; line < lineSequences.Length; line++)
            {
                lineRows[line] = -1;
                long sequence = lineSequences[line];
                if (sequence <= 0) continue;

                for (int row = 0; row < rowSequences.Length; row++)
                {
                    if (rowKept[row] || rowSequences[row] != sequence) continue;

                    rowKept[row] = true;
                    lineRows[line] = row;
                    break;
                }
            }

            // A new kill takes the cheapest row nobody kept.
            for (int line = 0; line < lineSequences.Length; line++)
            {
                if (lineSequences[line] <= 0 || lineRows[line] >= 0) continue;

                int best = -1;
                for (int row = 0; row < rowSequences.Length; row++)
                {
                    if (rowKept[row] || rowSequences[row] < 0) continue;
                    if (best < 0 || rowReuseCost[row] < rowReuseCost[best]) best = row;
                }

                if (best < 0) continue;

                rowKept[best] = true;
                lineRows[line] = best;
            }
        }
    }
}
