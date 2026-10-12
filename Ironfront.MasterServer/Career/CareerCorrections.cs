using System.Collections.Generic;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.MasterServer.Career
{
    /// <summary>One career number a rule change owes a player, with the evidence it rests on.</summary>
    internal readonly struct CareerCorrection
    {
        public CareerCorrection(string username, CareerStat stat, long value, string evidence)
        {
            Username = username;
            Stat = stat;
            Value = value;
            Evidence = evidence;
        }

        public string Username { get; }

        public CareerStat Stat { get; }

        /// <summary>Folded in by the stat's own rule: added to a count, raised to a best, OR-ed into parts.</summary>
        public long Value { get; }

        public string Evidence { get; }
    }

    /// <summary>
    /// Rounds the old rules withheld from careers, credited once at master start (owner's run of
    /// 2026-10-12: "award what a logic error withheld, to me and to every other player").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only what a log proves.</b> Each row is a round the game servers' logs of 2026-10-12 show
    /// in full (who joined when, on which side, the rule, the result) judged by today's rules
    /// (<see cref="CareerRules"/>). The achievements follow by themselves: the master judges every
    /// career again at the player's next sign-in, so a credited OUTNUMBERED arrives as a banner.
    /// Season 1's rounds cannot be judged this way: their server logs are gone and the master
    /// never kept the sides, so they are left as they were.
    /// </para>
    /// <para>
    /// <b>Applied once</b>, behind <see cref="Marker"/> in <c>career_meta</c>, and every row is logged.
    /// </para>
    /// </remarks>
    internal static class CareerCorrections
    {
        public const string Marker = "correction-2026-10-12";

        private const string IslandOneVersusThree =
            "Island room 3, 2026-10-12 03:04:12-03:14:05 UTC: nghaiz alone on team 0 against ptul2x5, thongdzbodoiqua "
            + "and nghianguancut, all in before the first point, LEAD BY 200, won 574-371 in 593 s (the old 10 minutes refused it)";

        private const string ForestLakeRoundTwo =
            "Forest Lake room 2 round 2, 2026-10-12 02:57:12-02:59:54 UTC: nghaiz + ptul2x5 against thongdzbodoiqua + "
            + "nghianguancut, all carried over from round 1, LEAD BY 200, won 234-32 in 162 s (the old 5 minutes refused it)";

        public static readonly IReadOnlyList<CareerCorrection> OwnerRun20261012 = new[]
        {
            new CareerCorrection("nghaiz", CareerStat.OutnumberedWins, 1, IslandOneVersusThree),

            new CareerCorrection("nghaiz", CareerStat.RoundsFinished, 1, ForestLakeRoundTwo),
            new CareerCorrection("nghaiz", CareerStat.RoundsWon, 1, ForestLakeRoundTwo),
            new CareerCorrection("nghaiz", CareerStat.GoldRoundsWon, 1, ForestLakeRoundTwo),
            new CareerCorrection("nghaiz", CareerStat.WinsForestLake, 1, ForestLakeRoundTwo),
            new CareerCorrection("ptul2x5", CareerStat.RoundsFinished, 1, ForestLakeRoundTwo),
            new CareerCorrection("ptul2x5", CareerStat.RoundsWon, 1, ForestLakeRoundTwo),
            new CareerCorrection("ptul2x5", CareerStat.GoldRoundsWon, 1, ForestLakeRoundTwo),
            new CareerCorrection("ptul2x5", CareerStat.WinsForestLake, 1, ForestLakeRoundTwo),
            new CareerCorrection("ptul2x5", CareerStat.BestWinStreak, 2,
                ForestLakeRoundTwo + "; with round 1 a run of two wins (UNDEFEATED's count)"),
            new CareerCorrection("thongdzbodoiqua", CareerStat.RoundsFinished, 1, ForestLakeRoundTwo),
            new CareerCorrection("nghianguancut", CareerStat.RoundsFinished, 1, ForestLakeRoundTwo),

            // GRAND TOUR's Dustbowl LEAD BY win: the owner played it, and the old practice rule
            // refused a win under five minutes (practice is judged on the player's own machine, so
            // no server log exists; the owner's word of 2026-10-12 is the record).
            new CareerCorrection("nghaiz", CareerStat.PrGrandTour, 1,
                "GRAND TOUR part DUSTBOWL, LEAD BY: a practice win the five-minute rule refused (owner, 2026-10-12)"),
        };
    }
}
