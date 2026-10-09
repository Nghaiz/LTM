using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Overlay;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The global ranking's comparison (achievements v2, section 6.2): the pairing, the "who holds
    /// it" filter, the cells each side shows, and that nothing about a hidden achievement the viewer
    /// lacks can be read off the screen.
    /// </summary>
    public sealed class AchievementCompareTests
    {
        private static AchievementState Mine(params string[] held) => new AchievementState
        {
            Players = 100,
            Unlocked = held.Select((id, i) => new AchievementUnlock { Id = id, At = 1_000 + i }).ToArray(),
            Earned = new Dictionary<string, long> { ["roll_call"] = 90, ["curvature"] = 1, ["steady_hand"] = 40, ["rampage"] = 3 },
            Career = new Dictionary<string, long> { [CareerStats.Key(CareerStat.Kills)] = 99 },
        };

        private static PlayerProfile Theirs(params string[] held) => new PlayerProfile
        {
            Player = new LeaderboardRow { PlayerId = 9, Name = "Kien", Rank = 1, Achievements = held.Length + 2, Points = 400, Mythics = 1 },
            Unlocked = held.Select((id, i) => new AchievementUnlock { Id = id, At = 2_000 + i }).ToArray(),
            Hidden = 2,
            Tiers = new[] { 3, 1, 0, 0, 1 },
            Career = new Dictionary<string, long> { [CareerStats.Key(CareerStat.Kills)] = 642 },
        };

        private static ComparePair Pair(List<ComparePair> pairs, string id) => pairs.Single(p => p.Achievement.Id == id);

        [Fact]
        public void EveryAchievementIsPairedInCatalogueOrder()
        {
            List<ComparePair> pairs = AchievementCompare.Build(Mine("roll_call"), new HashSet<string>(), null, Theirs("roll_call", "steady_hand"));

            Assert.Equal(AchievementCatalog.All.Select(a => a.Id), pairs.Select(p => p.Achievement.Id));
            Assert.True(Pair(pairs, "roll_call").IHold);
            Assert.True(Pair(pairs, "roll_call").TheyHold);
            Assert.False(Pair(pairs, "steady_hand").IHold);
            Assert.True(Pair(pairs, "steady_hand").TheyHold);
        }

        [Fact]
        public void TheOwnershipFilterSplitsBothMeThemAndNeither()
        {
            List<ComparePair> pairs = AchievementCompare.Build(Mine("roll_call", "lights_out"), new HashSet<string>(), null,
                Theirs("roll_call", "steady_hand"));

            string[] Ids(CompareOwnership owner) => pairs.Where(p => AchievementCompare.Passes(p, null, owner, null))
                .Select(p => p.Achievement.Id).ToArray();

            Assert.Equal(new[] { "roll_call" }, Ids(CompareOwnership.Both));
            Assert.Equal(new[] { "lights_out" }, Ids(CompareOwnership.OnlyMe));
            Assert.Equal(new[] { "steady_hand" }, Ids(CompareOwnership.OnlyThem));
            Assert.Equal(80 - 3, Ids(CompareOwnership.Neither).Length);
            Assert.Equal(80, Ids(CompareOwnership.All).Length);
        }

        [Fact]
        public void AHiddenAchievementTheViewerLacksIsClassifiedAndNeverOnlyTheirs()
        {
            Achievement hidden = AchievementCatalog.All.First(a => a.Hidden);
            // Even if a master ever sent it, the viewer's side decides: still classified.
            List<ComparePair> pairs = AchievementCompare.Build(Mine(), new HashSet<string>(), null, Theirs(hidden.Id));
            ComparePair pair = Pair(pairs, hidden.Id);

            Assert.True(pair.Classified);
            Assert.False(pair.TheyHold);
            Assert.Equal("CLASSIFIED", AchievementCompare.SideText(pair, mine: false));
            Assert.Equal("NOT YET", AchievementCompare.SideText(pair, mine: true));
            Assert.Equal(-1f, AchievementCompare.SideFraction(pair, mine: false));
            Assert.False(AchievementCompare.Passes(pair, null, CompareOwnership.OnlyThem, null));
            Assert.True(AchievementCompare.Passes(pair, null, CompareOwnership.Neither, null));
            Assert.DoesNotContain(AchievementCompare.Rarest(pairs, 80), e => e.Achievement.Id == hidden.Id);
        }

        [Fact]
        public void AHiddenAchievementTheViewerHoldsShowsWhetherTheyHaveItButNoProgress()
        {
            Achievement hidden = AchievementCatalog.All.First(a => a.Hidden && a.Progress != AchievementProgress.None);
            List<ComparePair> both = AchievementCompare.Build(Mine(hidden.Id), new HashSet<string>(), null, Theirs(hidden.Id));
            List<ComparePair> onlyMe = AchievementCompare.Build(Mine(hidden.Id), new HashSet<string>(), null, Theirs());

            Assert.False(Pair(both, hidden.Id).Classified);
            Assert.True(Pair(both, hidden.Id).TheyHold);
            Assert.Equal("NOT YET", AchievementCompare.SideText(Pair(onlyMe, hidden.Id), mine: false));
            Assert.Equal(-1f, AchievementCompare.SideFraction(Pair(onlyMe, hidden.Id), mine: false));
        }

        [Fact]
        public void TheCellsSayTheProgressOnEachSide()
        {
            List<ComparePair> pairs = AchievementCompare.Build(Mine(), new HashSet<string>(), null, Theirs());
            ComparePair grim = Pair(pairs, "grim_arithmetic");

            Assert.Equal("99 / 10,000", AchievementCompare.SideText(grim, mine: true));
            Assert.Equal("642 / 10,000", AchievementCompare.SideText(grim, mine: false));
            Assert.Equal(0.0642f, AchievementCompare.SideFraction(grim, mine: false), 4);
        }

        [Fact]
        public void TheirTotalsAreTheMastersCountsHiddenIncluded()
        {
            CompareTotals totals = AchievementCompare.TheirTotals(Theirs("roll_call"));

            Assert.Equal(3, totals.Count);
            Assert.Equal(400, totals.Points);
            Assert.Equal(new[] { 3, 1, 0, 0, 1 }, totals.PerTier);
            Assert.Equal(2, totals.Hidden);
        }

        [Fact]
        public void MyTotalsCountWhatIHold()
        {
            List<ComparePair> pairs = AchievementCompare.Build(Mine("roll_call", "curvature"), new HashSet<string>(), null, Theirs());
            CompareTotals totals = AchievementCompare.MineTotals(pairs);

            Assert.Equal(2, totals.Count);
            Assert.Equal(AchievementCatalog.Find("roll_call")!.Points + AchievementCatalog.Find("curvature")!.Points, totals.Points);
            Assert.Equal(1, totals.PerTier[(int)AchievementTier.Mythic]);
        }

        [Fact]
        public void TheCardsRarestAreTheirRarestFirst()
        {
            List<ComparePair> pairs = AchievementCompare.Build(Mine(), new HashSet<string>(), null,
                Theirs("roll_call", "curvature", "steady_hand", "rampage"));

            Assert.Equal(new[] { "curvature", "rampage", "steady_hand" },
                AchievementCompare.Rarest(pairs, 3).Select(e => e.Achievement.Id));
        }

        [Fact]
        public void SortingFollowsTheViewersSide()
        {
            List<ComparePair> pairs = AchievementCompare.Build(Mine("steady_hand", "roll_call"), new HashSet<string>(), null, Theirs());

            AchievementCompare.Sort(pairs, AchievementSort.Recent);
            Assert.Equal("roll_call", pairs[0].Achievement.Id);
            Assert.Equal("steady_hand", pairs[1].Achievement.Id);

            AchievementCompare.Sort(pairs, AchievementSort.Difficulty);
            Assert.Equal(Enumerable.Range(1, 80), pairs.Select(p => p.Achievement.Number));
        }
    }
}
