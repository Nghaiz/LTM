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
    /// review-blocking defect (see transport/plan.md risk B2).
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

        private const uint HALF32 = 2147483648; // 2^31

        /// <summary>
        /// The 32-bit form, for tick counters rather than packet sequences: serverTick,
        /// baselineTick and lastProcessedInputTick are all u32 (protocol-spec.md sections 4.2
        /// and 4.3).
        /// </summary>
        /// <remarks>
        /// A u32 tick counter at 30 Hz takes 4.5 years to wrap, so this looks like paranoia
        /// next to the 36-minute u16 case. It is not: a client that connects mid-match starts
        /// comparing against whatever tick the server is on, and any code path that subtracts
        /// ticks unsigned turns a one-tick-behind comparison into a 4-billion-tick-ahead one.
        /// Routing every tick comparison through here means that class of bug cannot happen,
        /// at zero cost.
        /// </remarks>
        public static bool IsNewer32(uint a, uint b)
        {
            return (a > b && a - b <= HALF32) || (b > a && b - a > HALF32);
        }

        /// <summary>
        /// Signed distance from <paramref name="b"/> to <paramref name="a"/> for 32-bit
        /// ticks, correct across the wrap boundary. Positive when <paramref name="a"/> is
        /// newer.
        /// </summary>
        public static int Distance32(uint a, uint b) => unchecked((int)(a - b));

        /// <summary>32-bit companion to <see cref="IsNewerOrEqual(ushort, ushort)"/>.</summary>
        public static bool IsNewerOrEqual32(uint a, uint b) => a == b || IsNewer32(a, b);
    }
}
