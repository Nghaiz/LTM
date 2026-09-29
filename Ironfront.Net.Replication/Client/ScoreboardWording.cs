using System.Globalization;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What the Tab scoreboard says: the clock, the phase, who leads and by how much, and the
    /// rules. Playtest 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// Engine-free so every string is tested here rather than read off a screenshot, and so the
    /// scoreboard and any other screen that states the match agree word for word.
    /// </remarks>
    public static class ScoreboardWording
    {
        private const string Separator = "  ·  ";

        /// <summary>The name a side goes by: team 0 is "TEAM 1", as in the room lobby.</summary>
        public static string TeamName(byte team)
            => team == TeamId.Team0 ? "TEAM 1"
             : team == TeamId.Team1 ? "TEAM 2"
             : string.Empty;

        /// <summary>"14:32"; empty for a negative count, which means "no clock this phase".</summary>
        public static string Clock(int seconds)
            => seconds < 0
                ? string.Empty
                : (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":"
                  + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);

        /// <summary>The words under the clock.</summary>
        public static string PhaseLabel(MatchPhase phase, bool hasTimer)
        {
            switch (phase)
            {
                case MatchPhase.WaitingForPlayers: return "WAITING FOR PLAYERS";
                case MatchPhase.Warmup:            return hasTimer ? "STARTS IN" : "WARMUP";
                case MatchPhase.Playing:           return hasTimer ? "TIME LEFT" : "LIVE";
                case MatchPhase.Ended:             return hasTimer ? "NEXT ROUND IN" : "ROUND OVER";
                case MatchPhase.Resetting:         return "NEXT ROUND";
                default:                           return string.Empty;
            }
        }

        /// <summary>
        /// Who leads and what winning still takes, or who won. Victory is a MARGIN, so the line
        /// counts the margin rather than a total.
        /// </summary>
        public static string LeadLine(
            MatchPhase phase, int score0, int score1, int victoryMargin, byte winningTeam)
        {
            if (phase == MatchPhase.Ended || phase == MatchPhase.Resetting)
                return winningTeam == TeamId.None ? "DRAW" : TeamName(winningTeam) + " WINS";

            string toWin = "LEAD BY " + victoryMargin.ToString(CultureInfo.InvariantCulture) + " TO WIN";

            if (phase != MatchPhase.Playing || score0 == score1)
                return (phase == MatchPhase.Playing ? "LEVEL" + Separator : string.Empty) + toWin;

            byte leader = score0 > score1 ? TeamId.Team0 : TeamId.Team1;
            int lead = score0 > score1 ? score0 - score1 : score1 - score0;
            string leads = TeamName(leader) + " LEADS BY " + lead.ToString(CultureInfo.InvariantCulture);

            int more = victoryMargin - lead;
            return more > 0
                ? leads + Separator + more.ToString(CultureInfo.InvariantCulture) + " MORE TO WIN"
                : leads;
        }

        /// <summary>
        /// How far the lead has gone towards the margin, from -1 (team 0 has it) to +1 (team 1).
        /// The scoreboard's tug-of-war bar is drawn from this.
        /// </summary>
        public static float Lead(int score0, int score1, int victoryMargin)
        {
            if (victoryMargin <= 0) return score0 == score1 ? 0f : score0 > score1 ? -1f : 1f;

            float lead = (score1 - score0) / (float)victoryMargin;
            return lead < -1f ? -1f : lead > 1f ? 1f : lead;
        }

        /// <summary>Kills per death to two places; kills alone while a player has not died.</summary>
        public static string Ratio(int kills, int deaths)
            => (deaths > 0 ? kills / (float)deaths : kills).ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>"12 PLAYERS  ·  2 HUMANS".</summary>
        public static string PlayersLine(int players, int humans)
            => Count(players, "PLAYER") + Separator + Count(humans, "HUMAN");

        /// <summary>A team's totals, "145 KILLS  ·  132 DEATHS".</summary>
        public static string TotalsLine(int kills, int deaths)
            => Count(kills, "KILL") + Separator + Count(deaths, "DEATH");

        /// <summary>The rules, in one line, for the foot of the board.</summary>
        /// <remarks>
        /// Numbers only where the client knows them: the margin travels in
        /// <c>S_MATCH_STATE</c>; the territory interval is the server's and does not, so the line
        /// says what holding ground does without claiming how often.
        /// </remarks>
        public static string Rules(int victoryMargin)
            => "LEAD BY " + victoryMargin.ToString(CultureInfo.InvariantCulture) + " POINTS TO WIN"
               + Separator + "KILLS SCORE MORE FOR EVERY FLAG YOU HOLD"
               + Separator + "HOLDING MORE FLAGS SCORES OVER TIME";

        private static string Count(int value, string noun)
            => value.ToString(CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? string.Empty : "S");
    }
}
