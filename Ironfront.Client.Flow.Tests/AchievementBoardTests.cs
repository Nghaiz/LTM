using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Overlay;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The achievement page's order and wording (owner's list of 2026-10-09, item 4): commonest
    /// first, a hidden one sealed until earned, practice achievements earned on this machine
    /// counted before the master has them.
    /// </summary>
    public sealed class AchievementBoardTests
    {
        private static AchievementState State(long players, params (string Id, long Holders)[] holders)
            => new AchievementState
            {
                Players = players,
                Earned = holders.ToDictionary(h => h.Id, h => h.Holders),
            };

        [Fact]
        public void TheCommonestAchievementComesFirst()
        {
            AchievementState state = State(200, ("victory", 150), ("first_blood", 190), ("flawless", 2));

            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());

            Assert.Equal(50, board.Count);
            Assert.Equal("first_blood", board[0].Achievement.Id);
            Assert.Equal("victory", board[1].Achievement.Id);
            Assert.True(board.FindIndex(e => e.Achievement.Id == "flawless") > 1);
        }

        [Fact]
        public void EqualSharesFallBackToTheCheaperMetalThenTheCatalogue()
        {
            List<AchievementEntry> board = AchievementBoard.Build(State(10), new HashSet<string>());

            for (int i = 1; i < board.Count; i++)
            {
                AchievementEntry before = board[i - 1], after = board[i];
                Assert.True(before.Achievement.Tier < after.Achievement.Tier
                            || (before.Achievement.Tier == after.Achievement.Tier && before.Order < after.Order));
            }
        }

        [Fact]
        public void APracticeAchievementEarnedHereCountsBeforeTheMasterKnows()
        {
            List<AchievementEntry> board = AchievementBoard.Build(null, new HashSet<string> { "basic_training" });

            AchievementEntry entry = board.Single(e => e.Achievement.Id == "basic_training");
            Assert.True(entry.Earned);
            Assert.Equal(0, entry.EarnedAt);
            Assert.Equal("EARNED", AchievementBoard.EarnedText(entry));
            Assert.Equal(1, AchievementBoard.EarnedCount(board));
        }

        [Fact]
        public void AHiddenAchievementIsSealedUntilEarned()
        {
            var state = new AchievementState
            {
                Players = 4,
                Unlocked = new[] { new AchievementUnlock { Id = "own_goal", At = 1_760_000_000_000 } },
            };

            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());

            Assert.False(board.Single(e => e.Achievement.Id == "gravity_wins").IsRevealed);
            AchievementEntry earned = board.Single(e => e.Achievement.Id == "own_goal");
            Assert.True(earned.IsRevealed);
            Assert.StartsWith("EARNED ", AchievementBoard.EarnedText(earned));
        }

        [Fact]
        public void TheShareReadsAsAPercentAndUnknownIsADash()
        {
            AchievementEntry known = AchievementBoard.Build(State(1000, ("victory", 632)), new HashSet<string>())
                .Single(e => e.Achievement.Id == "victory");
            AchievementEntry rare = AchievementBoard.Build(State(5000, ("flawless", 1)), new HashSet<string>())
                .Single(e => e.Achievement.Id == "flawless");
            AchievementEntry unknown = AchievementBoard.Build(null, new HashSet<string>())
                .Single(e => e.Achievement.Id == "victory");

            Assert.Equal("63.2%", AchievementBoard.ShareText(known));
            Assert.Equal("COMMON", AchievementBoard.RarityText(known));
            Assert.Equal("<0.1%", AchievementBoard.ShareText(rare));
            Assert.Equal("ULTRA RARE", AchievementBoard.RarityText(rare));
            Assert.Equal("—", AchievementBoard.ShareText(unknown));
        }

        [Fact]
        public void ProgressIsDrawnFromTheCareerInTheStatsOwnUnits()
        {
            var state = new AchievementState
            {
                Players = 3,
                Career = new Dictionary<string, long>
                {
                    [CareerStats.Key(CareerStat.Kills)] = 37,
                    [CareerStats.Key(CareerStat.SecondsPlayed)] = 5 * 3600,
                    [CareerStats.Key(CareerStat.LongestKillMetres)] = 90,
                },
            };

            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());
            AchievementEntry decorated = board.Single(e => e.Achievement.Id == "decorated");
            AchievementEntry forever = board.Single(e => e.Achievement.Id == "forever_war");
            AchievementEntry longShot = board.Single(e => e.Achievement.Id == "long_shot");

            Assert.True(decorated.ShowsProgress);
            Assert.Equal("37 / 100", AchievementBoard.ProgressText(decorated));
            Assert.Equal(0.37f, AchievementBoard.ProgressFraction(decorated), 3);
            Assert.Equal("5.0 / 24 H", AchievementBoard.ProgressText(forever));
            Assert.Equal("90 / 150 M", AchievementBoard.ProgressText(longShot));
            Assert.False(board.Single(e => e.Achievement.Id == "first_blood").ShowsProgress);
        }
    }
}
