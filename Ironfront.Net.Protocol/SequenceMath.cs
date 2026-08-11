namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Wrap-around-safe comparison for the 16-bit sequence numbers used by the GSP
    /// header and by every sequenced channel. Mirrors protocol-spec.md section 2.3.
    /// </summary>
    /// <remarks>
    /// Writing <c>if (seq &gt; lastSeq)</c> works perfectly for ~36 minutes at 30 Hz and
    /// then breaks when the counter wraps 65535 -> 0. Every sequence comparison in the
    /// solution must go through this class; a raw <c>&gt;</c> on a sequence number is a
    /// review-blocking defect (see dev-b-transport/plan.md risk B2).
    /// </remarks>
    public static class SequenceMath
    {
        private const ushort HALF = 32768;

        /// <summary>
        /// True when <paramref name="a"/> is more recent than <paramref name="b"/>,
        /// accounting for wrap-around. Returns false when the two are equal.
        /// </summary>
        public static bool IsNewer(ushort a, ushort b)
        {
            return (a > b && a - b <= HALF) || (b > a && b - a > HALF);
        }

        /// <summary>
        /// Signed distance from <paramref name="b"/> to <paramref name="a"/>, correct
        /// across the wrap boundary. Positive when <paramref name="a"/> is newer.
        /// </summary>
        public static int Distance(ushort a, ushort b) => (short)(a - b);

        /// <summary>
        /// True when <paramref name="a"/> is newer than or equal to <paramref name="b"/>.
        /// Convenience for "have I already processed this?" checks.
        /// </summary>
        public static bool IsNewerOrEqual(ushort a, ushort b) => a == b || IsNewer(a, b);
    }
}
