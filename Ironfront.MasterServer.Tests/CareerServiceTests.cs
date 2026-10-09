using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterServer.Career;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Careers, achievements and the global ranking (achievements v2, <c>docs/achievements.md</c>):
    /// the catalogue's shape, each rule at its threshold and one short of it, and how a round, a
    /// round in progress and a practice claim reach the career.
    /// </summary>
    public sealed class CareerServiceTests
    {
        private const string Password = "aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899";

        // ------------------------------------------------------------------ the catalogue

        [Fact]
        public void TheCatalogueHasEightyAchievementsInTheApprovedShape()
        {
            IReadOnlyList<Achievement> all = AchievementCatalog.All;

            Assert.Equal(80, all.Count);
            Assert.Equal(Enumerable.Range(1, 80), all.Select(a => a.Number));
            Assert.Equal(80, all.Select(a => a.Id).Distinct().Count());
            Assert.Equal(80, all.Select(a => a.Title).Distinct().Count());
            Assert.Equal(15, all.Count(a => a.Hidden));
            Assert.Equal(15, all.Count(a => a.IsPractice));
            Assert.Equal(new[] { 13, 16, 21, 14, 16 },
                new[] { AchievementTier.Bronze, AchievementTier.Silver, AchievementTier.Gold, AchievementTier.Platinum, AchievementTier.Mythic }
                    .Select(t => all.Count(a => a.Tier == t)));
            Assert.Equal(6980, AchievementCatalog.TotalPoints);
        }

        [Fact]
        public void EveryAchievementCanBeEarnedAndReadsAsAnExactRule()
        {
            foreach (Achievement a in AchievementCatalog.All)
            {
                Assert.True(a.Target > 0, a.Id);
                Assert.False(string.IsNullOrWhiteSpace(a.Description), a.Id);
                Assert.True(a.Description.EndsWith("."), a.Id);
                Assert.DoesNotContain("—", a.Description);
                Assert.Equal(a.Hidden, a.Teaser.Length > 0);
                bool online = (a.Tags & AchievementTags.Online) != 0;
                Assert.True(online != a.IsPractice, a.Id);
                if (online && a.Id != "ironclad") Assert.True(a.Stat != null, a.Id);
                if (a.Stat != null) Assert.Equal(a.IsPractice, CareerStats.IsPractice(a.Stat.Value));
                if (a.Progress == AchievementProgress.Parts) Assert.Equal(a.Parts.Count, a.Target);
            }
        }

        [Fact]
        public void EveryCareerKeyAndFactKeyIsUniqueAndKeepsTheOldNames()
        {
            var careerKeys = Enumerable.Range(0, CareerStats.Count).Select(i => CareerStats.Key((CareerStat)i)).ToList();
            var factKeys = Enumerable.Range(0, RoundFacts.Count).Select(i => RoundFacts.Key((RoundFact)i)).ToList();

            Assert.Equal(careerKeys.Count, careerKeys.Distinct().Count());
            Assert.Equal(factKeys.Count, factKeys.Distinct().Count());
            foreach (string kept in new[] { "matches", "wins", "kills", "deaths", "score", "headshots", "bestStreak", "secondsPlayed" })
                Assert.Contains(kept, careerKeys);
        }

        // ------------------------------------------------------------------ rounds

        [Fact]
        public void AFinishedFiveMinuteRoundIsARollCallAndFourMinutesIsNot()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(2);

            List<string> full = Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 300)));
            List<string> shortRound = Round(career, ids[1], Facts(("finished", 1), ("secondsPlayed", 299)));

            Assert.Contains("roll_call", full);
            Assert.DoesNotContain("roll_call", shortRound);
            Assert.Equal(1, db.ReadCareer(ids[1])["matches"]);
            Assert.False(db.ReadCareer(ids[1]).ContainsKey("roundsFinished"));
        }

        [Fact]
        public void TenQualifyingWinsAreATasteOfVictory()
        {
            (CareerService career, _, int[] ids) = Players(1);
            Dictionary<string, long> win = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 600));

            for (int i = 0; i < 9; i++) Assert.DoesNotContain("taste_of_victory", Round(career, ids[0], win));
            Assert.Contains("taste_of_victory", Round(career, ids[0], win));
        }

        [Fact]
        public void ARoundInProgressUnlocksWithoutBeingWritten()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            List<string> earned = career.RecordRound(ids[0], Facts(("kills", 15), ("botKills", 15), ("bestStreak", 15)),
                15, 0, 15, mapId: 1, night: false, final: false, now: 5);

            Assert.Contains("unbroken", earned);
            Assert.False(db.ReadCareer(ids[0]).ContainsKey("kills"));
            Assert.DoesNotContain("roll_call", earned);
        }

        [Fact]
        public void ARoundInProgressNeverAwardsARoundEndFeat()
        {
            (CareerService career, _, int[] ids) = Players(1);
            Dictionary<string, long> facts = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 1000), ("kills", 12),
                ("botKills", 12), ("deaths", 0));

            Assert.DoesNotContain("clean_sheet", career.RecordRound(ids[0], facts, 12, 0, 12, 1, false, final: false, now: 1));
            Assert.Contains("clean_sheet", career.RecordRound(ids[0], facts, 12, 0, 12, 1, false, final: true, now: 2));
        }

        [Theory]
        [InlineData(900, 10, 0, true)]
        [InlineData(899, 10, 0, false)]
        [InlineData(900, 9, 0, false)]
        [InlineData(900, 10, 1, false)]
        public void CleanSheetNeedsFifteenMinutesTenKillsAndNoDeath(long seconds, long kills, long deaths, bool earned)
        {
            (CareerService career, _, int[] ids) = Players(1);
            List<string> got = Round(career, ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", seconds),
                ("botKills", kills), ("deaths", deaths)));
            Assert.Equal(earned, got.Contains("clean_sheet"));
        }

        [Theory]
        [InlineData(1, 0, true)]
        [InlineData(1, 1, false)]
        [InlineData(0, 0, false)]
        public void NakedEyeNeedsAGameThatReportsNightVisionAndNeverUsedIt(long known, long used, bool earned)
        {
            (CareerService career, _, int[] ids) = Players(1);
            Dictionary<string, long> facts = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 900), ("botKills", 10),
                ("nightVisionKnown", known), ("nightVisionUsed", used));

            List<string> got = career.RecordRound(ids[0], facts, 10, 0, 10, RoomRules.NightModeMapId, night: true, final: true, now: 1);

            Assert.Equal(earned, got.Contains("naked_eye"));
            Assert.Contains("lights_out", got);
        }

        [Fact]
        public void TheLowestScoreOfEveryoneIncludingBotsIsAParticipationTrophy()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> Basis(long score, long minOthers) => Facts(("finished", 1), ("secondsPlayed", 400),
                ("score", score), ("minOtherPoints", minOthers));

            Assert.Contains("participation_trophy", Round(career, ids[0], Basis(0, 0)));
            Assert.DoesNotContain("participation_trophy", Round(career, ids[1], Basis(5, 4)));
        }

        [Theory]
        [InlineData(3, 6, 5, true)]
        [InlineData(2, 6, 5, false)]
        [InlineData(3, 5, 5, false)]
        public void BulletSpongeIsStrictlyMostDeathsAmongAtLeastThreeHumans(long humans, long deaths, long others, bool earned)
        {
            (CareerService career, _, int[] ids) = Players(1);
            List<string> got = Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400), ("deaths", deaths),
                ("humansOwnSide", 1), ("humansEnemySide", humans - 1), ("maxOtherHumanDeaths", others)));
            Assert.Equal(earned, got.Contains("bullet_sponge"));
        }

        [Theory]
        [InlineData(1, 3, 600, true)]
        [InlineData(2, 3, 600, false)]
        [InlineData(1, 2, 600, false)]
        [InlineData(1, 3, 599, false)]
        public void OutnumberedIsTheOnlyHumanOnTheSideAgainstThreeForTenMinutes(long own, long enemy, long seconds, bool earned)
        {
            (CareerService career, _, int[] ids) = Players(1);
            List<string> got = Round(career, ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", seconds),
                ("humansOwnSide", own), ("humansEnemySide", enemy)));
            Assert.Equal(earned, got.Contains("outnumbered"));
        }

        [Fact]
        public void PacifistTakesMoreFlagsThanAnyoneWithNoKillsAndNoDeaths()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> Basis(long kills, long flags) => Facts(("finished", 1), ("won", 1), ("secondsPlayed", 900),
                ("botKills", kills), ("flagsCaptured", flags), ("maxOtherCaptures", 4));

            Assert.Contains("pacifist", Round(career, ids[0], Basis(0, 5)));
            Assert.DoesNotContain("pacifist", Round(career, ids[1], Basis(1, 5)));
        }

        [Fact]
        public void AbsoluteDominanceIsFirstInKillsFlagsAndAccuracyWithTheFewestDeaths()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> Basis(long shots, long hits) => Facts(("finished", 1), ("secondsPlayed", 400),
                ("botKills", 30), ("flagsCaptured", 3), ("deaths", 1), ("shots", shots), ("hits", hits),
                ("maxOtherKills", 30), ("maxOtherCaptures", 3), ("minOtherDeaths", 1), ("bestOtherAccuracyPermille", 400));

            Assert.Contains("absolute_dominance", Round(career, ids[0], Basis(100, 40)));
            Assert.DoesNotContain("absolute_dominance", Round(career, ids[1], Basis(100, 39)));
        }

        [Theory]
        [InlineData(15, 15, 15, true)]
        [InlineData(15, 16, 15, false)]
        [InlineData(15, 14, 14, false)]
        [InlineData(14, 15, 15, false)]
        public void DeadEyeIsFifteenKillsAndEveryBulletAHit(long kills, long shots, long hits, bool earned)
        {
            (CareerService career, _, int[] ids) = Players(1);
            List<string> got = Round(career, ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", 400),
                ("botKills", kills), ("shots", shots), ("hits", hits)));
            Assert.Equal(earned, got.Contains("dead_eye"));
        }

        [Fact]
        public void UntouchableNeedsAZeroThatWasReported()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> basis = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 900), ("botKills", 15));

            Assert.DoesNotContain("untouchable", Round(career, ids[0], basis));
            basis["damageTaken"] = 0;
            Assert.Contains("untouchable", Round(career, ids[1], basis));
        }

        [Fact]
        public void MapPainterHelpsTakeEveryFlagWithoutDyingAndWins()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> Basis(long helped) => Facts(("finished", 1), ("won", 1), ("secondsPlayed", 400),
                ("mapFlags", 5), ("flagsHelpedDistinct", helped));

            Assert.Contains("map_painter", Round(career, ids[0], Basis(5)));
            Assert.DoesNotContain("map_painter", Round(career, ids[1], Basis(4)));
        }

        [Fact]
        public void BladeOnlyKeepsTheBestAllMeleeRound()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400), ("botKills", 24), ("meleeKills", 24)));
            Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400), ("botKills", 30), ("meleeKills", 29)));
            Assert.Equal(24, db.ReadCareer(ids[0])["bladeOnlyBest"]);

            Assert.Contains("blade_only", Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400),
                ("botKills", 25), ("meleeKills", 25))));
        }

        [Fact]
        public void NightOnlyBestsCountOnlyAtNight()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            Dictionary<string, long> facts = Facts(("meleeKills", 10), ("longestHeadshotMetres", 320), ("botKills", 10));

            List<string> day = career.RecordRound(ids[0], facts, 10, 0, 10, 1, night: false, final: true, now: 1);
            Assert.DoesNotContain("night_terror", day);
            Assert.DoesNotContain("moonlight_marksman", day);

            List<string> night = career.RecordRound(ids[0], facts, 10, 0, 10, 3, night: true, final: true, now: 2);
            Assert.Contains("night_terror", night);
            Assert.Contains("moonlight_marksman", night);
            Assert.Equal(10, db.ReadCareer(ids[0])["nightKills"]);
        }

        [Fact]
        public void UndefeatedCountsWinsInARowAndAnyCountedRoundWithoutAWinResets()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            Dictionary<string, long> win = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 400));
            Dictionary<string, long> loss = Facts(("finished", 1), ("won", 0), ("secondsPlayed", 400));
            Dictionary<string, long> tooShort = Facts(("finished", 1), ("won", 0), ("secondsPlayed", 100));

            for (int i = 0; i < 9; i++) Round(career, ids[0], win);
            Round(career, ids[0], tooShort);
            Assert.Equal(9, db.ReadCareer(ids[0])["winStreak"]);
            Round(career, ids[0], loss);
            Assert.Equal(0, db.ReadCareer(ids[0])["winStreak"]);

            for (int i = 0; i < 9; i++) Assert.DoesNotContain("undefeated", Round(career, ids[0], win));
            Assert.Contains("undefeated", Round(career, ids[0], win));
            Assert.Equal(10, db.ReadCareer(ids[0])["bestWinStreak"]);
        }

        [Fact]
        public void AllFrontsMasteredCountsAtMostFiftyWinsOnEachMap()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            db.WriteCareer(ids[0], new Dictionary<string, long> { ["winsDustbowl"] = 80, ["winsIsland"] = 50, ["winsForestLake"] = 49 });
            Assert.DoesNotContain("all_fronts_mastered", Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400))));

            List<string> earned = career.RecordRound(ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", 400)),
                0, 0, 0, mapId: 3, night: false, final: true, now: 1);
            Assert.Contains("all_fronts_mastered", earned);
        }

        [Fact]
        public void ThreeFrontsAndArmourerAreCountedByParts()
        {
            (CareerService career, _, int[] ids) = Players(1);
            Dictionary<string, long> finished = Facts(("finished", 1), ("secondsPlayed", 400));

            career.RecordRound(ids[0], finished, 0, 0, 0, 1, false, true, 1);
            career.RecordRound(ids[0], finished, 0, 0, 0, 2, false, true, 2);
            Assert.Contains("three_fronts", career.RecordRound(ids[0], finished, 0, 0, 0, 3, false, true, 3));

            long eleven = Weapons(WeaponIds.RK44, WeaponIds.SIND7, WeaponIds.SIND7_SUPPRESSED, WeaponIds.EAGLE_76,
                WeaponIds.SL_DEFENDER, WeaponIds.SIGNAL_DMR, WeaponIds.RECON_LRR, WeaponIds.FRAG, WeaponIds.SPEARHEAD,
                WeaponIds.BEU_AW1, WeaponIds.BIL_SCALPEL);
            Assert.DoesNotContain("armourer", Round(career, ids[0], Facts(("weaponKillMask", eleven))));
            Assert.Contains("armourer", Round(career, ids[0], Facts(("weaponKillMask", Weapons(WeaponIds.WRENCH)))));
        }

        [Fact]
        public void IroncladFollowsTheOtherSeventyNine()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            foreach (Achievement a in AchievementCatalog.All.Where(a => a.Id != "ironclad" && a.Id != "roll_call"))
                Assert.True(db.InsertAchievement(ids[0], a.Id, 1));

            Assert.Contains("ironclad", Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400))));
        }

        // ------------------------------------------------------------------ claims and views

        [Fact]
        public void OnlyPracticeAchievementsAndPracticeNumbersAreClaimed()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            List<string> earned = career.Claim(ids[0], new[] { "cadet", "grim_arithmetic", "nope" },
                new Dictionary<string, long> { ["prMapsFinished"] = 14, ["kills"] = 9999, ["prHellWeekBest"] = 40 }, now: 3);

            Assert.Equal(new[] { "cadet" }, earned);
            Dictionary<string, long> stats = db.ReadCareer(ids[0]);
            Assert.Equal(14, stats["prMapsFinished"]);
            Assert.Equal(40, stats["prHellWeekBest"]);
            Assert.False(stats.ContainsKey("kills"));

            career.Claim(ids[0], null, new Dictionary<string, long> { ["prHellWeekBest"] = 20 }, now: 4);
            Assert.Equal(40, db.ReadCareer(ids[0])["prHellWeekBest"]);
        }

        [Fact]
        public void RetiredAchievementRowsAreDeletedAndCareersKept()
        {
            var database = new SqliteDatabase(":memory:");
            Assert.True(database.InsertAccount("old", Password, "Old", 0));
            int id = database.FindAccount("old")!.PlayerId;
            database.WriteCareer(id, new Dictionary<string, long> { ["kills"] = 120 });
            Assert.True(database.InsertAchievement(id, "first_blood", 1));

            var career = new CareerService(database);

            Assert.Empty(database.ReadAchievements(id));
            Assert.Contains("baptism_of_fire", Round(career, id, Facts(("kills", 1))));
        }

        [Fact]
        public void AHiddenMythicsFirstHolderIsShownOnlyToAnotherHolder()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(3);
            Assert.True(db.InsertAchievement(ids[0], "mid_air", 100));
            Assert.True(db.InsertAchievement(ids[1], "mid_air", 200));
            Assert.True(db.InsertAchievement(ids[0], "curvature", 50));

            AchievementsView holder = career.Achievements(ids[1]);
            AchievementsView stranger = career.Achievements(ids[2]);

            Assert.Equal("Player0", holder.Firsts["mid_air"].Name);
            Assert.False(stranger.Firsts.ContainsKey("mid_air"));
            Assert.Equal("Player0", stranger.Firsts["curvature"].Name);
        }

        [Fact]
        public void TheRankingIsByScoreThenKillsThenFewerDeaths()
        {
            (CareerService career, _, int[] ids) = Players(3);
            Round(career, ids[0], Facts(("score", 10), ("kills", 5), ("deaths", 2), ("finished", 1)));
            Round(career, ids[1], Facts(("score", 10), ("kills", 5), ("deaths", 1), ("finished", 1)));
            Round(career, ids[2], Facts(("score", 12), ("kills", 1), ("deaths", 9), ("finished", 1)));

            LeaderboardView board = career.Leaderboard(ids[0]);

            Assert.Equal(new[] { ids[2], ids[1], ids[0] }, board.Top.Select(t => t.Row.PlayerId));
            Assert.Equal(3, board.You!.Value.Rank);
        }

        // ------------------------------------------------------------------ docs and the game

        [Fact]
        public void TheWikiPageNamesEveryAchievementWithItsBadge()
        {
            // docs/achievements-wiki.md is generated from the catalogue (tools/ui/write_achievements_doc.py);
            // a new or renamed achievement without a re-run is caught here.
            string page = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "docs", "achievements-wiki.md"));
            foreach (Achievement achievement in AchievementCatalog.All)
            {
                Assert.Contains("**" + achievement.Title + "**", page);
                Assert.Contains("`" + achievement.Id + "`", page);
                Assert.Contains("Achievements/" + achievement.Id + (achievement.Hidden ? "_shadow" : string.Empty) + ".png", page);
                Assert.Contains(achievement.Hidden ? achievement.Teaser : achievement.Description, page);
            }
        }

        [Fact]
        public void TheDesignDocumentNamesEveryAchievement()
        {
            string page = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "docs", "achievements.md"));
            foreach (Achievement achievement in AchievementCatalog.All) Assert.Contains(achievement.Title, page);
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

        // ------------------------------------------------------------------ helpers

        private static List<string> Round(CareerService career, int playerId, Dictionary<string, long> facts)
            => career.RecordRound(playerId, facts, 0, 0, 0, mapId: 1, night: false, final: true, now: 1);

        private static long Weapons(params byte[] ids) => ids.Aggregate(0L, (mask, id) => mask | (1L << id));

        private static string RepoRoot()
        {
            string? dir = System.AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Ironfront.sln"))) dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? throw new System.InvalidOperationException("repository root not found");
        }

        private static Dictionary<string, long> Facts(params (string Key, long Value)[] facts)
            => facts.ToDictionary(s => s.Key, s => s.Value);

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
