using System;
using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.MasterServer.Career
{
    /// <summary>The ranking as one player sees it.</summary>
    public sealed class LeaderboardView
    {
        public List<(int Rank, CareerRow Row)> Top { get; } = new List<(int, CareerRow)>();
        public (int Rank, CareerRow Row)? You { get; set; }
        public int Players { get; set; }
    }

    /// <summary>One player's achievements, and how common each one is.</summary>
    public sealed class AchievementsView
    {
        public List<(string Id, long At)> Unlocked { get; set; } = new List<(string, long)>();
        public Dictionary<string, long> Holders { get; set; } = new Dictionary<string, long>();
        public long Players { get; set; }
        public Dictionary<string, long> Career { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>
    /// Careers, achievements and the global ranking (owner's list of 2026-10-09, item 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The master judges every online achievement.</b> A game server reports what happened in a
    /// round (<c>MatchPlayerResult.Stats</c>); this folds it into the career and unlocks whatever
    /// the career now reaches (<see cref="AchievementCatalog"/>). The client cannot unlock an online
    /// achievement; it can only claim the four practice ones, which nothing else can see happen.
    /// </para>
    /// <para>
    /// <b>What the master knows itself, it does not take from the server.</b> The map and the mode
    /// come from the room, so a server cannot say a Dustbowl round was played at night.
    /// </para>
    /// </remarks>
    public sealed class CareerService
    {
        /// <summary>How many careers the global ranking keeps (owner: "top 100 only").</summary>
        public const int LeaderboardSize = 100;

        private readonly SqliteDatabase _database;

        public CareerService(SqliteDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Folds one round into <paramref name="playerId"/>'s career and answers the achievements it
        /// earned, in catalogue order.
        /// </summary>
        /// <param name="stats">The game server's numbers by key; null from a server that predates them.</param>
        public List<string> RecordRound(int playerId, IReadOnlyDictionary<string, long>? stats,
            int kills, int deaths, int score, ushort mapId, bool night, long now)
        {
            var round = new Dictionary<CareerStat, long>();
            if (stats != null)
                foreach (KeyValuePair<string, long> stat in stats)
                    if (CareerStats.TryParse(stat.Key, out CareerStat key) && stat.Value > 0)
                        round[key] = stat.Value;

            // A server older than the stats still reports these three; the match counts either way.
            round[CareerStat.Matches] = 1;
            round[CareerStat.Kills] = Math.Max(kills, round.TryGetValue(CareerStat.Kills, out long k) ? k : 0);
            round[CareerStat.Deaths] = Math.Max(deaths, round.TryGetValue(CareerStat.Deaths, out long d) ? d : 0);
            round[CareerStat.Score] = Math.Max(score, round.TryGetValue(CareerStat.Score, out long s) ? s : 0);

            // The master's own facts about the round.
            round[CareerStat.MapsPlayed] = mapId < 63 ? 1L << mapId : 0;
            round[CareerStat.NightMatches] = night ? 1 : 0;
            round[CareerStat.NightKills] = night ? round[CareerStat.Kills] : 0;

            Dictionary<string, long> career = _database.ReadCareer(playerId);
            var changed = new Dictionary<string, long>();
            foreach (KeyValuePair<CareerStat, long> stat in round)
            {
                string key = CareerStats.Key(stat.Key);
                long before = career.TryGetValue(key, out long value) ? value : 0;
                long after = CareerStats.Combine(stat.Key, before, stat.Value);
                if (after == before) continue;
                career[key] = after;
                changed[key] = after;
            }
            if (changed.Count > 0) _database.WriteCareer(playerId, changed);

            return Unlock(playerId, career, now);
        }

        /// <summary>
        /// Records the practice achievements in <paramref name="ids"/> that the catalogue lets the
        /// client claim; anything else is ignored. Answers the ones newly earned.
        /// </summary>
        public List<string> Claim(int playerId, IEnumerable<string>? ids, long now)
        {
            var earned = new List<string>();
            if (ids == null) return earned;
            foreach (string id in ids.Distinct())
            {
                Achievement? achievement = AchievementCatalog.Find(id);
                if (achievement == null || !achievement.IsClaimedByClient) continue;
                if (_database.InsertAchievement(playerId, id, now)) earned.Add(id);
            }
            return earned;
        }

        /// <summary>The best hundred careers, and where <paramref name="requester"/> stands.</summary>
        public LeaderboardView Leaderboard(int requester)
        {
            List<CareerRow> rows = _database.ReadCareerRows();
            rows.Sort(Compare);

            var view = new LeaderboardView { Players = rows.Count };
            for (int i = 0; i < rows.Count; i++)
            {
                if (i < LeaderboardSize) view.Top.Add((i + 1, rows[i]));
                if (rows[i].PlayerId == requester) view.You = (i + 1, rows[i]);
            }
            return view;
        }

        /// <summary>What <paramref name="requester"/> has earned, how common each achievement is, and the career behind them.</summary>
        public AchievementsView Achievements(int requester) => new AchievementsView
        {
            Unlocked = _database.ReadAchievements(requester),
            Holders = _database.CountAchievementHolders(),
            Players = _database.CountCareerPlayers(),
            Career = _database.ReadCareer(requester),
        };

        /// <summary>Ranking order: score, then kills, then fewer deaths, then the older account.</summary>
        internal static int Compare(CareerRow a, CareerRow b)
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byKills = b.Kills.CompareTo(a.Kills);
            if (byKills != 0) return byKills;
            int byDeaths = a.Deaths.CompareTo(b.Deaths);
            return byDeaths != 0 ? byDeaths : a.PlayerId.CompareTo(b.PlayerId);
        }

        private List<string> Unlock(int playerId, IReadOnlyDictionary<string, long> career, long now)
        {
            var earned = new List<string>();
            var held = new HashSet<string>(_database.ReadAchievements(playerId).Select(a => a.Id));
            foreach (Achievement achievement in AchievementCatalog.All)
            {
                if (achievement.Stat == null || held.Contains(achievement.Id)) continue;
                long value = career.TryGetValue(CareerStats.Key(achievement.Stat.Value), out long v) ? v : 0;
                if (achievement.IsEarnedBy(value) && _database.InsertAchievement(playerId, achievement.Id, now))
                    earned.Add(achievement.Id);
            }
            return earned;
        }
    }
}
