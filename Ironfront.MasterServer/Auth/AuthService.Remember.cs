using System;
using System.Security.Cryptography;
using System.Text;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Auth
{
    /// <summary>
    /// "Remember me" (owner's list of 2026-10-09, item 1): a sign-in that skips the password on a
    /// machine the player chose to trust, for thirty days.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A token, not the password.</b> The client used to keep only the username, so "remember
    /// me" remembered half a login. Keeping the password (or the hash the client sends in its
    /// place, which is just as good to whoever holds it) would make a copied settings file a
    /// permanent key to the account. A token expires, is stored here only as a hash, and is
    /// replaced every time it is used.
    /// </para>
    /// <para>
    /// <b>It answers like a password.</b> The same per-address rate limit applies, a ban and a lock
    /// are named only to an owner who proved the account is theirs (the token did), and an unknown
    /// or expired token is <see cref="ErrorCode.SessionExpired"/> so the client knows to ask for the
    /// password rather than to call it wrong.
    /// </para>
    /// </remarks>
    public sealed partial class AuthService
    {
        /// <summary>How long a remembered sign-in lasts.</summary>
        public const long RememberDurationMs = 30L * 24 * 60 * 60 * 1000;

        /// <summary>The token's length on the wire: 32 random bytes in URL-safe base64.</summary>
        public const int RememberTokenLength = 43;

        private const long RememberReapIntervalMs = 60 * 60 * 1000;
        private long _nextRememberReap;

        /// <summary>Mints a remembered sign-in for <paramref name="playerId"/>; the plaintext is returned once and never stored.</summary>
        public string IssueRememberToken(int playerId)
        {
            long now = UnixMs();
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            string token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _database.InsertRememberToken(HashRememberToken(token), playerId, now, now + RememberDurationMs);
            return token;
        }

        /// <summary>Signs in with a remembered token, spending it. The caller issues the next one.</summary>
        public AuthResult LoginWithRememberToken(string token, uint ip)
        {
            long now = UnixMs();
            if (!AllowAttempt(ip, now))
                return new AuthResult(false, ErrorCode.RateLimited, null, RateRetryAfterSeconds(ip, now));
            if (!IsValidRememberToken(token)) return new AuthResult(false, ErrorCode.SessionExpired, null);

            AccountRecord? account = _database.TakeRememberToken(HashRememberToken(token), now);
            if (account is null) return new AuthResult(false, ErrorCode.SessionExpired, null);

            // The token proved ownership as a password would have, so the named refusals apply.
            if (account.IsBanned) return new AuthResult(false, ErrorCode.AccountBanned, null);
            if (account.LockedUntil > now)
                return new AuthResult(false, ErrorCode.AccountLocked, null, SecondsUntil(account.LockedUntil, now));

            _database.RecordLoginSuccess(account.PlayerId, now);
            Session session = CreateSession(account.PlayerId, account.DisplayName, ip, now);
            return new AuthResult(true, ErrorCode.Ok, session);
        }

        public static bool IsValidRememberToken(string? value)
        {
            if (value is null || value.Length != RememberTokenLength) return false;
            foreach (char c in value)
                if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c == '-' || c == '_'))
                    return false;
            return true;
        }

        private static string HashRememberToken(string token)
            => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

        private void ReapExpiredRememberTokens(long now)
        {
            if (now < _nextRememberReap) return;
            _nextRememberReap = now + RememberReapIntervalMs;
            _database.DeleteExpiredRememberTokens(now);
        }
    }
}
