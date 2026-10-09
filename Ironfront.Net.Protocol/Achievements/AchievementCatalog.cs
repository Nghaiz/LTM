using System.Collections.Generic;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>
    /// The eighty achievements (achievements v2, <c>docs/achievements.md</c>, approved by the owner on
    /// 2026-10-09): one list, easiest first, shared by the master that judges them and the client that
    /// draws them, so a title, a number or a rule cannot differ between the two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every description is the rule <see cref="CareerRules"/> applies,</b> numbers included. A
    /// change to a threshold changes both, here and there, in one commit.
    /// </para>
    /// <para>
    /// Badges are rendered by <c>tools/ui/make_icons.py</c> from <c>tools/ui/badges.py</c>, one per
    /// <see cref="Achievement.Id"/>; <c>docs/achievements.md</c> is written by
    /// <c>tools/ui/write_achievements_doc.py</c>.
    /// </para>
    /// </remarks>
    public static class AchievementCatalog
    {
        private const AchievementTags On = AchievementTags.Online;
        private const AchievementTags Pr = AchievementTags.Practice;
        private const AchievementTags Night = AchievementTags.Night;
        private const AchievementTags Hid = AchievementTags.Hidden;
        private const AchievementTags Sad = AchievementTags.Disaster;

        private const AchievementTier B = AchievementTier.Bronze;
        private const AchievementTier S = AchievementTier.Silver;
        private const AchievementTier G = AchievementTier.Gold;
        private const AchievementTier P = AchievementTier.Platinum;
        private const AchievementTier M = AchievementTier.Mythic;

        /// <summary>The three maps, as parts: the bit is the map's id.</summary>
        private static readonly AchievementPart[] Maps =
        {
            new AchievementPart("DUSTBOWL", 1),
            new AchievementPart("ISLAND", 2),
            new AchievementPart("FOREST LAKE", 3),
        };

        /// <summary>ARMOURER's twelve weapons: the bit is the weapon's id.</summary>
        private static readonly AchievementPart[] Weapons =
        {
            new AchievementPart("RK-44", WeaponIds.RK44),
            new AchievementPart("S-IND7", WeaponIds.SIND7),
            new AchievementPart("S-IND7 [SUP]", WeaponIds.SIND7_SUPPRESSED),
            new AchievementPart("76 EAGLE", WeaponIds.EAGLE_76),
            new AchievementPart("SL-DEFENDER", WeaponIds.SL_DEFENDER),
            new AchievementPart("SIGNAL DMR", WeaponIds.SIGNAL_DMR),
            new AchievementPart("RECON LRR", WeaponIds.RECON_LRR),
            new AchievementPart("FRAG", WeaponIds.FRAG),
            new AchievementPart("SPEARHEAD", WeaponIds.SPEARHEAD),
            new AchievementPart("BEU AW1", WeaponIds.BEU_AW1),
            new AchievementPart("BIL SCALPEL", WeaponIds.BIL_SCALPEL),
            new AchievementPart("WRENCH", WeaponIds.WRENCH),
        };

        /// <summary>TURNCOAT's sides: the bit is the team's id.</summary>
        private static readonly AchievementPart[] Sides =
        {
            new AchievementPart("BLUE SIDE", TeamId.Team0),
            new AchievementPart("RED SIDE", TeamId.Team1),
        };

        /// <summary>
        /// GRAND TOUR's seven wins: bit <c>2 * (mapId - 1) + rule</c> for a map under a rule
        /// (0 LEAD BY, 1 FIRST TO), and bit 6 for a Night Mode win.
        /// </summary>
        private static readonly AchievementPart[] Tour =
        {
            new AchievementPart("DUSTBOWL, LEAD BY", 0),
            new AchievementPart("DUSTBOWL, FIRST TO", 1),
            new AchievementPart("ISLAND, LEAD BY", 2),
            new AchievementPart("ISLAND, FIRST TO", 3),
            new AchievementPart("FOREST LAKE, LEAD BY", 4),
            new AchievementPart("FOREST LAKE, FIRST TO", 5),
            new AchievementPart("NIGHT MODE", 6),
        };

        /// <summary>The guide's eight pages, in tab order: the bit is the tab's index.</summary>
        private static readonly AchievementPart[] GuidePages =
        {
            new AchievementPart("BASICS", 0),
            new AchievementPart("OBJECTIVES", 1),
            new AchievementPart("CONTROLS", 2),
            new AchievementPart("COMBAT", 3),
            new AchievementPart("VEHICLES", 4),
            new AchievementPart("DEPLOY & MAP", 5),
            new AchievementPart("ROOMS & MODES", 6),
            new AchievementPart("TIPS", 7),
        };

        public static readonly IReadOnlyList<Achievement> All = new[]
        {
            // ================================================================= BRONZE (13)
            Feat(1, "roll_call", "ROLL CALL", B, On, CareerStat.RoundsFinished,
                "Finish an online round. You must play at least 5 minutes of it."),
            Feat(2, "lights_out", "LIGHTS OUT", B, On | Night, CareerStat.NightRoundsFinished,
                "Finish an online round in Night Mode. You must play at least 5 minutes of it."),
            Counter(3, "baptism_of_fire", "BAPTISM OF FIRE", B, On, CareerStat.Kills, 100,
                "Get 100 kills in online rounds."),
            Counter(4, "steady_hand", "STEADY HAND", B, On, CareerStat.Headshots, 50,
                "Get 50 headshot kills."),
            Counter(5, "flag_runner", "FLAG RUNNER", B, On, CareerStat.FlagsCaptured, 10,
                "Capture 10 flags: stand in a flag's zone when it turns to your side."),
            Counter(6, "taste_of_victory", "TASTE OF VICTORY", B, On, CareerStat.RoundsWon, 10,
                "Win 10 online rounds. Each must have at least 5 minutes of your play."),
            Counter(7, "speed_bump", "SPEED BUMP", B, On, CareerStat.Roadkills, 10,
                "Run over 10 enemies with a vehicle."),
            PartsOf(8, "by_the_book", "BY THE BOOK", B, Pr, CareerStat.PrGuidePages, GuidePages,
                "Open every page of the How to play guide."),
            PartsOf(9, "cadet", "CADET", B, Pr, CareerStat.PrMapsFinished, Maps,
                "Finish a practice round on each map: Dustbowl, Island and Forest Lake. Each must have at least 5 minutes of your play."),
            PartsOf(10, "turncoat", "TURNCOAT", B, Pr, CareerStat.PrSidesWon, Sides,
                "Win a practice round on the blue side and another on the red side. Each must have at least 5 minutes of your play."),
            Feat(11, "victory_lap", "VICTORY LAP", B, On | Hid, CareerStat.HornAfterRoadkill,
                "Honk the horn within 3 seconds of running an enemy over.",
                "Some victories deserve a little noise."),
            Feat(12, "bullet_sponge", "BULLET SPONGE", B, On | Hid | Sad, CareerStat.BulletSpongeRounds,
                "Die more times than any other human player in an online round with at least 3 human players. You must play at least 5 minutes of it.",
                "Somebody has to soak up the bullets."),
            Feat(13, "participation_trophy", "PARTICIPATION TROPHY", B, On | Hid | Sad, CareerStat.ParticipationRounds,
                "Finish an online round with the lowest score of everyone in it, bots included. You must play at least 5 minutes of it.",
                "Everyone gets a medal. Yes, even you."),

            // ================================================================= SILVER (16)
            PartsOf(14, "three_fronts", "THREE FRONTS", S, On, CareerStat.MapsFinished, Maps,
                "Finish an online round on each map: Dustbowl, Island and Forest Lake. Each must have at least 5 minutes of your play."),
            Best(15, "unbroken", "UNBROKEN", S, On, CareerStat.BestStreak, 15, "kills",
                "Get 15 kills without dying."),
            Counter(16, "predator", "PREDATOR", S, On, CareerStat.PlayerKills, 100,
                "Kill 100 human players."),
            Counter(17, "dead_centre", "DEAD CENTRE", S, On, CareerStat.Headshots, 500,
                "Get 500 headshot kills."),
            Best(18, "overwatch", "OVERWATCH", S, On, CareerStat.LongestKillMetres, 300, "m",
                "Kill an enemy from 300 m or more."),
            Counter(19, "steel_rain", "STEEL RAIN", S, On, CareerStat.TankKills, 100,
                "Get 100 kills from a tank."),
            Counter(20, "rotorhead", "ROTORHEAD", S, On, CareerStat.HelicopterKills, 50,
                "Get 50 kills from a helicopter."),
            Counter(21, "forward_supply", "FORWARD SUPPLY", S, On, CareerStat.Resupplies, 200,
                "Refill or heal teammates 200 times with your AMMO BAG or MEDIPACK."),
            Counter(22, "night_shift", "NIGHT SHIFT", S, On | Night, CareerStat.NightKills, 250,
                "Get 250 kills in Night Mode."),
            Counter(23, "cannon_fodder", "CANNON FODDER", S, On | Sad, CareerStat.Deaths, 1000,
                "Die 1,000 times in online rounds."),
            Best(24, "dust_devil", "DUST DEVIL", S, Pr, CareerStat.PrDustDevilBest, 40, "kills",
                "Get 40 kills in one practice round on Dustbowl with at least 50 bots."),
            Claimed(25, "first_past_the_post", "FIRST PAST THE POST", S, Pr,
                "Win a practice round that uses the FIRST TO win rule, with at least 30 kills of your own and at least 5 minutes of your play."),
            Claimed(26, "boots_only", "BOOTS ONLY", S, Pr,
                "Win a practice round with vehicles turned off and at least 50 bots, with the most kills of anyone in it, after at least 5 minutes of your play."),
            Feat(27, "nine_lives", "NINE LIVES", S, On | Hid, CareerStat.FallsSurvivedLow,
                "Survive a fall with 5 health or less left.",
                "Gravity tried. Gravity failed."),
            Feat(28, "man_overboard", "MAN OVERBOARD", S, On | Hid, CareerStat.BoatRoadkills,
                "Run an enemy over with a boat.",
                "Not every road is made of dirt."),
            Feat(29, "mutual_destruction", "MUTUAL DESTRUCTION", S, On | Hid, CareerStat.MutualDestructions,
                "Kill yourself and at least one enemy with the same explosion.",
                "If you are going down, take company."),

            // ================================================================= GOLD (21)
            Counter(30, "grim_arithmetic", "GRIM ARITHMETIC", G, On, CareerStat.Kills, 10000,
                "Get 10,000 kills in online rounds."),
            Best(31, "juggernaut", "JUGGERNAUT", G, On, CareerStat.BestStreak, 30, "kills",
                "Get 30 kills without dying."),
            Best(32, "crowd_control", "CROWD CONTROL", G, On, CareerStat.BestGrenadeBlast, 3, "kills",
                "Kill 3 enemies with a single grenade."),
            Counter(33, "cold_steel", "COLD STEEL", G, On, CareerStat.MeleeKills, 25,
                "Get 25 melee kills."),
            PartsOf(34, "armourer", "ARMOURER", G, On, CareerStat.WeaponKillMask, Weapons,
                "Get a kill with each of the 12 weapons: RK-44, S-IND7, S-IND7 [SUP], 76 EAGLE, SL-DEFENDER, SIGNAL DMR, RECON LRR, FRAG, SPEARHEAD, BEU AW1, BIL SCALPEL and WRENCH."),
            Counter(35, "can_opener", "CAN OPENER", G, On, CareerStat.VehiclesDestroyed, 50,
                "Destroy 50 enemy vehicles."),
            Counter(36, "long_campaign", "LONG CAMPAIGN", G, On, CareerStat.RoundsWon, 100,
                "Win 100 online rounds. Each must have at least 5 minutes of your play."),
            Counter(37, "top_brass", "TOP BRASS", G, On, CareerStat.MvpRounds, 10,
                "Be the MVP of 10 rounds: win with the most points of anyone in the round, bots included, after at least 5 minutes of your play."),
            Feat(38, "clean_sheet", "CLEAN SHEET", G, On, CareerStat.CleanSheetRounds,
                "Win an online round without dying once. You must play at least 15 minutes and get at least 10 kills."),
            Feat(39, "naked_eye", "NAKED EYE", G, On | Night, CareerStat.NakedEyeRounds,
                "Win a Night Mode round without turning on night vision. You must play at least 15 minutes and get at least 10 kills."),
            Best(40, "night_terror", "NIGHT TERROR", G, On | Night, CareerStat.NightMeleeBest, 10, "melee kills",
                "Get 10 melee kills in one Night Mode round."),
            Best(41, "hell_week", "HELL WEEK", G, Pr, CareerStat.PrHellWeekBest, 75, "kills",
                "Get 75 kills in one practice round with 100 bots."),
            Claimed(42, "island_hopper", "ISLAND HOPPER", G, Pr,
                "In one practice round on Island, help capture every flag on the map: be in its zone when it turns to your side."),
            Best(43, "lake_monster", "LAKE MONSTER", G, Pr, CareerStat.PrLakeMonsterBest, 10, "kills",
                "Get 10 kills from a boat in one practice round on Forest Lake."),
            Claimed(44, "graveyard_shift", "GRAVEYARD SHIFT", G, Pr | Night,
                "Win a Night Mode practice round with at least 50 bots without turning on night vision, after at least 5 minutes of your play."),
            Best(45, "motor_pool", "MOTOR POOL", G, Pr, CareerStat.PrMotorPoolBest, 4, "of 4",
                "In one practice round, get a kill from a jeep or quad bike, a tank, a helicopter and a boat."),
            Feat(46, "jack_of_all_trades", "JACK OF ALL TRADES", G, On | Hid, CareerStat.JackOfAllTrades,
                "In one life, get a kill with a primary weapon, a kill with your pistol, a grenade kill and a kill with the BEU AW1 or BIL SCALPEL.",
                "Why pick one tool when you carry four?"),
            Feat(47, "touchdown", "TOUCHDOWN", G, On | Hid, CareerStat.HeliRoadkills,
                "Kill an enemy by landing or crashing a helicopter on them.",
                "Not every landing happens on a pad."),
            Best(48, "buckshot_sniper", "BUCKSHOT SNIPER", G, On | Hid, CareerStat.LongestShotgunKillMetres, 60, "m",
                "Kill an enemy with the 76 EAGLE shotgun from 60 m or more.",
                "Nobody told the pellets about range."),
            Feat(49, "dogfight", "DOGFIGHT", G, On | Hid, CareerStat.Dogfights,
                "While you pilot a helicopter 5 m or more above the ground, shoot down an enemy helicopter that is also 5 m or more up, with its pilot aboard.",
                "The sky is not big enough for two."),
            Claimed(50, "gold_standard", "GOLD STANDARD", G, Pr | Hid,
                "Get a kill with the golden wrench in practice.",
                "Old soldiers still whisper about a wrench made of gold."),

            // ================================================================= PLATINUM (14)
            Best(51, "windreader", "WINDREADER", P, On, CareerStat.LongestHeadshotMetres, 500, "m",
                "Kill an enemy with a headshot from 500 m or more."),
            new Achievement(52, "all_fronts_mastered", "ALL FRONTS MASTERED", P, On, AchievementProgress.Counter,
                AchievementMeasure.WinsOnEveryMap, CareerStat.RoundsWon, 3 * CareerRules.AllFrontsWinsPerMap,
                "Win 50 online rounds on each of the 3 maps. Each must have at least 5 minutes of your play."),
            Counter(53, "air_defense", "AIR DEFENSE", P, On, CareerStat.HelisDownedOnFoot, 25,
                "On foot, shoot down 25 enemy helicopters: destroy them with their pilot aboard while they are 5 m or more above the ground."),
            Best(54, "undefeated", "UNDEFEATED", P, On, CareerStat.BestWinStreak, 10, "wins in a row",
                "Win 10 online rounds in a row. Any round you play for 5 minutes or more and do not win resets the count."),
            Best(55, "moonlight_marksman", "MOONLIGHT MARKSMAN", P, On | Night, CareerStat.LongestNightHeadshotMetres, 300, "m",
                "Kill an enemy with a headshot from 300 m or more in Night Mode."),
            Feat(56, "outnumbered", "OUTNUMBERED", P, On, CareerStat.OutnumberedWins,
                "Win an online round as the only human player on your side against at least 3 human players. You must play at least 10 minutes."),
            Best(57, "on_borrowed_time", "ON BORROWED TIME", P, On, CareerStat.ClutchBest, 10, "kills",
                "After an enemy brings you down to 5 health or less, get 10 more kills without healing and without dying."),
            Feat(58, "pacifist", "PACIFIST", P, On, CareerStat.PacifistRounds,
                "Win an online round with 0 kills and 0 deaths while capturing more flags than anyone else in it, bots included. You must play at least 15 minutes."),
            Best(59, "nemesis", "NEMESIS", P, On, CareerStat.NemesisBest, 7, "kills",
                "Kill the same human player 7 times in one round without them killing you once."),
            Feat(60, "absolute_dominance", "ABSOLUTE DOMINANCE", P, On, CareerStat.DominanceRounds,
                "Finish an online round first of everyone in it, bots included, in kills, in flag captures and in accuracy (at least 50 shots), with nobody dying fewer times than you. You must play at least 5 minutes."),
            Claimed(61, "drill_sergeant", "DRILL SERGEANT", P, Pr,
                "Win a practice round with no allied bots against 20 or more enemy bots, under LEAD BY 200 or more or FIRST TO 500 or more, after at least 5 minutes of your play."),
            PartsOf(62, "grand_tour", "GRAND TOUR", P, Pr, CareerStat.PrGrandTour, Tour,
                "Win practice rounds on all 3 maps under both win rules (LEAD BY 200 or more, FIRST TO 500 or more), and win a Night Mode practice round."),
            Feat(63, "impossible_angle", "IMPOSSIBLE ANGLE", P, On | Hid, CareerStat.ImpossibleAngles,
                "Shoot down an enemy helicopter that is 10 m or more above the ground with a tank's main gun.",
                "Tanks were never meant to look up."),
            Best(64, "from_the_grave", "FROM THE GRAVE", P, On | Hid, CareerStat.FromTheGraveBest, 3, "kills",
                "After you die, kill 3 enemies with a grenade you threw before dying.",
                "Dying is only half of the plan."),

            // ================================================================= MYTHIC (16)
            Best(65, "centurion", "CENTURION", M, On, CareerStat.BestStreak, 100, "kills",
                "Get 100 kills without dying."),
            Best(66, "rampage", "RAMPAGE", M, On, CareerStat.BestMultiKill, 10, "kills",
                "Get 10 kills in a row, each one within 3 seconds of the last."),
            Best(67, "curvature", "CURVATURE", M, On, CareerStat.LongestHeadshotMetres, 900, "m",
                "Kill an enemy with a headshot from 900 m or more."),
            Best(68, "perfect_ten", "PERFECT TEN", M, On, CareerStat.HeadshotRun, 10, "headshots",
                "Get 10 headshot kills in a row without dying. A kill that is not a headshot resets the count."),
            Feat(69, "dead_eye", "DEAD EYE", M, On, CareerStat.DeadEyeRounds,
                "Win an online round with at least 15 kills and 100% accuracy: fire at least 15 bullets, and every one hits an enemy."),
            Feat(70, "untouchable", "UNTOUCHABLE", M, On, CareerStat.UntouchableRounds,
                "Win an online round without taking any damage. You must play at least 15 minutes and get at least 15 kills."),
            Best(71, "blade_only", "BLADE ONLY", M, On, CareerStat.BladeOnlyBest, 25, "kills",
                "Finish an online round with at least 25 kills, every one of them a melee kill. You must play at least 5 minutes."),
            Best(72, "tank_ace", "TANK ACE", M, On, CareerStat.TankStintBest, 50, "kills",
                "Get 50 kills in one tank without leaving it or it being destroyed."),
            Best(73, "sky_king", "SKY KING", M, On, CareerStat.HeliStintBest, 30, "kills",
                "Get 30 kills in one helicopter flight without leaving your seat or the helicopter being destroyed."),
            Feat(74, "map_painter", "MAP PAINTER", M, On, CareerStat.MapPainterRounds,
                "In one round, help capture every flag on the map, never die, and win. You must play at least 5 minutes."),
            Feat(75, "hail_mary", "HAIL MARY", M, On, CareerStat.HailMaryWins,
                "Win a round after the enemy came within 10 points of winning it. You must play at least 5 minutes."),
            Feat(76, "creature_of_the_night", "CREATURE OF THE NIGHT", M, On | Night, CareerStat.CreatureRounds,
                "Win a Night Mode round with at least 30 kills and 0 deaths, without turning on night vision. You must play at least 5 minutes."),
            Best(77, "immaculate", "IMMACULATE", M, Pr, CareerStat.PrImmaculateBest, 100, "kills",
                "Get 100 kills without dying in a practice round with 100 bots."),
            new Achievement(78, "ironclad", "IRONCLAD", M, On, AchievementProgress.Counter, AchievementMeasure.OthersHeld,
                null, 79, "Unlock all 79 other achievements."),
            Best(79, "mid_air", "MID-AIR", M, On | Hid, CareerStat.LongestPilotHeadshotMetres, 300, "m",
                "Kill an enemy helicopter pilot with a headshot from 300 m or more while the helicopter is 5 m or more above the ground.",
                "Pilots think they are safe up there."),
            Best(80, "counter_sniper", "COUNTER-SNIPER", M, On | Hid, CareerStat.LongestPistolOnSniperMetres, 151, "m",
                "With your pistol, kill an enemy holding a sniper or marksman rifle (SL-DEFENDER, SIGNAL DMR or RECON LRR) from more than 150 m.",
                "Bring a pistol to a sniper fight."),
        };

        /// <summary>The achievement with <paramref name="id"/>, or null.</summary>
        public static Achievement? Find(string id)
        {
            foreach (Achievement achievement in All)
                if (achievement.Id == id) return achievement;
            return null;
        }

        /// <summary>Points an achievement of <paramref name="tier"/> is worth.</summary>
        public static int PointsFor(AchievementTier tier) => tier switch
        {
            AchievementTier.Silver => 25,
            AchievementTier.Gold => 50,
            AchievementTier.Platinum => 100,
            AchievementTier.Mythic => 250,
            _ => 10,
        };

        /// <summary>All the points there are: 6,980.</summary>
        public static int TotalPoints
        {
            get
            {
                int total = 0;
                foreach (Achievement achievement in All) total += achievement.Points;
                return total;
            }
        }

        private static Achievement Counter(int number, string id, string title, AchievementTier tier, AchievementTags tags,
            CareerStat stat, long target, string description)
            => new Achievement(number, id, title, tier, tags, AchievementProgress.Counter, AchievementMeasure.Value, stat,
                target, description);

        private static Achievement Best(int number, string id, string title, AchievementTier tier, AchievementTags tags,
            CareerStat stat, long target, string unit, string description, string teaser = "")
            => new Achievement(number, id, title, tier, tags, AchievementProgress.Best, AchievementMeasure.Value, stat,
                target, description, unit, teaser: teaser);

        private static Achievement Feat(int number, string id, string title, AchievementTier tier, AchievementTags tags,
            CareerStat stat, string description, string teaser = "")
            => new Achievement(number, id, title, tier, tags, AchievementProgress.None, AchievementMeasure.Value, stat,
                1, description, teaser: teaser);

        private static Achievement PartsOf(int number, string id, string title, AchievementTier tier, AchievementTags tags,
            CareerStat stat, AchievementPart[] parts, string description)
            => new Achievement(number, id, title, tier, tags, AchievementProgress.Parts, AchievementMeasure.Parts, stat,
                parts.Length, description, parts: parts);

        private static Achievement Claimed(int number, string id, string title, AchievementTier tier, AchievementTags tags,
            string description, string teaser = "")
            => new Achievement(number, id, title, tier, tags, AchievementProgress.None, AchievementMeasure.Value, null,
                1, description, teaser: teaser);
    }
}
