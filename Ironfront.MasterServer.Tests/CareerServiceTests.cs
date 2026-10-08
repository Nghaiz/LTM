using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterServer.Career;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol.Achievements;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Careers, achievements and the global ranking (owner's list of 2026-10-09, item 4).
    /// </summary>
    public sealed class CareerServiceTests
    {
        private const string Password = "aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899";

        [Fact]
        public void AFirstRoundWithAKillEarnsTheFirstTwoAchievements()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            List<string> earned = career.RecordRound(ids[0], Stats(("headshots", 1)), kills: 2, deaths: 1, score: 6, mapId: 1, night: false, now: 10);

            Assert.Contains("boots_on_the_ground", earned);
            Assert.Contains("first_blood", earned);
            Assert.DoesNotContain("tour_of_duty", earned);
            Dictionary<string, long> stats = db.ReadCareer(ids[0]);
            Assert.Equal(1, stats["matches"]);
            Assert.Equal(2, stats["kills"]);
            Assert.Equal(6, stats["score"]);
        }

        [Fact]
        public void AnAchievementIsEarnedOnce()
        {
            (CareerService career, _, int[] ids) = Players(1);

            career.RecordRound(ids[0], null, 1, 0, 1, 1, false, 10);
            List<string> second = career.RecordRound(ids[0], null, 1, 0, 1, 1, false, 20);

            Assert.DoesNotContain("first_blood", second);
            Assert.DoesNotContain("boots_on_the_ground", second);
        }

        [Fact]
        public void CountersAddMaximaKeepTheBestAndMapsAreABitSet()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            career.RecordRound(ids[0], Stats(("bestStreak", 7), ("longestKillMetres", 120)), 3, 1, 3, mapId: 1, night: false, now: 1);
            List<string> earned = career.RecordRound(ids[0], Stats(("bestStreak", 4), ("longestKillMetres", 160)), 2, 0, 2, mapId: 2, night: false, now: 2);
            career.RecordRound(ids[0], null, 0, 0, 0, mapId: 3, night: true, now: 3);

            Dictionary<string, long> stats = db.ReadCareer(ids[0]);
            Assert.Equal(5, stats["kills"]);
            Assert.Equal(7, stats["bestStreak"]);
            Assert.Equal(160, stats["longestKillMetres"]);
            Assert.Equal((1 << 1) | (1 << 2) | (1 << 3), stats["mapsPlayed"]);
            Assert.Equal(1, stats["nightMatches"]);
            Assert.Contains("long_shot", earned);
            Assert.Contains("world_traveler", db.ReadAchievements(ids[0]).Select(a => a.Id));
            Assert.Contains("night_owl", db.ReadAchievements(ids[0]).Select(a => a.Id));
        }

        [Fact]
        public void TheMastersOwnFactsBeatTheServersWord()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            career.RecordRound(ids[0], Stats(("nightMatches", 5), ("mapsPlayed", 0xFF)), 0, 0, 0, mapId: 1, night: false, now: 1);

            Dictionary<string, long> stats = db.ReadCareer(ids[0]);
            Assert.False(stats.ContainsKey("nightMatches"));
            Assert.Equal(1 << 1, stats["mapsPlayed"]);
        }

        [Fact]
        public void OnlyPracticeAchievementsCanBeClaimed()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            List<string> earned = career.Claim(ids[0], new[] { "basic_training", "war_machine", "nonsense", "basic_training" }, 5);

            Assert.Equal(new[] { "basic_training" }, earned);
            Assert.Equal(new[] { "basic_training" }, db.ReadAchievements(ids[0]).Select(a => a.Id));
            Assert.Empty(career.Claim(ids[0], new[] { "basic_training" }, 6));
        }

        [Fact]
        public void TheRankingKeepsAHundredOrderedByScoreAndPlacesTheRequester()
        {
            (CareerService career, _, int[] ids) = Players(105);
            for (int i = 0; i < ids.Length; i++) career.RecordRound(ids[i], null, kills: i % 7, deaths: 1, score: i, mapId: 1, night: false, now: 1);

            LeaderboardView view = career.Leaderboard(ids[0]);

            Assert.Equal(CareerService.LeaderboardSize, view.Top.Count);
            Assert.Equal(105, view.Players);
            Assert.Equal(ids[104], view.Top[0].Row.PlayerId);
            Assert.Equal(1, view.Top[0].Rank);
            Assert.True(view.Top.Zip(view.Top.Skip(1)).All(p => p.First.Row.Score >= p.Second.Row.Score));
            Assert.Equal(105, view.You!.Value.Rank);
        }

        [Fact]
        public void AchievementsReportHowCommonEachOneIs()
        {
            (CareerService career, _, int[] ids) = Players(4);
            career.RecordRound(ids[0], null, 1, 0, 1, 1, false, 1);
            career.RecordRound(ids[1], null, 0, 1, 0, 1, false, 1);
            career.Claim(ids[2], new[] { "student_of_war" }, 1);

            AchievementsView view = career.Achievements(ids[0]);

            Assert.Equal(3, view.Players);
            Assert.Equal(2, view.Holders["boots_on_the_ground"]);
            Assert.Equal(1, view.Holders["first_blood"]);
            Assert.Equal(1, view.Holders["student_of_war"]);
            Assert.Equal(1, view.Career["kills"]);
        }

        [Fact]
        public void TheCatalogueHasFiftyUniqueAchievementsWithBadges()
        {
            Assert.Equal(50, AchievementCatalog.All.Count);
            Assert.Equal(50, AchievementCatalog.All.Select(a => a.Id).Distinct().Count());
            Assert.Equal(4, AchievementCatalog.All.Count(a => a.IsClaimedByClient));
            Assert.Equal(6, AchievementCatalog.All.Count(a => a.Hidden));
            string badges = System.IO.Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi", "Achievements");
            foreach (Achievement achievement in AchievementCatalog.All)
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(badges, achievement.Id + ".png")), achievement.Id + " has no badge.");
        }

        [Fact]
        public void TheWikiPageNamesEveryAchievementWithItsBadge()
        {
            // docs/achievements.md is generated from the catalogue (tools/ui/write_achievements_doc.py);
            // a new or renamed achievement without a re-run is caught here.
            string page = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "docs", "achievements.md"));
            foreach (Achievement achievement in AchievementCatalog.All)
            {
                Assert.Contains("**" + achievement.Title + "**", page);
                Assert.Contains("`" + achievement.Id + "`", page);
                Assert.Contains("Achievements/" + achievement.Id + ".png", page);
                Assert.Contains(achievement.Description, page);
            }
        }

        [Fact]
        public void TheGamesPracticeFeatsAreExactlyTheCataloguesClaimableAchievements()
        {
            // PracticeFeats (Unity, Net/Shared) raises these ids; the master accepts a claim only
            // for a client-claimed achievement, so a typo there would be refused for ever.
            string feats = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(),
                "Ironfront_Reborn", "Assets", "Scripts", "Net", "Shared", "PracticeFeats.cs"));
            var raised = new HashSet<string>(System.Text.RegularExpressions.Regex
                .Matches(feats, @"public const string \w+ = ""([a-z_]+)"";")
                .Select(m => m.Groups[1].Value));
            var claimable = new HashSet<string>(AchievementCatalog.All.Where(a => a.IsClaimedByClient).Select(a => a.Id));
            Assert.Equal(claimable.OrderBy(x => x), raised.OrderBy(x => x));
        }

        private static string RepoRoot()
        {
            string? dir = System.AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Ironfront.sln"))) dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? throw new System.InvalidOperationException("repository root not found");
        }

        private static Dictionary<string, long> Stats(params (string Key, long Value)[] stats)
            => stats.ToDictionary(s => s.Key, s => s.Value);

        private static (CareerService, SqliteDatabase, int[]) Players(int count)
        {
            // Straight into the table: a career does not care how the account was made, and a
            // hundred bcrypt registrations would cost the suite ten seconds.
            var database = new SqliteDatabase(":memory:");
            var ids = new int[count];
            for (int i = 0; i < count; i++)
            {
                Assert.True(database.InsertAccount("pl" + i, Password, "Player" + i, 0));
                ids[i] = database.FindAccount("pl" + i)!.PlayerId;
            }
            return (new CareerService(database), database, ids);
        }
    }
}
