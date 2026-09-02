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
        public AuthResult(bool ok, ErrorCode errorCode, Session? session, int retryAfterSeconds = 0)
        {
            Ok = ok; ErrorCode = errorCode; Session = session; RetryAfterSeconds = retryAfterSeconds;
        }
        public bool Ok { get; }
        public ErrorCode ErrorCode { get; }
        public Session? Session { get; }

        /// <summary>
        /// Seconds until this refusal stops applying, or 0 when waiting does not help.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The number, not an adjective.</b> The client rendered every
        /// <see cref="ErrorCode.RateLimited"/> as "Wait a few seconds and try again" against a
        /// window of sixty, and a lockout — reported as a wrong password — as nothing at all.
        /// A player who waits the few seconds they were promised, fails again, and is told the
        /// same thing has been given a loop rather than an instruction.
        /// </para>
        /// <para>
        /// Rounded UP, so a wait this reports as over really is over. Reporting 0 on a 400 ms
        /// remainder would invite an immediate retry that fails for the same reason.
        /// </para>
        /// </remarks>
        public int RetryAfterSeconds { get; }
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

        /// <summary>The phase-01 security table's number: 5 login attempts per minute per IP.</summary>
        public const int DefaultRatePerMinute = 5;

        private const long LockDurationMs = 15 * 60 * 1000;
        private const long SessionDurationMs = 24 * 60 * 60 * 1000;
        private const string DummyHash = "$2a$11$BHywQ2fudMwWA.zauC4w5.dsi8MZqOZIgvQI0P02ldviQkqFypvje";

        private readonly SqliteDatabase _database;
        private readonly int _ratePerMinute;
        private readonly Dictionary<uint, RateWindow> _rates = new Dictionary<uint, RateWindow>();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);

        public AuthService(SqliteDatabase database)
            : this(database, DefaultRatePerMinute)
        {
        }

        /// <summary>
        /// Creates the service with an explicit per-IP login rate limit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Configurable because of what the phase-03 load test found. The limit counts
        /// attempts per <b>source address</b>, and every bot on a test rig shares one — so a
        /// 16-client run logs five bots in and gets error 9001 for the other eleven. Measured:
        /// 16 bots produced 5 sessions and 11 <c>RateLimited</c> failures, and the run then
        /// silently measured a five-player lobby while claiming sixteen.
        /// </para>
        /// <para>
        /// The default stays at 5. It is the right number against a brute-force attempt from
        /// one address, and moving it because a benchmark found it inconvenient would let the
        /// benchmark set the security policy. What was missing was a way for the operator to
        /// say "this address is the test rig", which is a deployment statement, not a change
        /// to the defence.
        /// </para>
        /// </remarks>
        public AuthService(SqliteDatabase database, int ratePerMinute)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            if (ratePerMinute < 1) throw new ArgumentOutOfRangeException(nameof(ratePerMinute));
            _ratePerMinute = ratePerMinute;
        }

        /// <summary>
        /// Sessions currently held. This is the "online now" figure on the metrics endpoint,
        /// and comparing it against the raw connection count is the leak check the operations
        /// runbook asks for: the two should track each other, and a connection count that
        /// climbs while this stays flat means connections are not being released.
        /// </summary>
        public int ActiveSessionCount => _sessions.Count;

        /// <summary>
        /// Longest display name the master stores. A username is at most 16 characters
        /// (<see cref="IsValidUsername"/>), so the blank-name fallback can never exceed it.
        /// </summary>
        private const int MaxDisplayNameLength = 32;

        public RegisterResult Register(string username, string passwordHash, string displayName)
        {
            if (!IsValidUsername(username)) return new RegisterResult(false, ErrorCode.InvalidUsername);
            if (!IsValidSha256(passwordHash)) return new RegisterResult(false, ErrorCode.WrongCredentials);

            // THE MASTER'S OWN RULE for a blank display name, which until 2026-09-03 did not
            // exist while two places in the client promised it did: the Canvas labels the field
            // "Display name (optional)" (BuildMenuCanvas.cs:273) and MenuRegisterScreen's
            // docstring says "Left blank, the master applies its own rule." Blank was in fact
            // REFUSED -- and refused as WrongCredentials, so a player who left the optional
            // field alone was told "Wrong username or password." on a form that has no
            // credentials to be wrong yet. Account creation failed 100% of the time.
            //
            // Trimmed before the emptiness test, so "   " is blank rather than a three-space
            // name: whitespace is what an accidental keypress leaves behind, and a lobby row
            // rendering as nothing at all is the same defect one screen later.
            string name = (displayName ?? string.Empty).Trim();
            if (name.Length == 0) name = username;

            // A name that was SUPPLIED and is unusable is a different answer, and gets a
            // different code (ErrorCode.InvalidDisplayName, protocol-spec.md SS 13). The
            // credential codes stay for credentials -- see the malformed-hash line above, which
            // is what WrongCredentials genuinely describes.
            if (name.Length > MaxDisplayNameLength)
                return new RegisterResult(false, ErrorCode.InvalidDisplayName);

            string stored = BCrypt.Net.BCrypt.HashPassword(passwordHash, BcryptCost);
            bool inserted = _database.InsertAccount(username, stored, name, UnixMs());
            return new RegisterResult(inserted, inserted ? ErrorCode.Ok : ErrorCode.UsernameTaken);
        }

        /// <summary>
        /// Verifies a credential and mints a session, or says why it would not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Four different refusals used to leave here as one code.</b> A wrong password, an
        /// account that does not exist, a banned account and one locked out after ten failures
        /// all returned <see cref="ErrorCode.WrongCredentials"/>, and the client rendered all
        /// four as "Wrong username or password." Two of those four are the OPPOSITE of a wrong
        /// password: the lock is armed by failures and lasts fifteen minutes, during which the
        /// correct password is refused with advice to go and change it — advice that cannot
        /// work, because a password change does not clear the lock either.
        /// </para>
        /// <para>
        /// <b>The name is spent only on somebody who proved they own the account.</b> Naming a
        /// lock or a ban admits the account exists, so both are withheld unless the supplied
        /// password VERIFIED. Along the guessing path — the only path an enumerator has — every
        /// answer is still <see cref="ErrorCode.WrongCredentials"/>, identical to a username
        /// nobody has registered, so this leaks nothing that was not already leakable. That is
        /// why the trade-off did not need to be traded: the honest message and the silent one
        /// are not on the same branch. <c>LockedAccountRefusesAWrongPasswordAsCredentials</c>
        /// pins it.
        /// </para>
        /// <para>
        /// <b>The order of the two named states is ban before lock.</b> A banned account that is
        /// also mid-lockout is banned; telling its owner to wait fifteen minutes would be false.
        /// </para>
        /// </remarks>
        public AuthResult Login(string username, string passwordHash, uint ip)
        {
            long now = UnixMs();
            if (!AllowAttempt(ip, now))
                return new AuthResult(false, ErrorCode.RateLimited, null, RateRetryAfterSeconds(ip, now));
            if (!IsValidUsername(username)) return new AuthResult(false, ErrorCode.InvalidUsername, null);
            if (!IsValidSha256(passwordHash)) return new AuthResult(false, ErrorCode.WrongCredentials, null);

            AccountRecord? account = _database.FindAccount(username);

            // Runs against a dummy hash for an account that does not exist, so the bcrypt cost is
            // paid either way and the answer cannot be timed. Unchanged, and load-bearing.
            bool verified = BCrypt.Net.BCrypt.Verify(passwordHash, account?.PasswordHash ?? DummyHash);

            if (account is null || !verified)
            {
                // A failure against a live, unlocked, unbanned account is what arms the lock.
                // A failure against a locked one does NOT extend it — the fifteen minutes are
                // punishment for the ten attempts already made, not a treadmill that a
                // still-guessing attacker can keep the owner on indefinitely.
                if (account is not null && !account.IsBanned && account.LockedUntil <= now)
                    _database.RecordLoginFailure(account.PlayerId, MaxFailedLogins, now + LockDurationMs);

                return new AuthResult(false, ErrorCode.WrongCredentials, null);
            }

            if (account.IsBanned) return new AuthResult(false, ErrorCode.AccountBanned, null);

            if (account.LockedUntil > now)
                return new AuthResult(
                    false, ErrorCode.AccountLocked, null, SecondsUntil(account.LockedUntil, now));

            _database.RecordLoginSuccess(account.PlayerId, now);
            Session session = CreateSession(account.PlayerId, account.DisplayName, ip, now);
            return new AuthResult(true, ErrorCode.Ok, session);
        }

        /// <summary>Whole seconds from <paramref name="now"/> to <paramref name="deadline"/>, rounded up.</summary>
        private static int SecondsUntil(long deadline, long now)
        {
            long remaining = deadline - now;
            return remaining <= 0 ? 0 : (int)((remaining + 999) / 1000);
        }

        /// <summary>
        /// Seconds until this address's login budget resets. Reads the window; never opens one.
        /// </summary>
        /// <remarks>
        /// Called only after <see cref="AllowAttempt"/> has already refused, so the window it
        /// reads is guaranteed to exist and to be the one that did the refusing. It must not
        /// create a window of its own — doing so from a refusal path would restart the clock the
        /// caller is asking about.
        /// </remarks>
        private int RateRetryAfterSeconds(uint ip, long now)
            => _rates.TryGetValue(ip, out RateWindow? window)
                ? SecondsUntil(window.StartedAt + 60_000, now)
                : 0;

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
            return window.Attempts <= _ratePerMinute;
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
