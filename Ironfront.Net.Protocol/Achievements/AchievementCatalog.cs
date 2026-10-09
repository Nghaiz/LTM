using System.Collections.Generic;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>The families the achievement page groups by (owner's list of 2026-10-09, item 4).</summary>
    public enum AchievementCategory : byte
    {
        Multiplayer,
        Combat,
        Vehicles,
        Honor,
        Hard,
        Secret,
        Practice,
    }

    /// <summary>The badge's metal, which also orders the difficulty.</summary>
    public enum AchievementTier : byte
    {
        Bronze,
        Silver,
        Gold,
        Platinum,
    }

    /// <summary>One achievement: what it is called, what earns it, and how it is drawn.</summary>
    public sealed class Achievement
    {
        internal Achievement(string id, string title, string description, AchievementCategory category,
            AchievementTier tier, CareerStat? stat, long target, bool hidden = false)
        {
            Id = id;
            Title = title;
            Description = description;
            Category = category;
            Tier = tier;
            Stat = stat;
            Target = target;
            Hidden = hidden;
        }

        /// <summary>Stable id: the master's database key, the badge's file name, the wire's name.</summary>
        public string Id { get; }

        public string Title { get; }

        /// <summary>One line on how to earn it. A hidden achievement's is shown only once earned.</summary>
        public string Description { get; }

        public AchievementCategory Category { get; }

        public AchievementTier Tier { get; }

        /// <summary>
        /// The career number that earns it once it reaches <see cref="Target"/>, or null for a
        /// practice achievement, which only the player's own game can see happen and claims.
        /// </summary>
        public CareerStat? Stat { get; }

        public long Target { get; }

        /// <summary>Drawn as "???" with no description until it is earned.</summary>
        public bool Hidden { get; }

        /// <summary>Whether the player's game reports it (offline practice) rather than the master judging it.</summary>
        public bool IsClaimedByClient => Stat == null;

        /// <summary>Whether a career with <paramref name="value"/> in <see cref="Stat"/> has earned it.</summary>
        public bool IsEarnedBy(long value)
            => Stat != null && (Stat == CareerStat.MapsPlayed ? CareerStats.BitCount(value) : value) >= Target;

        /// <summary>How far along a career with <paramref name="value"/> is, for a progress bar.</summary>
        public long ProgressFor(long value)
        {
            long have = Stat == CareerStat.MapsPlayed ? CareerStats.BitCount(value) : value;
            return have > Target ? Target : have < 0 ? 0 : have;
        }
    }

    /// <summary>
    /// The fifty achievements (owner's list of 2026-10-09, item 4): online career milestones the
    /// master judges from the stats every match reports, and four practice ones the game reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared by the master and the client.</b> The master unlocks from this list and the
    /// client draws from it, so a title or a target cannot differ between the two.
    /// </para>
    /// <para>
    /// <b>Every online achievement is "a career stat reaches a target".</b> A one-match feat (a
    /// triple kill, a flawless round) is a career maximum or counter the match fed, so the master
    /// has one rule to run and the page can show progress for all of them.
    /// </para>
    /// <para>
    /// Docs: <c>docs/achievements.md</c> lists every one with its badge and how to earn it; the
    /// badges are rendered by <c>tools/ui/make_icons.py</c> from <c>tools/ui/badges.py</c>.
    /// </para>
    /// </remarks>
    public static class AchievementCatalog
    {
        public static readonly IReadOnlyList<Achievement> All = new[]
        {
            // ---- Multiplayer: playing online
            new Achievement("boots_on_the_ground", "BOOTS ON THE GROUND", "Play your first online match.", AchievementCategory.Multiplayer, AchievementTier.Bronze, CareerStat.Matches, 1),
            new Achievement("tour_of_duty", "TOUR OF DUTY", "Play 10 online matches.", AchievementCategory.Multiplayer, AchievementTier.Bronze, CareerStat.Matches, 10),
            new Achievement("career_soldier", "CAREER SOLDIER", "Play 100 online matches.", AchievementCategory.Multiplayer, AchievementTier.Silver, CareerStat.Matches, 100),
            new Achievement("forever_war", "FOREVER WAR", "Spend 24 hours in online battles.", AchievementCategory.Multiplayer, AchievementTier.Gold, CareerStat.SecondsPlayed, 24 * 60 * 60),
            new Achievement("victory", "VICTORY", "Win an online round.", AchievementCategory.Multiplayer, AchievementTier.Bronze, CareerStat.Wins, 1),
            new Achievement("champion", "CHAMPION", "Win 25 online rounds.", AchievementCategory.Multiplayer, AchievementTier.Silver, CareerStat.Wins, 25),
            new Achievement("world_traveler", "WORLD TRAVELER", "Play an online match on every map.", AchievementCategory.Multiplayer, AchievementTier.Silver, CareerStat.MapsPlayed, 3),
            new Achievement("night_owl", "NIGHT OWL", "Play a Night Mode match.", AchievementCategory.Multiplayer, AchievementTier.Bronze, CareerStat.NightMatches, 1),
            new Achievement("patched_up", "PATCHED UP", "Fall 100 times and keep coming back.", AchievementCategory.Multiplayer, AchievementTier.Bronze, CareerStat.Deaths, 100),
            new Achievement("quartermaster", "QUARTERMASTER", "Score 1,000 points for your side.", AchievementCategory.Multiplayer, AchievementTier.Bronze, CareerStat.Score, 1000),

            // ---- Combat
            new Achievement("first_blood", "FIRST BLOOD", "Get your first kill online.", AchievementCategory.Combat, AchievementTier.Bronze, CareerStat.Kills, 1),
            new Achievement("decorated", "DECORATED", "Get 100 kills online.", AchievementCategory.Combat, AchievementTier.Silver, CareerStat.Kills, 100),
            new Achievement("veteran", "VETERAN", "Get 1,000 kills online.", AchievementCategory.Combat, AchievementTier.Gold, CareerStat.Kills, 1000),
            new Achievement("two_for_one", "TWO FOR ONE", "Kill two enemies within three seconds.", AchievementCategory.Combat, AchievementTier.Bronze, CareerStat.BestMultiKill, 2),
            new Achievement("triple_threat", "TRIPLE THREAT", "Three kills, each within three seconds of the last.", AchievementCategory.Combat, AchievementTier.Silver, CareerStat.BestMultiKill, 3),
            new Achievement("unstoppable", "UNSTOPPABLE", "Kill 10 enemies without dying.", AchievementCategory.Combat, AchievementTier.Gold, CareerStat.BestStreak, 10),
            new Achievement("sharpshooter", "SHARPSHOOTER", "Land 50 headshot kills.", AchievementCategory.Combat, AchievementTier.Bronze, CareerStat.Headshots, 50),
            new Achievement("long_shot", "LONG SHOT", "Kill an enemy 150 metres away or more.", AchievementCategory.Combat, AchievementTier.Silver, CareerStat.LongestKillMetres, 150),
            new Achievement("up_close", "UP CLOSE", "Kill an enemy with a blade or a wrench.", AchievementCategory.Combat, AchievementTier.Bronze, CareerStat.MeleeKills, 1),
            new Achievement("frag_out", "FRAG OUT", "Kill two enemies with a single grenade.", AchievementCategory.Combat, AchievementTier.Silver, CareerStat.GrenadeDoubleKills, 1),
            new Achievement("demolition_expert", "DEMOLITION EXPERT", "Get 100 kills with explosives.", AchievementCategory.Combat, AchievementTier.Gold, CareerStat.ExplosiveKills, 100),
            new Achievement("payback", "PAYBACK", "Kill the soldier who last killed you.", AchievementCategory.Combat, AchievementTier.Bronze, CareerStat.RevengeKills, 1),
            new Achievement("man_vs_machine", "MAN VS MACHINE", "Kill 100 bots online.", AchievementCategory.Combat, AchievementTier.Bronze, CareerStat.BotKills, 100),
            new Achievement("player_hunter", "PLAYER HUNTER", "Kill 50 human players.", AchievementCategory.Combat, AchievementTier.Silver, CareerStat.PlayerKills, 50),
            new Achievement("night_stalker", "NIGHT STALKER", "Get 100 kills in Night Mode.", AchievementCategory.Combat, AchievementTier.Silver, CareerStat.NightKills, 100),

            // ---- Vehicles
            new Achievement("road_rage", "ROAD RAGE", "Run an enemy over.", AchievementCategory.Vehicles, AchievementTier.Bronze, CareerStat.Roadkills, 1),
            new Achievement("tank_buster", "TANK BUSTER", "Destroy an enemy tank with its crew inside.", AchievementCategory.Vehicles, AchievementTier.Silver, CareerStat.TanksDestroyed, 1),
            new Achievement("armored_fist", "ARMORED FIST", "Get 50 kills from a tank.", AchievementCategory.Vehicles, AchievementTier.Gold, CareerStat.TankKills, 50),
            new Achievement("air_superiority", "AIR SUPERIORITY", "Get 25 kills from a helicopter.", AchievementCategory.Vehicles, AchievementTier.Gold, CareerStat.HelicopterKills, 25),
            new Achievement("anchors_aweigh", "ANCHORS AWEIGH", "Get 10 kills from a boat.", AchievementCategory.Vehicles, AchievementTier.Silver, CareerStat.BoatKills, 10),

            // ---- Honor: playing for the side
            new Achievement("flag_bearer", "FLAG BEARER", "Help capture a flag.", AchievementCategory.Honor, AchievementTier.Bronze, CareerStat.FlagsCaptured, 1),
            new Achievement("blitzkrieg", "BLITZKRIEG", "Help capture 50 flags.", AchievementCategory.Honor, AchievementTier.Gold, CareerStat.FlagsCaptured, 50),
            new Achievement("mvp", "MOST VALUABLE", "Win a round with the most points of anyone in it.", AchievementCategory.Honor, AchievementTier.Silver, CareerStat.MvpRounds, 1),
            new Achievement("against_all_odds", "AGAINST ALL ODDS", "Win a round after your side trailed by 100 points.", AchievementCategory.Honor, AchievementTier.Gold, CareerStat.ComebackWins, 1),
            new Achievement("conqueror", "CONQUEROR", "Win 100 online rounds.", AchievementCategory.Honor, AchievementTier.Gold, CareerStat.Wins, 100),

            // ---- Hard
            new Achievement("war_machine", "WAR MACHINE", "Get 10,000 kills online.", AchievementCategory.Hard, AchievementTier.Platinum, CareerStat.Kills, 10000),
            new Achievement("legend_never_dies", "LEGENDS NEVER DIE", "Kill 25 enemies without dying.", AchievementCategory.Hard, AchievementTier.Platinum, CareerStat.BestStreak, 25),
            new Achievement("head_hunter", "HEAD HUNTER", "Land 500 headshot kills.", AchievementCategory.Hard, AchievementTier.Gold, CareerStat.Headshots, 500),
            new Achievement("eagle_eye", "EAGLE EYE", "Kill an enemy with a headshot from 300 metres.", AchievementCategory.Hard, AchievementTier.Platinum, CareerStat.LongestHeadshotMetres, 300),
            new Achievement("flawless", "FLAWLESS", "Finish a round with 15 kills and no deaths.", AchievementCategory.Hard, AchievementTier.Platinum, CareerStat.FlawlessRounds, 1),

            // ---- Secret: drawn as "???" until earned
            new Achievement("gravity_wins", "GRAVITY WINS", "Die from a fall.", AchievementCategory.Secret, AchievementTier.Bronze, CareerStat.FallDeaths, 1, hidden: true),
            new Achievement("sleeping_with_the_fishes", "SLEEPING WITH THE FISHES", "Drown.", AchievementCategory.Secret, AchievementTier.Bronze, CareerStat.DrownDeaths, 1, hidden: true),
            new Achievement("friendly_fire", "FRIENDLY FIRE", "Kill a teammate. It happens.", AchievementCategory.Secret, AchievementTier.Bronze, CareerStat.TeamKills, 1, hidden: true),
            new Achievement("own_goal", "OWN GOAL", "Blow yourself up.", AchievementCategory.Secret, AchievementTier.Bronze, CareerStat.OwnExplosiveDeaths, 1, hidden: true),
            new Achievement("not_today", "NOT TODAY", "Get a kill within three seconds of deploying.", AchievementCategory.Secret, AchievementTier.Silver, CareerStat.QuickKills, 1, hidden: true),
            new Achievement("hat_trick", "HAT TRICK", "Three kills in a row, every one a headshot.", AchievementCategory.Secret, AchievementTier.Gold, CareerStat.HeadshotRun, 3, hidden: true),

            // ---- Practice: offline, reported by the player's own game
            new Achievement("basic_training", "BASIC TRAINING", "Finish a practice match.", AchievementCategory.Practice, AchievementTier.Bronze, null, 1),
            new Achievement("bot_buster", "BOT BUSTER", "Get 25 kills in one practice match.", AchievementCategory.Practice, AchievementTier.Silver, null, 1),
            new Achievement("one_man_army", "ONE-MAN ARMY", "Win a practice match with 100 bots.", AchievementCategory.Practice, AchievementTier.Gold, null, 1),
            new Achievement("student_of_war", "STUDENT OF WAR", "Read every tab of How to play.", AchievementCategory.Practice, AchievementTier.Bronze, null, 1),
        };

        /// <summary>The achievement with <paramref name="id"/>, or null.</summary>
        public static Achievement? Find(string id)
        {
            foreach (Achievement achievement in All)
                if (achievement.Id == id) return achievement;
            return null;
        }

        /// <summary>The category's name on the page.</summary>
        public static string CategoryName(AchievementCategory category) => category switch
        {
            AchievementCategory.Multiplayer => "MULTIPLAYER",
            AchievementCategory.Combat => "COMBAT",
            AchievementCategory.Vehicles => "VEHICLES",
            AchievementCategory.Honor => "HONOR",
            AchievementCategory.Hard => "HARD",
            AchievementCategory.Secret => "SECRET",
            _ => "PRACTICE",
        };
    }
}
