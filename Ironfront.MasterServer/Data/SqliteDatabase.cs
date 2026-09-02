using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>
        /// Sets or clears an account's ban flag.
        /// </summary>
        /// <remarks>
        /// <b>Nothing in the shipped master calls this yet, and that is stated rather than
        /// hidden.</b> The <c>is_banned</c> column has existed since the schema was written and
        /// <c>AuthService.Login</c> has always branched on it, but there is no operator surface
        /// that sets it — a ban is applied by editing the database by hand. This method exists so
        /// the refusal path is reachable from a test at all; adding the admin endpoint that would
        /// call it is a separate piece of work, and pretending otherwise by leaving the column
        /// unwritable would only make the gap harder to find.
        /// </remarks>
        public void SetBanned(int playerId, bool banned)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET is_banned = $banned WHERE player_id = $id";
            cmd.Parameters.AddWithValue("$banned", banned ? 1 : 0); cmd.Parameters.AddWithValue("$id", playerId); cmd.ExecuteNonQuery();
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

        /// <summary>Accounts in the database. Reported by the metrics endpoint.</summary>
        public int CountAccounts()
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM accounts";
            object? scalar = cmd.ExecuteScalar();
            return scalar is long count ? (int)count : 0;
        }

        /// <summary>
        /// Writes a consistent copy of the live database to <paramref name="destinationPath"/>
        /// using SQLite's online backup API (phase 03 task 6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><c>cp ironfront.db backup.db</c> is not a backup.</b> It is a byte copy of a file
        /// that is being written to, and in WAL mode — which this connection turns on — it is
        /// worse than that: the committed state lives partly in <c>ironfront.db-wal</c>, so
        /// copying only the main file can produce a backup that is both corrupt and older than
        /// the last commit. The backup API takes a read lock per page, copies through SQLite
        /// itself, and produces a single file that is a valid database as of a real instant.
        /// </para>
        /// <para>
        /// Backing up an <b>open</b> connection is the point: the server does not stop. The
        /// cron job in <c>tools/backup.sh</c> runs against a running master.
        /// </para>
        /// <para>
        /// A backup nobody has restored is not a backup, which is why criterion 10 asks for a
        /// tested restore and why the test suite performs one rather than merely asserting
        /// that a file appeared.
        /// </para>
        /// </remarks>
        public void BackupTo(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Destination path is required.", nameof(destinationPath));

            string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Overwrite rather than append-to-existing: SqliteConnection.BackupDatabase copies
            // into whatever is there, and a stale larger file left behind would be silently
            // reused as the target's page store.
            if (File.Exists(destinationPath)) File.Delete(destinationPath);

            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,

                // Pooling OFF, and this is load-bearing rather than a micro-optimisation.
                // Microsoft.Data.Sqlite pools connections by connection string, so a pooled
                // destination is returned to the pool on Dispose with its file handle still
                // open — and the NEXT backup to the same path then fails at the File.Delete
                // above with "the process cannot access the file". A backup job that works
                // the first time and fails every time after is exactly the kind of failure
                // nobody notices until they need the backup. Found by
                // BackingUpTwiceOverwritesRatherThanMergingIntoTheOldFile.
                Pooling = false,
            }.ToString();

            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                destination.Open();
                _connection.BackupDatabase(destination);
            }

            // Belt and braces: if a pooled connection for this path exists from anywhere else
            // in the process, release it so the file is not left mapped.
            SqliteConnection.ClearPool(new SqliteConnection(destinationConnectionString));
        }

        private void Execute(string sql)
        {
            using SqliteCommand cmd = _connection.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();
    }
}
