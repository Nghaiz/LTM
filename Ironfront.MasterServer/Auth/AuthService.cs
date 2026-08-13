using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Auth
{
    public sealed class Session
    {
        public required string Token { get; init; }
        public required int PlayerId { get; init; }
        public required string DisplayName { get; init; }
        public required uint Ip { get; init; }
        public required long ExpiresAt { get; init; }
    }

    public readonly struct AuthResult
    {
        public AuthResult(bool ok, ErrorCode errorCode, Session? session)
        {
            Ok = ok; ErrorCode = errorCode; Session = session;
        }
        public bool Ok { get; }
        public ErrorCode ErrorCode { get; }
        public Session? Session { get; }
    }

    internal sealed class RateWindow
    {
        public long StartedAt { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class AuthService
    {
        private const int BcryptCost = 11;
        private const int MaxFailedLogins = 10;
        private const int RatePerMinute = 5;
        private const long LockDurationMs = 15 * 60 * 1000;
        private const long SessionDurationMs = 24 * 60 * 60 * 1000;
        private const string DummyHash = "$2a$11$BHywQ2fudMwWA.zauC4w5.dsi8MZqOZIgvQI0P02ldviQkqFypvje";

        private readonly SqliteDatabase _database;
        private readonly Dictionary<uint, RateWindow> _rates = new Dictionary<uint, RateWindow>();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);

        public AuthService(SqliteDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

        public RegisterResult Register(string username, string passwordHash, string displayName)
        {
            if (!IsValidUsername(username)) return new RegisterResult(false, ErrorCode.InvalidUsername);
            if (!IsValidSha256(passwordHash) || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 32)
                return new RegisterResult(false, ErrorCode.WrongCredentials);
            string stored = BCrypt.Net.BCrypt.HashPassword(passwordHash, BcryptCost);
            bool inserted = _database.InsertAccount(username, stored, displayName.Trim(), UnixMs());
            return new RegisterResult(inserted, inserted ? ErrorCode.Ok : ErrorCode.UsernameTaken);
        }

        public AuthResult Login(string username, string passwordHash, uint ip)
        {
            long now = UnixMs();
            if (!AllowAttempt(ip, now)) return new AuthResult(false, ErrorCode.RateLimited, null);
            if (!IsValidUsername(username)) return new AuthResult(false, ErrorCode.InvalidUsername, null);
            if (!IsValidSha256(passwordHash)) return new AuthResult(false, ErrorCode.WrongCredentials, null);

            AccountRecord? account = _database.FindAccount(username);
            bool verified = BCrypt.Net.BCrypt.Verify(passwordHash, account?.PasswordHash ?? DummyHash);
            if (account is null || !verified || account.IsBanned || account.LockedUntil > now)
            {
                if (account is not null && !account.IsBanned && account.LockedUntil <= now)
                    _database.RecordLoginFailure(account.PlayerId, MaxFailedLogins, now + LockDurationMs);
                return new AuthResult(false, ErrorCode.WrongCredentials, null);
            }

            _database.RecordLoginSuccess(account.PlayerId, now);
            Session session = CreateSession(account.PlayerId, account.DisplayName, ip, now);
            return new AuthResult(true, ErrorCode.Ok, session);
        }

        public bool TryGetSession(string token, uint ip, out Session? session)
        {
            session = null;
            if (!_sessions.TryGetValue(token, out Session? candidate)) return false;
            if (candidate.Ip != ip || candidate.ExpiresAt <= UnixMs()) { _sessions.Remove(token); return false; }
            session = candidate; return true;
        }

        public void RemoveSession(string token) => _sessions.Remove(token);

        public void ReapExpiredSessions(long now)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, Session> item in _sessions)
                if (item.Value.ExpiresAt <= now) expired.Add(item.Key);
            foreach (string token in expired) _sessions.Remove(token);
        }

        public static bool IsValidUsername(string? value)
        {
            if (value is null || value.Length < 3 || value.Length > 16) return false;
            foreach (char c in value)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        public static bool IsValidSha256(string? value)
        {
            if (value is null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        private bool AllowAttempt(uint ip, long now)
        {
            if (!_rates.TryGetValue(ip, out RateWindow? window) || now - window.StartedAt >= 60_000)
            {
                _rates[ip] = new RateWindow { StartedAt = now, Attempts = 1 }; return true;
            }
            window.Attempts++;
            return window.Attempts <= RatePerMinute;
        }

        private Session CreateSession(int playerId, string displayName, uint ip, long now)
        {
            Span<byte> bytes = stackalloc byte[32]; RandomNumberGenerator.Fill(bytes);
            string token = Convert.ToHexString(bytes);
            var session = new Session { Token = token, PlayerId = playerId, DisplayName = displayName, Ip = ip, ExpiresAt = now + SessionDurationMs };
            _sessions[token] = session; return session;
        }

        private static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public readonly struct RegisterResult
    {
        public RegisterResult(bool ok, ErrorCode errorCode) { Ok = ok; ErrorCode = errorCode; }
        public bool Ok { get; }
        public ErrorCode ErrorCode { get; }
    }
}
