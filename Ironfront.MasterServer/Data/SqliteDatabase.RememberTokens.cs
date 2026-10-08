using Microsoft.Data.Sqlite;

namespace Ironfront.MasterServer.Data
{
    /// <summary>
    /// "Remember me" tokens: a long-lived sign-in a player's own machine keeps, so the next visit
    /// skips the password (owner's list of 2026-10-09, item 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a hash is stored.</b> The token is a bearer credential; a copy of this file must not
    /// sign anybody in, so the row holds its SHA-256 and the plaintext exists only on the player's
    /// machine and on the wire.
    /// </para>
    /// <para>
    /// <b>A token is spent by using it.</b> <see cref="TakeRememberToken"/> deletes the row it
    /// reads, and the caller issues a fresh one with the new session, so a token copied off a
    /// machine works at most once, and its use logs the owner out of the next automatic sign-in.
    /// </para>
    /// </remarks>
    public sealed partial class SqliteDatabase
    {
        /// <summary>How many remembered machines one account keeps; the oldest goes first.</summary>
        public const int MaxRememberTokensPerPlayer = 8;

        private void CreateRememberTokenTable() => Execute(@"
CREATE TABLE IF NOT EXISTS remember_tokens (
    token_hash TEXT PRIMARY KEY,
    player_id INTEGER NOT NULL REFERENCES accounts(player_id),
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_remember_player ON remember_tokens(player_id);");

        /// <summary>Stores a token's hash for <paramref name="playerId"/> and trims the account's oldest beyond the cap.</summary>
        public void InsertRememberToken(string tokenHash, int playerId, long now, long expiresAt)
        {
            using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO remember_tokens(token_hash, player_id, created_at, expires_at) VALUES ($hash, $id, $now, $expires)";
                insert.Parameters.AddWithValue("$hash", tokenHash);
                insert.Parameters.AddWithValue("$id", playerId);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$expires", expiresAt);
                insert.ExecuteNonQuery();
            }

            using SqliteCommand trim = _connection.CreateCommand();
            trim.CommandText = "DELETE FROM remember_tokens WHERE player_id = $id AND token_hash NOT IN "
                               + "(SELECT token_hash FROM remember_tokens WHERE player_id = $id ORDER BY created_at DESC, rowid DESC LIMIT $keep)";
            trim.Parameters.AddWithValue("$id", playerId);
            trim.Parameters.AddWithValue("$keep", MaxRememberTokensPerPlayer);
            trim.ExecuteNonQuery();
        }

        /// <summary>
        /// Removes the token whose hash is <paramref name="tokenHash"/> and answers its account, or
        /// null when it is unknown or has expired (an expired row is removed all the same).
        /// </summary>
        public AccountRecord? TakeRememberToken(string tokenHash, long now)
        {
            int playerId;
            long expiresAt;
            using (SqliteCommand find = _connection.CreateCommand())
            {
                find.CommandText = "SELECT player_id, expires_at FROM remember_tokens WHERE token_hash = $hash";
                find.Parameters.AddWithValue("$hash", tokenHash);
                using SqliteDataReader reader = find.ExecuteReader();
                if (!reader.Read()) return null;
                playerId = reader.GetInt32(0);
                expiresAt = reader.GetInt64(1);
            }

            using (SqliteCommand delete = _connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM remember_tokens WHERE token_hash = $hash";
                delete.Parameters.AddWithValue("$hash", tokenHash);
                delete.ExecuteNonQuery();
            }

            return expiresAt > now ? FindAccountById(playerId) : null;
        }

        /// <summary>How many remembered machines <paramref name="playerId"/> has.</summary>
        public int CountRememberTokens(int playerId)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM remember_tokens WHERE player_id = $id";
            cmd.Parameters.AddWithValue("$id", playerId);
            return System.Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>Drops every expired token; the reaper's job, so the table does not grow with dead rows.</summary>
        public void DeleteExpiredRememberTokens(long now)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM remember_tokens WHERE expires_at <= $now";
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        public AccountRecord? FindAccountById(int playerId)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT player_id, username, password_hash, display_name, locked_until, is_banned FROM accounts WHERE player_id = $id";
            cmd.Parameters.AddWithValue("$id", playerId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new AccountRecord
            {
                PlayerId = reader.GetInt32(0), Username = reader.GetString(1), PasswordHash = reader.GetString(2),
                DisplayName = reader.GetString(3), LockedUntil = reader.GetInt64(4), IsBanned = reader.GetInt64(5) != 0
            };
        }
    }
}
