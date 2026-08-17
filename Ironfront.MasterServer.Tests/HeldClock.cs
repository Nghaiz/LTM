using System;
using System.Threading;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A <see cref="TimeProvider"/> that only moves when a test moves it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand this to <c>TcpListenerHostOptions.Clock</c> and the server's timeout sweep measures
    /// against it instead of the wall clock. That turns "wait and hope the runner keeps up" into
    /// a statement: nothing expires until <see cref="Advance"/> says so, and once it does, the
    /// next sweep sees it no matter how long the runner took to get there.
    /// </para>
    /// <para>
    /// This is what the timing tests were missing. They shrank 30 s deadlines to ~200 ms and
    /// paced themselves with <c>Task.Delay</c>, so a stalled thread-pool continuation on a
    /// shared windows-latest runner would let the server's clock run past a deadline the test
    /// believed it was inside. Two of them failed that way on unrelated dependency PRs (#90,
    /// #91) and passed on a plain re-run, and one of them had already been widened once
    /// (300 ms, then 1500 ms) without fixing it. There is no window wide enough, because the
    /// stall has no upper bound.
    /// </para>
    /// <para>
    /// The origin is deliberately not zero. Real monotonic clocks start at boot, so a test that
    /// accidentally depends on "time began at 0" should fail here rather than pass by luck.
    /// </para>
    /// </remarks>
    internal sealed class HeldClock : TimeProvider
    {
        private long _ms = 1_000_000;

        /// <summary>One tick is one millisecond, so a timestamp reads directly as ms.</summary>
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _ms);

        /// <summary>
        /// The current reading, in the same milliseconds the server stamps
        /// <c>ClientConnection.LastActivityMs</c> and <c>ConnectedAtMs</c> with — so a test can
        /// wait for the server to have observed a specific moment.
        /// </summary>
        public long NowMs => GetTimestamp();

        /// <summary>
        /// Moves the clock forward. Safe to call while the host's logic thread is running: the
        /// sweep reads the timestamp atomically, so it sees the value before or after this call
        /// and never a torn one.
        /// </summary>
        public void Advance(TimeSpan by)
        {
            long ms = (long)by.TotalMilliseconds;
            if (ms < 0) throw new ArgumentOutOfRangeException(nameof(by), "a monotonic clock cannot go backwards");
            Interlocked.Add(ref _ms, ms);
        }
    }
}
