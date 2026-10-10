using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Ironfront.MasterServer.Data
{
    /// <summary>One player's line on the global ranking.</summary>
    public sealed class CareerRow
    {
        public int PlayerId { get; init; }
        public string Name { get; init; } = string.Empty;
        public long Score { get; init; }
        public long Kills { get; init; }
        public long Deaths { get; init; }
        public long Headshots { get; init; }
        public long Wins { get; init; }
        public long Matches { get; init; }
        public long BestStreak { get; init; }
    }

    /// <summary>
    /// Careers and achievements (owner's list of 2026-10-09, item 4): one row per player and stat,
    /// one per player and achievement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stats are rows, not columns.</b> A new stat is a new key in <c>CareerStats</c> and needs
    /// no migration; a column per stat would make every addition a schema change on the live
    /// database.
    /// </para>
    /// <para>
    /// <b>The ranking is sorted in memory.</b> At the master's scale (hundreds of accounts) reading
    /// the five ranking stats of everyone is cheaper to reason about than a pivot query, and it is
    /// the same order for the top hundred and for the requester's own place.
    /// </para>
    /// </remarks>
    public sealed partial class SqliteDatabase
    {
        /// <summary>
        /// The career season the stored numbers belong to. Season 1 (v4.6.0 and v4.6.1) is closed:
        /// its online numbers were wrong both ways. A game server re-sent each round's running
        /// totals, so every round in a room re-added the ones before it (maidang2804 held 1,005
        /// kills for about 555), and a round's end was dropped for every player whose master link
        /// had closed mid-round. The owner chose a fresh ranking over a partial repair (2026-10-11);
        /// season 1's rows are archived in <c>career_stats_season1</c>.
        /// </summary>
        public const int CurrentCareerSeason = 2;

        private void CreateCareerTables()
        {
            bool fresh = !TableExists("career_stats");
            Execute(@"
CREATE TABLE IF NOT EXISTS career_stats (
    player_id INTEGER NOT NULL REFERENCES accounts(player_id),
    stat TEXT NOT NULL,
    value INTEGER NOT NULL,
    PRIMARY KEY (player_id, stat)
);
CREATE TABLE IF NOT EXISTS achievements (
    player_id INTEGER NOT NULL REFERENCES accounts(player_id),
    achievement_id TEXT NOT NULL,
    unlocked_at INTEGER NOT NULL,
    PRIMARY KEY (player_id, achievement_id)
);
CREATE INDEX IF NOT EXISTS idx_achievements_id ON achievements(achievement_id);
CREATE TABLE IF NOT EXISTS career_meta (
    key TEXT PRIMARY KEY,
    value INTEGER NOT NULL
);");

            // A database that has never held a career starts in the current season: there is
            // nothing of an older one in it to reset.
            if (fresh) Execute($"INSERT OR IGNORE INTO career_meta(key, value) VALUES ('season', {CurrentCareerSeason})");
        }

        private bool TableExists(string name)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            cmd.Parameters.AddWithValue("$name", name);
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }

        /// <summary>The career season the stored numbers belong to; 1 for a database that never recorded one.</summary>
        public int ReadCareerSeason()
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM career_meta WHERE key = 'season'";
            object? value = cmd.ExecuteScalar();
            return value is long season ? (int)season : 1;
        }

        /// <summary>
        /// Starts career season <paramref name="season"/>: every stat row <paramref name="reset"/>
        /// names is copied into <c>career_stats_season{old}</c> and deleted, and the season is
        /// recorded, in one transaction. Achievements and the rows not named are untouched.
        /// </summary>
        /// <returns>How many rows were archived and deleted.</returns>
        public int BeginCareerSeason(int season, System.Func<string, bool> reset)
        {
            int old = ReadCareerSeason();
            var keys = new List<string>();
            using (SqliteCommand cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT stat FROM career_stats";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read()) keys.Add(reader.GetString(0));
            }

            string archive = $"career_stats_season{old}";
            int removed = 0;
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using (SqliteCommand create = _connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = $"CREATE TABLE IF NOT EXISTS {archive} (player_id INTEGER NOT NULL, stat TEXT NOT NULL, value INTEGER NOT NULL, PRIMARY KEY (player_id, stat))";
                create.ExecuteNonQuery();
            }
            foreach (string key in keys)
            {
                if (!reset(key)) continue;
                using SqliteCommand copy = _connection.CreateCommand();
                copy.Transaction = transaction;
                copy.CommandText = $"INSERT OR REPLACE INTO {archive}(player_id, stat, value) SELECT player_id, stat, value FROM career_stats WHERE stat = $stat";
                copy.Parameters.AddWithValue("$stat", key);
                copy.ExecuteNonQuery();

                using SqliteCommand delete = _connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM career_stats WHERE stat = $stat";
                delete.Parameters.AddWithValue("$stat", key);
                removed += delete.ExecuteNonQuery();
            }
            using (SqliteCommand mark = _connection.CreateCommand())
            {
                mark.Transaction = transaction;
                mark.CommandText = "INSERT INTO career_meta(key, value) VALUES ('season', $season) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                mark.Parameters.AddWithValue("$season", season);
                mark.ExecuteNonQuery();
            }
            transaction.Commit();
            return removed;
        }

        /// <summary>Every stat <paramref name="playerId"/> has, by key.</summary>
        public Dictionary<string, long> ReadCareer(int playerId)
        {
            var career = new Dictionary<string, long>();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT stat, value FROM career_stats WHERE player_id = $id";
            cmd.Parameters.AddWithValue("$id", playerId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) career[reader.GetString(0)] = reader.GetInt64(1);
            return career;
        }

        /// <summary>Writes the stats in <paramref name="career"/> for <paramref name="playerId"/>, in one transaction.</summary>
        public void WriteCareer(int playerId, IReadOnlyDictionary<string, long> career)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            foreach (KeyValuePair<string, long> stat in career)
            {
                using SqliteCommand cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO career_stats(player_id, stat, value) VALUES ($id, $stat, $value) "
                                  + "ON CONFLICT(player_id, stat) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("$id", playerId);
                cmd.Parameters.AddWithValue("$stat", stat.Key);
                cmd.Parameters.AddWithValue("$value", stat.Value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        /// <summary>Records an achievement; false when the player already had it.</summary>
        public bool InsertAchievement(int playerId, string achievementId, long now)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO achievements(player_id, achievement_id, unlocked_at) VALUES ($id, $achievement, $now)";
            cmd.Parameters.AddWithValue("$id", playerId);
            cmd.Parameters.AddWithValue("$achievement", achievementId);
            cmd.Parameters.AddWithValue("$now", now);
            return cmd.ExecuteNonQuery() == 1;
        }

        /// <summary>The achievements <paramref name="playerId"/> has, with when each was earned.</summary>
        public List<(string Id, long At)> ReadAchievements(int playerId)
        {
            var unlocked = new List<(string, long)>();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT achievement_id, unlocked_at FROM achievements WHERE player_id = $id ORDER BY unlocked_at";
            cmd.Parameters.AddWithValue("$id", playerId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) unlocked.Add((reader.GetString(0), reader.GetInt64(1)));
            return unlocked;
        }

        /// <summary>For each id in <paramref name="ids"/> that anyone holds: who earned it first, and when.</summary>
        public Dictionary<string, (string Name, long At)> ReadFirstHolders(IEnumerable<string> ids)
        {
            var firsts = new Dictionary<string, (string, long)>();
            foreach (string id in ids)
            {
                using SqliteCommand cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT a.display_name, x.unlocked_at FROM achievements x "
                                  + "JOIN accounts a ON a.player_id = x.player_id "
                                  + "WHERE x.achievement_id = $id ORDER BY x.unlocked_at, x.player_id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", id);
                using SqliteDataReader reader = cmd.ExecuteReader();
                if (reader.Read()) firsts[id] = (reader.GetString(0), reader.GetInt64(1));
            }
            return firsts;
        }

        /// <summary>How many players have each achievement.</summary>
        public Dictionary<string, long> CountAchievementHolders()
        {
            var counts = new Dictionary<string, long>();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT achievement_id, COUNT(*) FROM achievements GROUP BY achievement_id";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt64(1);
            return counts;
        }

        /// <summary>Every player's achievement ids, by player: the ranking's achievement columns.</summary>
        public Dictionary<int, List<string>> ReadAchievementIdsByPlayer()
        {
            var held = new Dictionary<int, List<string>>();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT player_id, achievement_id FROM achievements";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int id = reader.GetInt32(0);
                if (!held.TryGetValue(id, out List<string>? ids)) held[id] = ids = new List<string>();
                ids.Add(reader.GetString(1));
            }
            return held;
        }

        /// <summary>Players who have played online or earned anything: the share's denominator.</summary>
        public long CountCareerPlayers()
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM (SELECT player_id FROM career_stats UNION SELECT player_id FROM achievements)";
            return (long)cmd.ExecuteScalar()!;
        }

        /// <summary>Every player who has played online, with the ranking's numbers, unsorted.</summary>
        public List<CareerRow> ReadCareerRows()
        {
            var rows = new Dictionary<int, Dictionary<string, long>>();
            var names = new Dictionary<int, string>();
            using (SqliteCommand cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT s.player_id, a.display_name, s.stat, s.value FROM career_stats s "
                                  + "JOIN accounts a ON a.player_id = s.player_id "
                                  + "WHERE s.stat IN ('score','kills','deaths','headshots','wins','matches','bestStreak')";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    names[id] = reader.GetString(1);
                    if (!rows.TryGetValue(id, out Dictionary<string, long>? stats)) rows[id] = stats = new Dictionary<string, long>();
                    stats[reader.GetString(2)] = reader.GetInt64(3);
                }
            }

            var result = new List<CareerRow>(rows.Count);
            foreach (KeyValuePair<int, Dictionary<string, long>> row in rows)
            {
                long Get(string key) => row.Value.TryGetValue(key, out long value) ? value : 0;
                if (Get("matches") <= 0) continue;
                result.Add(new CareerRow
                {
                    PlayerId = row.Key, Name = names[row.Key], Score = Get("score"), Kills = Get("kills"),
                    Deaths = Get("deaths"), Headshots = Get("headshots"), Wins = Get("wins"),
                    Matches = Get("matches"), BestStreak = Get("bestStreak"),
                });
            }
            return result;
        }
    }
}
