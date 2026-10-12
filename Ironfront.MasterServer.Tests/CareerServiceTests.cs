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

        /// <summary>Every id v4.6.0 shipped: a player may hold any of them, on the master or in a save file.</summary>
        private static readonly string[] ShippedIds =
        {
            "roll_call", "lights_out", "baptism_of_fire", "steady_hand", "flag_runner", "taste_of_victory",
            "speed_bump", "by_the_book", "cadet", "turncoat", "victory_lap", "bullet_sponge", "participation_trophy",
            "three_fronts", "unbroken", "predator", "dead_centre", "overwatch", "steel_rain", "rotorhead",
            "forward_supply", "night_shift", "cannon_fodder", "dust_devil", "first_past_the_post", "boots_only",
            "nine_lives", "man_overboard", "mutual_destruction", "grim_arithmetic", "juggernaut", "crowd_control",
            "cold_steel", "armourer", "can_opener", "long_campaign", "top_brass", "clean_sheet", "naked_eye",
            "night_terror", "hell_week", "island_hopper", "lake_monster", "graveyard_shift", "motor_pool",
            "jack_of_all_trades", "touchdown", "buckshot_sniper", "dogfight", "gold_standard", "windreader",
            "all_fronts_mastered", "air_defense", "undefeated", "moonlight_marksman", "outnumbered",
            "on_borrowed_time", "pacifist", "nemesis", "absolute_dominance", "drill_sergeant", "grand_tour",
            "impossible_angle", "from_the_grave", "centurion", "rampage", "curvature", "perfect_ten", "dead_eye",
            "untouchable", "blade_only", "tank_ace", "sky_king", "map_painter", "hail_mary", "creature_of_the_night",
            "immaculate", "ironclad", "mid_air", "counter_sniper",
        };

        [Fact]
        public void AnIdOncePlayersCanHoldItNeverLeavesTheCatalogue()
        {
            // Owner, 2026-10-10: an update never costs a player an achievement. Change an achievement's
            // rule, title or tier as much as you like; its id is the key every database and save file
            // holds it under, so it never changes and never goes. The master keeps rows for an unknown
            // id and the client keeps unknown ids, but only the catalogue draws them.
            var ids = new HashSet<string>(AchievementCatalog.All.Select(a => a.Id));
            foreach (string id in ShippedIds)
                Assert.True(ids.Contains(id), $"'{id}' shipped in v4.6.0 and left the catalogue: players holding it would stop seeing it");
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
        public void AFinishedRoundIsARollCallHoweverShortItWas()
        {
            // Owner's run of 2026-10-12: no minute limits, and no "join before anyone scores". A
            // 40-second round in a big enough room counts, and so does one a player loaded into
            // 30 seconds before its end: the room's size and being there at the end are what matter.
            (CareerService career, SqliteDatabase db, int[] ids) = Players(3);

            List<string> fast = Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 40)));
            List<string> late = Round(career, ids[1], Facts(("finished", 1), ("secondsPlayed", 30)));
            List<string> small = career.RecordRound(ids[2], Facts(("finished", 1), ("secondsPlayed", 900)), 0, 0, 0,
                mapId: 1, night: false, final: true, now: 1, VictoryRule.Margin, 90);

            Assert.Contains("roll_call", fast);
            Assert.Contains("roll_call", late);
            Assert.DoesNotContain("roll_call", small);
            Assert.Equal(1, db.ReadCareer(ids[1])["roundsFinished"]);
            Assert.False(db.ReadCareer(ids[2]).ContainsKey("roundsFinished"));
        }

        [Theory]
        [InlineData(AchievementTier.Bronze, 100, 250)]
        [InlineData(AchievementTier.Silver, 120, 300)]
        [InlineData(AchievementTier.Gold, 150, 400)]
        [InlineData(AchievementTier.Platinum, 200, 500)]
        [InlineData(AchievementTier.Mythic, 400, 1000)]
        public void ARoundIsOfASizeFromTheOwnersPoints(AchievementTier size, int leadBy, int firstTo)
        {
            Assert.True(CareerRules.IsRoundOf(size, VictoryRule.Margin, leadBy));
            Assert.False(CareerRules.IsRoundOf(size, VictoryRule.Margin, leadBy - 10));
            Assert.True(CareerRules.IsRoundOf(size, VictoryRule.Target, firstTo));
            Assert.False(CareerRules.IsRoundOf(size, VictoryRule.Target, firstTo - 50));
            Assert.False(CareerRules.IsRoundOf(size, VictoryRule.Margin, 0));
        }

        [Fact]
        public void EveryRoundAchievementNamesTheRoomPointsOfItsSize()
        {
            // The description is the rule: a size changed in CareerRules and not in the text (or the
            // other way round) fails here, for all thirty of them at once.
            var sized = AchievementCatalog.All.Where(a => a.RoundSize != null).ToList();
            Assert.Equal(28, sized.Count);
            foreach (Achievement a in sized)
            {
                AchievementTier size = a.RoundSize!.Value;
                string target = $"FIRST TO {CareerRules.TargetPoints(size).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}";
                Assert.True(a.Description.Contains(target), $"{a.Id}: '{target}' missing from \"{a.Description}\"");
                if (a.Id != "first_past_the_post")
                    Assert.True(a.Description.Contains($"LEAD BY {CareerRules.MarginPoints(size)}"), a.Id);
                Assert.DoesNotContain("anyone scores", a.Description);
                Assert.DoesNotContain("minute", a.Description);
            }
            foreach (Achievement a in AchievementCatalog.All) Assert.DoesNotContain("minute", a.Description);
        }

        [Fact]
        public void LongCampaignCountsGoldSizeWinsOnly()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            Dictionary<string, long> win = Facts(("finished", 1), ("won", 1));

            career.RecordRound(ids[0], win, 0, 0, 0, 1, false, true, 1, VictoryRule.Margin, 140);
            career.RecordRound(ids[0], win, 0, 0, 0, 1, false, true, 2, VictoryRule.Margin, 150);
            career.RecordRound(ids[0], win, 0, 0, 0, 1, false, true, 3, VictoryRule.Target, 400);

            Assert.Equal(3, db.ReadCareer(ids[0])["roundsWon"]);
            Assert.Equal(2, db.ReadCareer(ids[0])["goldRoundsWon"]);
        }

        [Fact]
        public void GoldWinsStartFromTheRoundWinsAlreadyCountedOnce()
        {
            var database = new SqliteDatabase(":memory:");
            Assert.True(database.InsertAccount("pl0", Password, "Player0", 0));
            int id = database.FindAccount("pl0")!.PlayerId;
            database.WriteCareer(id, new Dictionary<string, long> { ["roundsWon"] = 7 });

            _ = new CareerService(database);
            Assert.Equal(7, database.ReadCareer(id)["goldRoundsWon"]);

            database.WriteCareer(id, new Dictionary<string, long> { ["roundsWon"] = 9, ["goldRoundsWon"] = 8 });
            _ = new CareerService(database);
            Assert.Equal(8, database.ReadCareer(id)["goldRoundsWon"]);
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
                15, 0, 15, mapId: 1, night: false, final: false, now: 5, DefaultRule, DefaultPoints);

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

            Assert.DoesNotContain("clean_sheet", career.RecordRound(ids[0], facts, 12, 0, 12, 1, false, final: false, now: 1, DefaultRule, DefaultPoints));
            Assert.Contains("clean_sheet", career.RecordRound(ids[0], facts, 12, 0, 12, 1, false, final: true, now: 2, DefaultRule, DefaultPoints));
        }

        [Theory]
        [InlineData(150, 10, 0, true)]
        [InlineData(140, 10, 0, false)]
        [InlineData(150, 9, 0, false)]
        [InlineData(150, 10, 1, false)]
        public void CleanSheetNeedsAGoldSizeRoomTenKillsAndNoDeath(int leadBy, long kills, long deaths, bool earned)
        {
            (CareerService career, _, int[] ids) = Players(1);
            List<string> got = career.RecordRound(ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", 120),
                ("botKills", kills), ("deaths", deaths)), 0, 0, 0, 1, false, true, 1, VictoryRule.Margin, leadBy);
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

            List<string> got = career.RecordRound(ids[0], facts, 10, 0, 10, 3, night: true, final: true, now: 1, DefaultRule, DefaultPoints);

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
        [InlineData(1, 3, VictoryRule.Margin, 200, true)]
        [InlineData(1, 3, VictoryRule.Target, 500, true)]
        [InlineData(2, 3, VictoryRule.Margin, 200, false)]
        [InlineData(1, 2, VictoryRule.Margin, 200, false)]
        [InlineData(1, 3, VictoryRule.Margin, 190, false)]
        [InlineData(1, 3, VictoryRule.Target, 450, false)]
        public void OutnumberedIsTheOnlyHumanOnTheSideAgainstThreeInAPlatinumRoom(
            long own, long enemy, VictoryRule rule, int points, bool earned)
        {
            // The owner's 1v3 of 2026-10-12 was won in 593 s and refused by the old ten minutes.
            (CareerService career, _, int[] ids) = Players(1);
            List<string> got = career.RecordRound(ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", 593),
                ("humansOwnSide", own), ("humansEnemySide", enemy)),
                0, 0, 0, 2, false, true, 1, rule, points);
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
            List<string> got = MythicRound(career, ids[0], Facts(("finished", 1), ("won", 1), ("secondsPlayed", 400),
                ("botKills", kills), ("shots", shots), ("hits", hits)));
            Assert.Equal(earned, got.Contains("dead_eye"));
        }

        [Fact]
        public void UntouchableNeedsAZeroThatWasReported()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> basis = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 900), ("botKills", 15));

            Assert.DoesNotContain("untouchable", MythicRound(career, ids[0], basis));
            basis["damageTaken"] = 0;
            Assert.DoesNotContain("untouchable", Round(career, ids[1], basis));
            Assert.Contains("untouchable", MythicRound(career, ids[1], basis));
        }

        [Fact]
        public void MapPainterHelpsTakeEveryFlagWithoutDyingAndWins()
        {
            (CareerService career, _, int[] ids) = Players(2);
            Dictionary<string, long> Basis(long helped) => Facts(("finished", 1), ("won", 1), ("secondsPlayed", 400),
                ("mapFlags", 5), ("flagsHelpedDistinct", helped));

            Assert.Contains("map_painter", MythicRound(career, ids[0], Basis(5)));
            Assert.DoesNotContain("map_painter", MythicRound(career, ids[1], Basis(4)));
        }

        [Fact]
        public void BladeOnlyKeepsTheBestAllMeleeRound()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            MythicRound(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400), ("botKills", 24), ("meleeKills", 24)));
            MythicRound(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400), ("botKills", 30), ("meleeKills", 29)));
            Assert.Equal(24, db.ReadCareer(ids[0])["bladeOnlyBest"]);

            Assert.DoesNotContain("blade_only", Round(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400),
                ("botKills", 25), ("meleeKills", 25))));
            Assert.Contains("blade_only", MythicRound(career, ids[0], Facts(("finished", 1), ("secondsPlayed", 400),
                ("botKills", 25), ("meleeKills", 25))));
        }

        [Fact]
        public void NightOnlyBestsCountOnlyAtNight()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            Dictionary<string, long> facts = Facts(("meleeKills", 10), ("longestHeadshotMetres", 320), ("botKills", 10));

            List<string> day = career.RecordRound(ids[0], facts, 10, 0, 10, 1, night: false, final: true, now: 1, DefaultRule, DefaultPoints);
            Assert.DoesNotContain("night_terror", day);
            Assert.DoesNotContain("moonlight_marksman", day);

            List<string> night = career.RecordRound(ids[0], facts, 10, 0, 10, 3, night: true, final: true, now: 2, DefaultRule, DefaultPoints);
            Assert.Contains("night_terror", night);
            Assert.Contains("moonlight_marksman", night);
            Assert.Equal(10, db.ReadCareer(ids[0])["nightKills"]);
        }

        [Fact]
        public void UndefeatedCountsWinsInARowAndAnyCountedRoundWithoutAWinResets()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            Dictionary<string, long> win = Facts(("finished", 1), ("won", 1), ("secondsPlayed", 90));
            Dictionary<string, long> loss = Facts(("finished", 1), ("won", 0), ("secondsPlayed", 400));
            Dictionary<string, long> left = Facts(("finished", 0), ("won", 0));

            for (int i = 0; i < 9; i++) Round(career, ids[0], win);
            career.RecordRound(ids[0], loss, 0, 0, 0, 1, false, true, 1, VictoryRule.Margin, 190);
            Assert.Equal(9, db.ReadCareer(ids[0])["winStreak"]);
            Round(career, ids[0], left);
            Assert.Equal(0, db.ReadCareer(ids[0])["winStreak"]);
            for (int i = 0; i < 3; i++) Round(career, ids[0], win);
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
                0, 0, 0, mapId: 3, night: false, final: true, now: 1, DefaultRule, DefaultPoints);
            Assert.Contains("all_fronts_mastered", earned);
        }

        [Fact]
        public void ThreeFrontsAndArmourerAreCountedByParts()
        {
            (CareerService career, _, int[] ids) = Players(1);
            Dictionary<string, long> finished = Facts(("finished", 1), ("secondsPlayed", 400));

            career.RecordRound(ids[0], finished, 0, 0, 0, 1, false, true, 1, VictoryRule.Target, 300);
            career.RecordRound(ids[0], finished, 0, 0, 0, 2, false, true, 2, VictoryRule.Margin, 110);
            career.RecordRound(ids[0], finished, 0, 0, 0, 2, false, true, 2, VictoryRule.Margin, 120);
            Assert.Contains("three_fronts", career.RecordRound(ids[0], finished, 0, 0, 0, 3, false, true, 3, DefaultRule, DefaultPoints));

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
        public void ANewSeasonResetsTheOnlineCareerOnceAndKeepsAchievementsAndPractice()
        {
            // Owner, 2026-10-11: season 1's online numbers were double-counted and incomplete, so the
            // ranking starts again. What a player holds, and what practice counted, stays.
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"season-{System.Guid.NewGuid():N}.db");
            int id;
            using (var old = new SqliteDatabase(path))
            {
                Assert.True(old.InsertAccount("veteran", Password, "Veteran", 0));
                id = old.FindAccount("veteran")!.PlayerId;
                old.WriteCareer(id, new Dictionary<string, long> { ["kills"] = 1005, ["wins"] = 7, ["prGuidePages"] = 3 });
                Assert.True(old.InsertAchievement(id, "cadet", 1));
            }
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                // A season-1 database: written before seasons were recorded.
                raw.Open();
                using var drop = raw.CreateCommand();
                drop.CommandText = "DROP TABLE career_meta";
                drop.ExecuteNonQuery();
            }

            using var database = new SqliteDatabase(path);
            Assert.Equal(1, database.ReadCareerSeason());
            _ = new CareerService(database);

            Assert.Equal(SqliteDatabase.CurrentCareerSeason, database.ReadCareerSeason());
            Assert.Equal(new Dictionary<string, long> { ["prGuidePages"] = 3 }, database.ReadCareer(id));
            Assert.Contains(database.ReadAchievements(id), row => row.Id == "cadet");
            Assert.Empty(database.ReadCareerRows());

            // Once: a later start keeps the new season's numbers.
            database.WriteCareer(id, new Dictionary<string, long> { ["kills"] = 5 });
            _ = new CareerService(database);
            Assert.Equal(5, database.ReadCareer(id)["kills"]);
        }

        [Fact]
        public void AFreshDatabaseStartsInTheCurrentSeason()
        {
            var database = new SqliteDatabase(":memory:");
            Assert.Equal(SqliteDatabase.CurrentCareerSeason, database.ReadCareerSeason());
        }

        [Fact]
        public void AchievementsCountEveryClosedSeasonWhileTheRankingCountsThisOne()
        {
            // Owner's run of 2026-10-12: STEEL RAIN sat at 0 after season 2 archived 17 tank kills.
            (SqliteDatabase db, int id) = SeasonOnePlayer(new Dictionary<string, long>
            {
                ["kills"] = 100, ["botKills"] = 40, ["playerKills"] = 10, ["deaths"] = 20, ["headshots"] = 30,
                ["score"] = 300, ["tankKills"] = 99, ["longestKillMetres"] = 250, ["matches"] = 4, ["winStreak"] = 3,
                ["weaponKillMask"] = 2,
            });
            var career = new CareerService(db);

            Dictionary<string, long> history = db.ReadCareerHistory(id);
            Assert.Equal(50, history["kills"]);       // exact: bot + player kills
            Assert.Equal(10, history["deaths"]);      // scaled by 50 / 100
            Assert.Equal(15, history["headshots"]);
            Assert.Equal(150, history["score"]);
            Assert.Equal(99, history["tankKills"]);
            Assert.False(history.ContainsKey("winStreak"));
            Assert.Empty(career.Leaderboard(id).Top);

            List<string> earned = Round(career, id, Facts(("finished", 1), ("tankKills", 1), ("botKills", 1),
                ("longestKillMetres", 310), ("weaponKillMask", 4)));
            Assert.Contains("steel_rain", earned);
            Assert.Contains("overwatch", earned);
            Assert.Equal(1, db.ReadCareer(id)["tankKills"]);
            Assert.Equal(100, career.Achievements(id).Career["tankKills"]);
            Assert.Equal(6, career.Achievements(id).Career["weaponKillMask"]);
            Assert.Equal(1, career.Leaderboard(id).Top.Single().Row.Matches);
        }

        [Fact]
        public void AClosedSeasonIsFoldedIntoTheHistoryOnce()
        {
            (SqliteDatabase db, int id) = SeasonOnePlayer(new Dictionary<string, long> { ["tankKills"] = 7 });
            _ = new CareerService(db);
            _ = new CareerService(db);
            Assert.Equal(7, db.ReadCareerHistory(id)["tankKills"]);
        }

        [Fact]
        public void SeasonOneKeepsItsNumbersWhenTheyWereNeverInflated()
        {
            Dictionary<string, long> corrected = CareerService.CorrectSeasonOne(new Dictionary<string, long>
            {
                ["kills"] = 4, ["botKills"] = 4, ["deaths"] = 1,
            });
            Assert.Equal(4, corrected["kills"]);
            Assert.Equal(1, corrected["deaths"]);
        }

        /// <summary>A database from before season 2, holding one player's season-1 numbers.</summary>
        private static (SqliteDatabase, int) SeasonOnePlayer(Dictionary<string, long> numbers)
        {
            var database = new SqliteDatabase(":memory:");
            database.WriteCareerMeta("season", 1);
            Assert.True(database.InsertAccount("veteran", Password, "Veteran", 0));
            int id = database.FindAccount("veteran")!.PlayerId;
            database.WriteCareer(id, numbers);
            return (database, id);
        }

        [Fact]
        public void ACorrectionIsFoldedOnceByEachStatsOwnRuleAndTheAchievementFollows()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);
            db.WriteCareer(ids[0], new Dictionary<string, long> { ["roundsWon"] = 2, ["bestWinStreak"] = 3, ["prGrandTour"] = 94 });
            var corrections = new[]
            {
                new CareerCorrection("pl0", CareerStat.OutnumberedWins, 1, "test"),
                new CareerCorrection("pl0", CareerStat.RoundsWon, 1, "test"),
                new CareerCorrection("pl0", CareerStat.BestWinStreak, 2, "test"),
                new CareerCorrection("pl0", CareerStat.PrGrandTour, 1, "test"),
                new CareerCorrection("nobody", CareerStat.RoundsWon, 1, "test"),
            };

            career.ApplyCorrectionsOnce("test-marker", corrections);
            career.ApplyCorrectionsOnce("test-marker", corrections);

            Dictionary<string, long> numbers = db.ReadCareer(ids[0]);
            Assert.Equal(1, numbers["outnumberedWins"]);
            Assert.Equal(3, numbers["roundsWon"]);       // added, once
            Assert.Equal(3, numbers["bestWinStreak"]);   // a best is never lowered
            Assert.Equal(95, numbers["prGrandTour"]);    // parts joined
            Assert.Contains("outnumbered", career.Claim(ids[0], null, null, now: 9));
        }

        [Fact]
        public void TheOwnersRunCorrectionsNameRealStatsAndCarryEvidence()
        {
            Assert.NotEmpty(CareerCorrections.OwnerRun20261012);
            foreach (CareerCorrection correction in CareerCorrections.OwnerRun20261012)
            {
                Assert.False(string.IsNullOrWhiteSpace(correction.Username));
                Assert.True(correction.Value > 0);
                Assert.True(correction.Evidence.Length > 20, correction.Username + " " + correction.Stat);
            }
        }

        [Fact]
        public void AnAchievementTheCatalogueNoLongerHasIsKeptAndNotCounted()
        {
            // Owner, 2026-10-10: an update never costs a player an achievement. A row for an id this
            // build does not know survives the master's start, every judging and every read.
            var database = new SqliteDatabase(":memory:");
            Assert.True(database.InsertAccount("old", Password, "Old", 0));
            int id = database.FindAccount("old")!.PlayerId;
            database.WriteCareer(id, new Dictionary<string, long> { ["kills"] = 120 });
            Assert.True(database.InsertAchievement(id, "first_blood", 1));

            var career = new CareerService(database);
            // A leaver's round: it feeds the kills, and earns no round achievement.
            Assert.Contains("baptism_of_fire", Round(career, id, Facts(("kills", 1), ("finished", 0))));
            career.Claim(id, new[] { "cadet" }, null, now: 5);

            Assert.Contains(database.ReadAchievements(id), row => row.Id == "first_blood" && row.At == 1);
            Assert.Contains(career.Achievements(id).Unlocked, row => row.Id == "first_blood");
            AchievementTotals totals = AchievementTotals.Of(database.ReadAchievements(id).Select(row => row.Id));
            Assert.Equal(2, totals.Count);
        }

        [Fact]
        public void APracticeAchievementIsJudgedFromTheNumbersAClaimCarries()
        {
            // A practice achievement that reads a number is judged by today's rule from the claimed
            // numbers, so a threshold an update lowered unlocks it at the next sign-in even though
            // the player's game never sent its id.
            (CareerService career, SqliteDatabase db, int[] ids) = Players(1);

            List<string> earned = career.Claim(ids[0], null,
                new Dictionary<string, long> { ["prDustDevilBest"] = 40, ["prMapsFinished"] = 14 }, now: 3);

            Assert.Contains("dust_devil", earned);
            Assert.Contains("cadet", earned);
            Assert.DoesNotContain("hell_week", earned);
            Assert.DoesNotContain("gold_standard", earned);
            Assert.Contains(db.ReadAchievements(ids[0]), row => row.Id == "dust_devil");
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

        // ------------------------------------------------------------------ comparing players (section 6.2)

        [Fact]
        public void AProfileKeepsAHiddenAchievementAndItsProgressFromAViewerWhoLacksIt()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(3);
            int target = ids[0], stranger = ids[1], holder = ids[2];
            Round(career, target, Facts(("kills", 12), ("finished", 1), ("secondsPlayed", 400)));
            db.WriteCareer(target, new Dictionary<string, long> { ["longestShotgunKillMetres"] = 40 });
            Assert.True(db.InsertAchievement(target, "buckshot_sniper", 7));
            Assert.True(db.InsertAchievement(holder, "buckshot_sniper", 9));
            Assert.True(AchievementCatalog.Find("buckshot_sniper")!.Hidden);

            ProfileView seenByStranger = career.Profile(stranger, target)!;
            ProfileView seenByHolder = career.Profile(holder, target)!;

            Assert.DoesNotContain(seenByStranger.Unlocked, u => u.Id == "buckshot_sniper");
            Assert.False(seenByStranger.Career.ContainsKey("longestShotgunKillMetres"));
            Assert.Equal(12, seenByStranger.Career["kills"]);
            Assert.Equal(1, seenByStranger.Totals.Hidden);
            Assert.Contains(seenByStranger.Unlocked, u => u.Id == "roll_call");
            Assert.Equal(seenByStranger.Unlocked.Count + 1, seenByStranger.Totals.Count);

            Assert.Contains(seenByHolder.Unlocked, u => u.Id == "buckshot_sniper" && u.At == 7);
            Assert.Equal(40, seenByHolder.Career["longestShotgunKillMetres"]);
        }

        [Fact]
        public void AStatAnOpenAchievementAlsoReadsIsNeverHidden()
        {
            var open = new HashSet<CareerStat>(AchievementCatalog.All.Where(a => !a.Hidden).SelectMany(a => a.StatsRead()));
            (CareerService career, SqliteDatabase db, int[] ids) = Players(2);
            var everything = new Dictionary<string, long>();
            for (int i = 0; i < CareerStats.Count; i++) everything[CareerStats.Key((CareerStat)i)] = 1;
            db.WriteCareer(ids[0], everything);

            ProfileView view = career.Profile(ids[1], ids[0])!;

            foreach (CareerStat stat in open) Assert.True(view.Career.ContainsKey(CareerStats.Key(stat)), CareerStats.Key(stat));
            Assert.Contains(AchievementCatalog.All.Where(a => a.Hidden).SelectMany(a => a.StatsRead()),
                stat => !view.Career.ContainsKey(CareerStats.Key(stat)));
        }

        [Fact]
        public void AProfileOfNoSuchAccountIsNullAndAnUnrankedOneHasRankZero()
        {
            (CareerService career, _, int[] ids) = Players(2);

            Assert.Null(career.Profile(ids[0], 999_999));
            ProfileView fresh = career.Profile(ids[0], ids[1])!;
            Assert.Equal(0, fresh.Rank);
            Assert.Equal("Player1", fresh.Row.Name);
            Assert.Equal(0, fresh.Totals.Count);
        }

        [Fact]
        public void TheRankingCountsEachPlayersAchievementsPointsAndMythics()
        {
            (CareerService career, SqliteDatabase db, int[] ids) = Players(2);
            Round(career, ids[0], Facts(("score", 10), ("finished", 1)));
            Round(career, ids[1], Facts(("score", 5), ("finished", 1)));
            Assert.True(db.InsertAchievement(ids[0], "curvature", 3));
            Assert.True(db.InsertAchievement(ids[0], "buckshot_sniper", 4));

            LeaderboardView board = career.Leaderboard(ids[1]);
            AchievementTotals first = board.Totals[ids[0]];
            List<string> held = db.ReadAchievements(ids[0]).Select(a => a.Id).ToList();

            Assert.Equal(held.Count, first.Count);
            Assert.Equal(held.Sum(id => AchievementCatalog.Find(id)!.Points), first.Points);
            Assert.Equal(1, first.Mythics);
            Assert.Equal(1, first.Hidden);
            Assert.True(board.Totals.ContainsKey(ids[1]));
        }

        [Fact]
        public void TotalsIgnoreUnknownIdsAndRepeats()
        {
            AchievementTotals totals = AchievementTotals.Of(new[] { "roll_call", "roll_call", "no_such_thing", "curvature" });

            Assert.Equal(2, totals.Count);
            Assert.Equal(AchievementCatalog.Find("roll_call")!.Points + AchievementCatalog.Find("curvature")!.Points, totals.Points);
            Assert.Equal(new[] { 1, 0, 0, 0, 1 }, totals.PerTier);
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
        public void EveryAchievementHasItsBadgeEveryHiddenOneItsSilhouetteAndNothingIsLeftOver()
        {
            // Rendered by tools/ui/make_icons.py --badges from tools/ui/badges.py; the game loads them
            // from Resources by id (AchievementArt.Badge), so a missing file is a blank square in game.
            string folder = System.IO.Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi", "Achievements");
            var expected = new HashSet<string> { "_hidden" };
            foreach (Achievement achievement in AchievementCatalog.All)
            {
                expected.Add(achievement.Id);
                if (achievement.Hidden) expected.Add(achievement.Id + "_shadow");
            }
            var present = new HashSet<string>(System.IO.Directory.GetFiles(folder, "*.png")
                .Select(path => System.IO.Path.GetFileNameWithoutExtension(path)!));

            Assert.Empty(expected.Except(present).OrderBy(x => x));
            Assert.Empty(present.Except(expected).OrderBy(x => x));
            foreach (string name in present) Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, name + ".png.meta")), name);
        }

        [Fact]
        public void EveryMetalAndTheDisastersHaveTheirSound()
        {
            // AchievementToast plays IronfrontUi/achievement-<metal> (or -disaster), falling back to the chime,
            // and a hidden one's -hidden sting before its metal.
            string folder = System.IO.Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Resources", "IronfrontUi");
            foreach (string name in new[] { "bronze", "silver", "gold", "platinum", "mythic", "hidden", "disaster", "unlocked" })
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "achievement-" + name + ".wav")), name);
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

        /// <summary>The lobby's default room: LEAD BY 200, Platinum size.</summary>
        private const VictoryRule DefaultRule = VictoryRule.Margin;

        private const int DefaultPoints = 200;

        private static List<string> Round(CareerService career, int playerId, Dictionary<string, long> facts)
            => career.RecordRound(playerId, facts, 0, 0, 0, mapId: 1, night: false, final: true, now: 1, DefaultRule, DefaultPoints);

        /// <summary>A Mythic-size room: LEAD BY 400.</summary>
        private static List<string> MythicRound(CareerService career, int playerId, Dictionary<string, long> facts)
            => career.RecordRound(playerId, facts, 0, 0, 0, mapId: 1, night: false, final: true, now: 1, VictoryRule.Margin, 400);

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
