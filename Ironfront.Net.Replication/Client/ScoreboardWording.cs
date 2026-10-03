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
            => team == TeamId.Team0 ? "BLUE TEAM"
             : team == TeamId.Team1 ? "RED TEAM"
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
                case MatchPhase.Playing:           return "LIVE";   // a round has no clock (owner ruling 2026-09-29)
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
        /// <see cref="LeadLine(MatchPhase, int, int, int, byte)"/> under the room's rule (protocol
        /// 14, phase P32). First to the points counts what the leader still needs to reach them.
        /// </summary>
        public static string LeadLine(
            MatchPhase phase, int score0, int score1, int victoryPoints, byte winningTeam, VictoryRule rule)
        {
            if (rule != VictoryRule.Target) return LeadLine(phase, score0, score1, victoryPoints, winningTeam);

            if (phase == MatchPhase.Ended || phase == MatchPhase.Resetting)
                return winningTeam == TeamId.None ? "DRAW" : TeamName(winningTeam) + " WINS";

            string toWin = "FIRST TO " + victoryPoints.ToString(CultureInfo.InvariantCulture) + " WINS";
            if (phase != MatchPhase.Playing || score0 == score1)
                return (phase == MatchPhase.Playing ? "LEVEL" + Separator : string.Empty) + toWin;

            byte leader = score0 > score1 ? TeamId.Team0 : TeamId.Team1;
            int top = score0 > score1 ? score0 : score1;
            int more = victoryPoints - top;
            return TeamName(leader) + " LEADS" + Separator
                   + (more > 0 ? more.ToString(CultureInfo.InvariantCulture) + " MORE TO WIN" : toWin);
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

        /// <summary>The mode a round is played in, as the board names it.</summary>
        /// <remarks>
        /// Point Match is the game's one mode today (owner, 2026-09-29): a death scores for the other
        /// side, times the flags it holds. One constant, so another mode is a new value here rather
        /// than a string in every screen that names it.
        /// </remarks>
        public const string PointMatch = "POINT MATCH";

        /// <summary>Night Mode, Forest Lake only (protocol 14, phase P32).</summary>
        public const string NightMode = "NIGHT MODE";

        /// <summary>The name the board gives a room's mode.</summary>
        public static string ModeName(GameMode mode) => mode == GameMode.Night ? NightMode : PointMatch;

        /// <summary>The line under the map's name: "POINT MATCH  ·  38 PLAYERS  ·  3 HUMANS".</summary>
        public static string SummaryLine(int players, int humans)
            => PointMatch + Separator + PlayersLine(players, humans);

        /// <summary>The summary line for a room's own mode: "NIGHT MODE  ·  38 PLAYERS  ·  3 HUMANS".</summary>
        public static string SummaryLine(GameMode mode, int players, int humans)
            => ModeName(mode) + Separator + PlayersLine(players, humans);

        /// <summary>"12 PLAYERS  ·  2 HUMANS".</summary>
        public static string PlayersLine(int players, int humans)
            => Count(players, "PLAYER") + Separator + Count(humans, "HUMAN");

        /// <summary>A team's totals, "145 KILLS  ·  132 DEATHS".</summary>
        public static string TotalsLine(int kills, int deaths)
            => Count(kills, "KILL") + Separator + Count(deaths, "DEATH");

        /// <summary>
        /// The rules line at the foot of the board: the original game's rules, and only those.
        /// </summary>
        /// <remarks>
        /// Owner ruling 2026-09-29. A death scores for the victim's opponents, one point per
        /// capture point they hold; a side wins at the margin, or by taking every spawn point the
        /// other side has. The board used to add "holding more flags scores over time", a rule the
        /// owner had removed.
        /// </remarks>
        public static string Rules(int victoryMargin)
            => "EVERY ENEMY DEATH SCORES +1 PER FLAG YOU HOLD"
               + Separator + "LEAD BY " + victoryMargin.ToString(CultureInfo.InvariantCulture) + " TO WIN"
               + Separator + "OR TAKE EVERY ENEMY SPAWN";

        /// <summary>The rules line under the room's rule (protocol 14, phase P32).</summary>
        public static string Rules(int victoryPoints, VictoryRule rule)
            => rule != VictoryRule.Target
                ? Rules(victoryPoints)
                : "EVERY ENEMY DEATH SCORES +1 PER FLAG YOU HOLD"
                  + Separator + "FIRST TO " + victoryPoints.ToString(CultureInfo.InvariantCulture) + " WINS"
                  + Separator + "OR TAKE EVERY ENEMY SPAWN";

        /// <summary>
        /// What one enemy death is worth to a side holding <paramref name="flags"/> capture
        /// points, in the words the side's band shows: "+3 PER KILL".
        /// </summary>
        /// <remarks>
        /// The multiplier on the board, so a score that moves by three on one kill reads as the
        /// rule rather than as a wrong number. Zero flags is shown as it is: a side with no points
        /// scores nothing for a kill.
        /// </remarks>
        public static string PerKillLine(int flags)
            => "+" + (flags < 0 ? 0 : flags).ToString(CultureInfo.InvariantCulture) + " PER KILL";

        private static string Count(int value, string noun)
            => value.ToString(CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? string.Empty : "S");
    }
}
