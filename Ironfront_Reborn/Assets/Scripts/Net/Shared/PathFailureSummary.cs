using System.Collections.Generic;
using System.Text;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Counts the A* searches that fail and reports them as one line a minute, by reason, instead
    /// of one error each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failed search is routine for a bot and costs it nothing.</b> A goal with no navmesh node
    /// in the bot's own area ("Couldn't find a close node to the end point"), a bot standing where
    /// no node is near ("... to the start point"): the bot's move timeout starts and it asks again
    /// (phase P29). The scenes' <c>AstarPath</c> printed every one as "Path Failed" and
    /// <c>AiActorController.OnPathComplete</c> printed it again as an error, so a Forest Lake match
    /// logged dozens of error lines a player never sees a consequence of (playtest 2026-10-03,
    /// item 3f; v3.1.1 Forest Lake server log: 30 close-node and 10 "Path Failed" lines).
    /// </para>
    /// <para>
    /// <b>Counted, not hidden.</b> The reasons and their counts still reach the log, once a minute,
    /// so a map whose bots cannot path anywhere still reads as such.
    /// </para>
    /// <para>
    /// A search the seeker abandoned for a newer one ("Canceled path because a new one was
    /// requested") is not a failure and is not counted at all.
    /// </para>
    /// </remarks>
    public sealed class PathFailureSummary
    {
        /// <summary>How long one summary covers.</summary>
        public const float WindowSeconds = 60f;

        private readonly object _gate = new object();
        private readonly Dictionary<string, int> _byReason = new Dictionary<string, int>();
        private float _windowStart = float.NaN;
        private int _total;

        /// <summary>
        /// Counts one failed search. <paramref name="errorLog"/> is A*'s own message; its first
        /// line is the reason.
        /// </summary>
        /// <remarks>
        /// <b>Called on A*'s worker thread</b> (<c>AstarPath.CalculatePathsThreaded</c>), so it takes
        /// a lock and touches no Unity API. A first version stamped the time here with
        /// <c>Time.realtimeSinceStartup</c>, which throws off the main thread: the exception ended
        /// the pathfinding thread and no bot received a path again. The window is timed on the main
        /// thread, by <see cref="TakeDueSummary"/>.
        /// </remarks>
        public void Record(string errorLog)
        {
            string reason = ReasonOf(errorLog);
            lock (_gate)
            {
                _byReason.TryGetValue(reason, out int count);
                _byReason[reason] = count + 1;
                _total++;
            }
        }

        /// <summary>
        /// The summary of a window that has run its course, or null while it has not or when it
        /// counted nothing. Main thread; <paramref name="now"/> is its clock. The first call opens
        /// the window, and a window that ends with something to say starts the next.
        /// </summary>
        public string TakeDueSummary(float now)
        {
            lock (_gate)
            {
                if (float.IsNaN(_windowStart)) _windowStart = now;
                if (now - _windowStart < WindowSeconds) return null;
                if (_total == 0)
                {
                    _windowStart = now;
                    return null;
                }

                var line = new StringBuilder();
                line.Append("[ai] ").Append(_total).Append(" bot path search(es) failed in the last ")
                    .Append((int)(now - _windowStart)).Append(" s, each retried:");

                bool first = true;
                foreach (KeyValuePair<string, int> pair in _byReason)
                {
                    line.Append(first ? " " : "; ").Append(pair.Value).Append(" x \"").Append(pair.Key).Append('"');
                    first = false;
                }

                _byReason.Clear();
                _total = 0;
                _windowStart = now;
                return line.ToString();
            }
        }

        /// <summary>The first line of A*'s message, without "Error: " or a trailing full stop.</summary>
        public static string ReasonOf(string errorLog)
        {
            if (string.IsNullOrEmpty(errorLog)) return "unknown";

            string text = errorLog.Trim();
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text.Substring(0, newline).Trim();
            if (text.StartsWith("Error:")) text = text.Substring("Error:".Length).Trim();
            return text.TrimEnd('.');
        }

        /// <summary>Whether A*'s message says the seeker dropped the search for a newer one.</summary>
        public static bool IsCancellation(string errorLog) =>
            errorLog != null && errorLog.IndexOf("Canceled path", System.StringComparison.Ordinal) >= 0;
    }
}
