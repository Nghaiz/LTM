namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Decides what <see cref="NetPredictionClock"/> says when a long frame makes it drop ticks: a
    /// warning while the window has focus, and one summary line for everything dropped while it
    /// did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In the background a long frame is the operating system's doing, not a hitch.</b> Windows
    /// throttles and occludes unfocused windows, so frames there run hundreds of milliseconds long
    /// and the clock drops ticks on most of them -- by design, since nobody is playing. Each drop
    /// used to log "Raise MaxTicksPerFrame only if this is routine", a warning that read as a
    /// performance fault in every log of a game left behind another window (playtest 2026-10-03,
    /// item 3d).
    /// </para>
    /// <para>
    /// <b>Counted, not hidden.</b> The drops are still totalled and reported, once, when focus
    /// returns, so a log still shows that the clock skipped time and how much.
    /// </para>
    /// </remarks>
    public sealed class TickDropReport
    {
        private int _backgroundTicks;
        private int _backgroundFrames;
        private float _backgroundLongestMs;

        /// <summary>
        /// Records one drop and returns the warning to log now, or null when it is deferred to the
        /// summary because the window is in the background.
        /// </summary>
        public string Record(int droppedTicks, float frameMs, bool focused)
        {
            if (focused)
            {
                return $"[NetPredictionClock] dropped {droppedTicks} tick(s) after a {frameMs:F0} ms "
                       + "frame. Raise MaxTicksPerFrame only if this is routine.";
            }

            _backgroundTicks += droppedTicks;
            _backgroundFrames++;
            if (frameMs > _backgroundLongestMs) _backgroundLongestMs = frameMs;
            return null;
        }

        /// <summary>
        /// The summary of what was dropped in the background, once focus is back, or null when
        /// nothing was or the window is still unfocused. Resets the count.
        /// </summary>
        public string TakeBackgroundSummary(bool focused)
        {
            if (!focused || _backgroundFrames == 0) return null;

            string line = $"[NetPredictionClock] dropped {_backgroundTicks} tick(s) over "
                          + $"{_backgroundFrames} long frame(s) (longest {_backgroundLongestMs:F0} ms) "
                          + "while the window was in the background; expected, the system throttles "
                          + "unfocused windows.";
            _backgroundTicks = 0;
            _backgroundFrames = 0;
            _backgroundLongestMs = 0f;
            return line;
        }
    }
}
