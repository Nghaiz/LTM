using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Ironfront.MasterServer.Data
{
    public sealed class AccountRecord
    {
        public int PlayerId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public long LockedUntil { get; init; }
        public bool IsBanned { get; init; }
    }

    public sealed class MatchResultRecord
    {
        public int RoomId { get; init; }
        public int PlayerId { get; init; }
        public int Kills { get; init; }
        public int Deaths { get; init; }
        public int Score { get; init; }
        public long EndedAt { get; init; }
    }

    public sealed class SqliteDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SqliteDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Database path is required.", nameof(path));
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());
            _connection.Open();
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute(@"
CREATE TABLE IF NOT EXISTS accounts (
    player_id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT NOT NULL,
    display_name TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    last_login_at INTEGER,
    failed_logins INTEGER NOT NULL DEFAULT 0,
    locked_until INTEGER NOT NULL DEFAULT 0,
    is_banned INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_accounts_username ON accounts(username COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS match_results (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    room_id INTEGER NOT NULL,
    player_id INTEGER NOT NULL REFERENCES accounts(player_id),
    kills INTEGER NOT NULL,
    deaths INTEGER NOT NULL,
    score INTEGER NOT NULL,
    ended_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_results_player ON match_results(player_id);");
        }

        public AccountRecord? FindAccount(string username)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT player_id, username, password_hash, display_name, locked_until, is_banned FROM accounts WHERE username = $username COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$username", username);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new AccountRecord
            {
                PlayerId = reader.GetInt32(0), Username = reader.GetString(1), PasswordHash = reader.GetString(2),
                DisplayName = reader.GetString(3), LockedUntil = reader.GetInt64(4), IsBanned = reader.GetInt64(5) != 0
            };
        }

        public bool InsertAccount(string username, string passwordHash, string displayName, long now)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO accounts(username, password_hash, display_name, created_at) VALUES ($username, $password, $display, $created)";
            cmd.Parameters.AddWithValue("$username", username);
            cmd.Parameters.AddWithValue("$password", passwordHash);
            cmd.Parameters.AddWithValue("$display", displayName);
            cmd.Parameters.AddWithValue("$created", now);
            try { return cmd.ExecuteNonQuery() == 1; }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
        }

        public void RecordLoginSuccess(int playerId, long now)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET failed_logins = 0, locked_until = 0, last_login_at = $now WHERE player_id = $id";
            cmd.Parameters.AddWithValue("$now", now); cmd.Parameters.AddWithValue("$id", playerId); cmd.ExecuteNonQuery();
        }

        public void RecordLoginFailure(int playerId, int maxFailures, long lockedUntil)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET failed_logins = failed_logins + 1, locked_until = CASE WHEN failed_logins + 1 >= $max THEN $until ELSE locked_until END WHERE player_id = $id";
            cmd.Parameters.AddWithValue("$max", maxFailures); cmd.Parameters.AddWithValue("$until", lockedUntil); cmd.Parameters.AddWithValue("$id", playerId); cmd.ExecuteNonQuery();
        }

        public void InsertMatchResult(int roomId, int playerId, int kills, int deaths, int score, long endedAt)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO match_results(room_id, player_id, kills, deaths, score, ended_at) VALUES ($room, $player, $kills, $deaths, $score, $ended)";
            cmd.Parameters.AddWithValue("$room", roomId); cmd.Parameters.AddWithValue("$player", playerId);
            cmd.Parameters.AddWithValue("$kills", kills); cmd.Parameters.AddWithValue("$deaths", deaths); cmd.Parameters.AddWithValue("$score", score); cmd.Parameters.AddWithValue("$ended", endedAt);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Every result row recorded for one match, oldest first. Exists so the M2 criterion
        /// "match results are written to the DB" can be checked by a test rather than by a human
        /// opening the file with a SQLite browser, which is what the phase document assumed.
        /// </summary>
        public List<MatchResultRecord> FindMatchResults(int roomId)
        {
            var results = new List<MatchResultRecord>();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT room_id, player_id, kills, deaths, score, ended_at FROM match_results WHERE room_id = $room ORDER BY id";
            cmd.Parameters.AddWithValue("$room", roomId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new MatchResultRecord
                {
                    RoomId = reader.GetInt32(0), PlayerId = reader.GetInt32(1), Kills = reader.GetInt32(2),
                    Deaths = reader.GetInt32(3), Score = reader.GetInt32(4), EndedAt = reader.GetInt64(5)
                });
            }
            return results;
        }

        private void Execute(string sql)
        {
            using SqliteCommand cmd = _connection.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();
    }
}
