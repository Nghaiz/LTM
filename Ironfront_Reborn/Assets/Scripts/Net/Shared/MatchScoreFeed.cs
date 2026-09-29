#nullable enable

using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The networked match's live score, for code that cannot reach the server assembly: the bot
    /// commander, which plays to the score (phase P28, part 4).
    /// </summary>
    /// <remarks>
    /// <b>Why it exists.</b> <c>BotCommander</c> read <c>MatchScoreboard</c>, the OFFLINE match's
    /// scoreboard, which a dedicated server never writes: a networked match is scored by
    /// <c>MatchStateMachine</c> in <c>MatchController</c>, an assembly Assembly-CSharp may not
    /// name. So on the server both scores read 0 and the commander's score-driven posture never
    /// moved. The match controller installs its reader here; Shared is the declared channel
    /// between the two, as <see cref="ICapturePointDirectory"/> is.
    /// </remarks>
    public static class MatchScoreFeed
    {
        private static Func<int, int>? _scoreOf;

        /// <summary>Installed by the server's match controller for the life of its match.</summary>
        public static void Install(Func<int, int> scoreOf) => _scoreOf = scoreOf;

        /// <summary>Removed by the controller that installed it, and only by that one.</summary>
        public static void Clear(Func<int, int> scoreOf)
        {
            if (_scoreOf == scoreOf) _scoreOf = null;
        }

        /// <summary><paramref name="team"/>'s score in the networked match running here, if there is one.</summary>
        public static bool TryScore(int team, out int score)
        {
            Func<int, int>? scoreOf = _scoreOf;
            if (scoreOf == null)
            {
                score = 0;
                return false;
            }
            score = scoreOf(team);
            return true;
        }
    }
}
