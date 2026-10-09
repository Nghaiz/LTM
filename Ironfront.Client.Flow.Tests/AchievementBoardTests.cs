using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Overlay;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The achievement page's model (achievements v2): easiest first, a hidden one named but
    /// hinted until earned, progress read from the same career numbers the master judges, practice
    /// achievements earned on this machine counted before the master has them.
    /// </summary>
    public sealed class AchievementBoardTests
    {
        private static AchievementState State(long players, params (string Id, long Holders)[] holders)
            => new AchievementState
            {
                Players = players,
                Earned = holders.ToDictionary(h => h.Id, h => h.Holders),
            };

        private static AchievementEntry Entry(List<AchievementEntry> board, string id) => board.Single(e => e.Achievement.Id == id);

        [Fact]
        public void EveryAchievementIsListedEasiestFirst()
        {
            List<AchievementEntry> board = AchievementBoard.Build(State(10), new HashSet<string>());

            Assert.Equal(80, board.Count);
            Assert.Equal(Enumerable.Range(1, 80), board.Select(e => e.Achievement.Number));
        }

        [Fact]
        public void APracticeAchievementEarnedHereCountsBeforeTheMasterKnows()
        {
            List<AchievementEntry> board = AchievementBoard.Build(null, new HashSet<string> { "cadet" });

            AchievementEntry entry = Entry(board, "cadet");
            Assert.True(entry.Earned);
            Assert.Equal(0, entry.EarnedAt);
            Assert.Equal("EARNED", AchievementBoard.EarnedText(entry));
            Assert.Equal(1, AchievementBoard.EarnedCount(board));
            Assert.Equal(10, AchievementBoard.EarnedPoints(board));
        }

        [Fact]
        public void AHiddenAchievementShowsItsNameAndHintUntilEarned()
        {
            var state = new AchievementState
            {
                Players = 4,
                Unlocked = new[] { new AchievementUnlock { Id = "nine_lives", At = 1_760_000_000_000 } },
            };

            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());

            AchievementEntry sealedOne = Entry(board, "man_overboard");
            Assert.False(sealedOne.IsRevealed);
            Assert.Equal("Not every road is made of dirt.", AchievementBoard.DescriptionText(sealedOne));
            Assert.False(sealedOne.ShowsProgress);

            AchievementEntry earned = Entry(board, "nine_lives");
            Assert.True(earned.IsRevealed);
            Assert.Equal("Survive a fall with 5 health or less left.", AchievementBoard.DescriptionText(earned));
            Assert.StartsWith("EARNED ", AchievementBoard.EarnedText(earned));
        }

        [Fact]
        public void TheShareReadsAsAPercentWithItsRarity()
        {
            AchievementEntry common = Entry(AchievementBoard.Build(State(1000, ("roll_call", 632)), new HashSet<string>()), "roll_call");
            AchievementEntry epic = Entry(AchievementBoard.Build(State(1000, ("juggernaut", 20)), new HashSet<string>()), "juggernaut");
            AchievementEntry legendary = Entry(AchievementBoard.Build(State(5000, ("curvature", 1)), new HashSet<string>()), "curvature");
            AchievementEntry unknown = Entry(AchievementBoard.Build(null, new HashSet<string>()), "roll_call");

            Assert.Equal("63.2%", AchievementBoard.ShareText(common));
            Assert.Equal("COMMON", AchievementBoard.RarityText(common));
            Assert.Equal("EPIC", AchievementBoard.RarityText(epic));
            Assert.Equal("<0.1%", AchievementBoard.ShareText(legendary));
            Assert.Equal("LEGENDARY", AchievementBoard.RarityText(legendary));
            Assert.Equal("-", AchievementBoard.ShareText(unknown));
        }

        [Fact]
        public void ProgressReadsAsACountAPersonalBestOrParts()
        {
            var state = new AchievementState
            {
                Players = 3,
                Career = new Dictionary<string, long>
                {
                    [CareerStats.Key(CareerStat.Kills)] = 642,
                    [CareerStats.Key(CareerStat.LongestKillMetres)] = 412,
                    [CareerStats.Key(CareerStat.MapsFinished)] = (1L << 1) | (1L << 3),
                },
            };

            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>());

            AchievementEntry grim = Entry(board, "grim_arithmetic");
            Assert.True(grim.ShowsProgress);
            Assert.Equal("642 / 10,000", AchievementBoard.ProgressText(grim));
            Assert.Equal(0.0642f, AchievementBoard.ProgressFraction(grim), 4);
            Assert.Equal("BEST 412 M", AchievementBoard.ProgressText(Entry(board, "overwatch")));
            Assert.Equal("2 / 3", AchievementBoard.ProgressText(Entry(board, "three_fronts")));
            Assert.False(Entry(board, "clean_sheet").ShowsProgress);
        }

        [Fact]
        public void PracticeNumbersKeptOnThisMachineAreLaidOverTheMasters()
        {
            var state = new AchievementState { Career = new Dictionary<string, long> { ["prHellWeekBest"] = 40 } };
            var local = new Dictionary<string, long> { ["prHellWeekBest"] = 52, ["prGrandTour"] = 3 };

            List<AchievementEntry> board = AchievementBoard.Build(state, new HashSet<string>(), local);

            Assert.Equal("BEST 52 KILLS", AchievementBoard.ProgressText(Entry(board, "hell_week")));
            Assert.Equal("2 / 7", AchievementBoard.ProgressText(Entry(board, "grand_tour")));
        }

        [Fact]
        public void AMythicNamesWhoEarnedItFirst()
        {
            var state = new AchievementState
            {
                Players = 2,
                Firsts = new Dictionary<string, FirstHolderInfo> { ["curvature"] = new FirstHolderInfo { Name = "Kien", At = 1_760_000_000_000 } },
            };

            AchievementEntry entry = Entry(AchievementBoard.Build(state, new HashSet<string>()), "curvature");

            Assert.StartsWith("First unlocked by Kien on ", AchievementBoard.FirstText(entry));
            Assert.Equal(string.Empty, AchievementBoard.FirstText(Entry(AchievementBoard.Build(state, new HashSet<string>()), "rampage")));
        }

        [Fact]
        public void IroncladCountsTheOthersHeld()
        {
            var state = new AchievementState
            {
                Unlocked = AchievementCatalog.All.Take(10).Select(a => new AchievementUnlock { Id = a.Id, At = 1 }).ToArray(),
            };

            AchievementEntry ironclad = Entry(AchievementBoard.Build(state, new HashSet<string> { "gold_standard" }), "ironclad");

            Assert.Equal("11 / 79", AchievementBoard.ProgressText(ironclad));
        }
    }
}
