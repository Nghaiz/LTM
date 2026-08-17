using System;

namespace Ironfront.MasterServer.Net
{
    /// <summary>
    /// Reads a <see cref="TimeProvider"/> as monotonic milliseconds — the shape every timeout
    /// in this namespace is written against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timeout code used <see cref="Environment.TickCount64"/> directly, which is correct
    /// in production and untestable outside it: the only way to prove a 30 s deadline fires is
    /// to wait 30 s, so the tests shrank the deadline to ~200 ms instead and raced the runner.
    /// That race is not winnable by picking better numbers. A shared CI runner stalls a
    /// thread-pool continuation for an unbounded time, and the server's clock keeps moving
    /// through the stall, so the test's pacing and the server's deadline drift apart by an
    /// amount nobody gets to choose. Widening the window makes the failure rarer and hides the
    /// direction it fails in.
    /// </para>
    /// <para>
    /// Behind a provider the tests hold the clock still and step it themselves, and a stall
    /// stops being able to move a deadline at all. Production passes
    /// <see cref="TimeProvider.System"/> and is bit-for-bit what it was.
    /// </para>
    /// </remarks>
    internal static class MonotonicClock
    {
        /// <summary>
        /// Milliseconds on <paramref name="clock"/>'s monotonic timeline. Only ever meaningful
        /// as a difference against another reading of the SAME provider — the origin is
        /// arbitrary (boot, for the system provider) and comparing across providers is
        /// nonsense.
        /// </summary>
        /// <remarks>
        /// Conversion goes through <see cref="TimeProvider.GetElapsedTime(long, long)"/> rather
        /// than hand-rolled <c>ticks * 1000 / frequency</c> arithmetic, which overflows a
        /// <see cref="long"/> on a high-frequency counter and a long-running host.
        /// </remarks>
        public static long NowMs(this TimeProvider clock)
            => (long)clock.GetElapsedTime(0, clock.GetTimestamp()).TotalMilliseconds;
    }
}
