namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// A running total plus a per-minute rate, for the <c>rates</c> block of the metrics
    /// endpoint and the alert thresholds that read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PerMinute"/> reports the <b>last completed</b> 60-second window, not an
    /// extrapolation of the current one. Extrapolating is what makes an alert scream: three
    /// errors two seconds into a window extrapolates to 90/min and trips a
    /// "more than 10 errors a minute" rule that nothing actually violated. A number that lags
    /// by up to a minute is the right trade for a number nobody learns to ignore.
    /// </para>
    /// <para>
    /// Logic thread only — <see cref="Increment"/> is called from dispatch and
    /// <see cref="Advance"/> from the tick, both of which run on the single logic thread
    /// (D-AD-1), so there is nothing to synchronise.
    /// </para>
    /// </remarks>
    public sealed class RateCounter
    {
        private const long WindowMs = 60_000;

        private long _windowStartMs;
        private long _currentWindow;
        private bool _started;

        /// <summary>Events counted since the process started.</summary>
        public long Total { get; private set; }

        /// <summary>
        /// Events during the last completed 60-second window. Zero until one has elapsed,
        /// which is honest: no minute has finished yet.
        /// </summary>
        public long PerMinute { get; private set; }

        /// <summary>Counts one event.</summary>
        public void Increment()
        {
            Total++;
            _currentWindow++;
        }

        /// <summary>
        /// Rolls the window if 60 seconds have passed. Safe to call at tick frequency.
        /// </summary>
        public void Advance(long nowUnixMs)
        {
            if (!_started)
            {
                _windowStartMs = nowUnixMs;
                _started = true;
                return;
            }

            if (nowUnixMs - _windowStartMs < WindowMs) return;

            // A single assignment rather than a loop over skipped windows: if the process was
            // frozen for ten minutes, the count belongs to the whole stall, and pretending
            // nine empty minutes elapsed would report a suspiciously calm system immediately
            // after the least calm thing that has happened to it.
            PerMinute = _currentWindow;
            _currentWindow = 0;
            _windowStartMs = nowUnixMs;
        }
    }
}
