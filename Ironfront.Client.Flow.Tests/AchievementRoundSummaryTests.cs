using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Client.Menu;
using Ironfront.Net.Unity.Client.Overlay;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The end-of-round summary, the page's filters and sorts, and practice alone against every bot
    /// (achievements v2, sections 5.2 and 6.1).
    /// </summary>
    public sealed class AchievementRoundSummaryTests
    {
        private static Dictionary<string, long> Career(params (CareerStat Stat, long Value)[] stats)
            => stats.ToDictionary(s => CareerStats.Key(s.Stat), s => s.Value);

        [Fact]
        public void TheSummaryListsUnlocksFirstThenTheNearestProgress()
        {
            List<RoundSummaryLine> lines = AchievementRoundSummary.Build(
                Career((CareerStat.Kills, 600), (CareerStat.LongestKillMetres, 200)),
                Career((CareerStat.Kills, 642), (CareerStat.LongestKillMetres, 260), (CareerStat.RoundsFinished, 1)),
                new HashSet<string>(), new[] { "roll_call" });

            Assert.Equal(RoundSummaryKind.Unlocked, lines[0].Kind);
            Assert.Equal("UNLOCKED  ·  ROLL CALL  ·  +10 PTS", lines[0].Text);
            Assert.Contains(lines, l => l.Text == "+42  ·  GRIM ARITHMETIC  642 / 10,000");
            Assert.Contains(lines, l => l.Text == "NEW BEST 260 M  ·  OVERWATCH  (TARGET 300 M)");
            Assert.True(lines.Count(l => l.Kind == RoundSummaryKind.Progress) <= AchievementRoundSummary.MaxProgressLines);
        }

        [Fact]
        public void PassingHalfOrNineTenthsIsAMilestone()
        {
            List<RoundSummaryLine> lines = AchievementRoundSummary.Build(
                Career((CareerStat.Kills, 4990), (CareerStat.Deaths, 890)),
                Career((CareerStat.Kills, 5010), (CareerStat.Deaths, 905)),
                new HashSet<string>(), new string[0]);

            Assert.Contains(lines, l => l.Kind == RoundSummaryKind.Milestone && l.Text == "HALFWAY  ·  GRIM ARITHMETIC  5,010 / 10,000");
            Assert.Contains(lines, l => l.Kind == RoundSummaryKind.Milestone && l.Text == "ALMOST THERE  ·  CANNON FODDER  905 / 1,000");
        }

        [Fact]
        public void WithoutACareerFromBeforeTheRoundOnlyTheUnlocksAreListed()
        {
            List<RoundSummaryLine> lines = AchievementRoundSummary.Build(
                null,
                Career((CareerStat.Kills, 642), (CareerStat.LongestKillMetres, 260)),
                new HashSet<string>(), new[] { "roll_call" });

            Assert.Equal(new[] { "roll_call" }, lines.Select(l => l.Achievement.Id));
        }

        [Fact]
        public void AHiddenAchievementsProgressIsNeverShown()
        {
            List<RoundSummaryLine> lines = AchievementRoundSummary.Build(
                Career((CareerStat.LongestShotgunKillMetres, 10)),
                Career((CareerStat.LongestShotgunKillMetres, 50)),
                new HashSet<string>(), new string[0]);

            Assert.DoesNotContain(lines, l => l.Achievement.Id == "buckshot_sniper");
        }

        [Fact]
        public void TheFiltersPickByMetalStateAndKind()
        {
            var state = new AchievementState
            {
                Unlocked = new[] { new AchievementUnlock { Id = "roll_call", At = 5 } },
                Career = new Dictionary<string, long> { [CareerStats.Key(CareerStat.Kills)] = 12 },
            };
            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());

            Assert.Equal(16, board.Count(e => AchievementBoard.Passes(e, AchievementTier.Mythic, AchievementStatus.All, null)));
            Assert.Equal(new[] { "roll_call" }, board.Where(e => AchievementBoard.Passes(e, null, AchievementStatus.Unlocked, null)).Select(e => e.Achievement.Id));
            Assert.Contains(board.Where(e => AchievementBoard.Passes(e, null, AchievementStatus.InProgress, null)), e => e.Achievement.Id == "baptism_of_fire");
            Assert.Equal(15, board.Count(e => AchievementBoard.Passes(e, null, AchievementStatus.All, AchievementTags.Hidden)));
            Assert.Equal(7, board.Count(e => AchievementBoard.Passes(e, null, AchievementStatus.All, AchievementTags.Night)));
        }

        [Fact]
        public void TheSortsOrderRarestRecentAndClosest()
        {
            var state = new AchievementState
            {
                Players = 100,
                Earned = new Dictionary<string, long> { ["roll_call"] = 90, ["curvature"] = 1, ["steady_hand"] = 40 },
                Unlocked = new[]
                {
                    new AchievementUnlock { Id = "roll_call", At = 10 },
                    new AchievementUnlock { Id = "steady_hand", At = 20 },
                },
                Career = new Dictionary<string, long> { [CareerStats.Key(CareerStat.Kills)] = 99 },
            };
            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());

            AchievementBoard.Sort(board, AchievementSort.Rarity);
            Assert.True(board.FindIndex(e => e.Achievement.Id == "curvature") < board.FindIndex(e => e.Achievement.Id == "roll_call"));

            AchievementBoard.Sort(board, AchievementSort.Recent);
            Assert.Equal("steady_hand", board[0].Achievement.Id);
            Assert.Equal("roll_call", board[1].Achievement.Id);

            AchievementBoard.Sort(board, AchievementSort.Closest);
            Assert.Equal("baptism_of_fire", board[0].Achievement.Id);

            AchievementBoard.Sort(board, AchievementSort.Difficulty);
            Assert.Equal(Enumerable.Range(1, 80), board.Select(e => e.Achievement.Number));
        }

        [Fact]
        public void PracticeAloneSendsEveryBotToTheOtherSideUpToItsLimit()
        {
            Assert.True(PracticeChoice.TryRead(GameMode.PointMatch, VictoryRule.Margin, "", "", 1, 30, team: 2,
                vehicleOption: 0, "", out PracticeSettings blueAlone, out _));
            Assert.Equal(0, blueAlone.PlayerTeam);
            Assert.Equal(0, blueAlone.Team0Bots);
            Assert.Equal(30, blueAlone.Team1Bots);
            Assert.Contains("ALONE VS 30 BOTS", PracticeChoice.Summary(in blueAlone));

            Assert.True(PracticeChoice.TryRead(GameMode.PointMatch, VictoryRule.Margin, "", "", 1, 100, team: 3,
                vehicleOption: 0, "", out PracticeSettings redAlone, out _));
            Assert.Equal(1, redAlone.PlayerTeam);
            Assert.Equal(ProtocolConstants.MAX_BOTS_PER_TEAM, redAlone.Team0Bots);
            Assert.Equal(0, redAlone.Team1Bots);
        }
    }
}
