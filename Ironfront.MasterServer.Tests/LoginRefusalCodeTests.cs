using System;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The four different reasons a login is refused, and the one code they all used to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect these pin.</b> <c>AuthService.Login</c> collapsed *wrong password*, *no such
    /// account*, *banned* and *locked out* into <see cref="ErrorCode.WrongCredentials"/>, which
    /// the client renders as "Wrong username or password." Two of those four are the opposite of
    /// a wrong password: the lock is armed by ten failures and lasts fifteen minutes, and during
    /// it the CORRECT password was answered with advice to go and change the password — advice
    /// that cannot help, because changing a password does not clear the lock.
    /// </para>
    /// <para>
    /// <b>The enumeration guarantee is the load-bearing test in this file</b>
    /// (<see cref="LockedAccountAnswersAWrongPasswordAsCredentials"/>). Naming the lock admits the
    /// account exists, so it is named only to somebody who supplied the right password. Delete
    /// the <c>verified</c> guard in <c>Login</c> and that test goes red while every other test
    /// here stays green — which is what makes it a guard and not a comment.
    /// </para>
    /// </remarks>
    public sealed class LoginRefusalCodeTests
    {
        private const string Username = "gunner";
        private const string RightPassword = "aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899";
        private const string WrongPassword = "1111111111111111111111111111111111111111111111111111111111111111";

        /// <summary>Ten failures is <c>MaxFailedLogins</c>; the eleventh finds the lock armed.</summary>
        private const int FailuresToLock = 10;

        [Fact]
        public void LockedAccountTellsTheOwnerItIsLocked()
        {
            (AuthService auth, _) = Registered();
            LockOut(auth);

            AuthResult result = auth.Login(Username, RightPassword, ip: 1);

            Assert.False(result.Ok);
            Assert.Equal(ErrorCode.AccountLocked, result.ErrorCode);
            Assert.Null(result.Session);
        }

        /// <summary>
        /// A locked account still answers a WRONG password as wrong credentials.
        /// </summary>
        /// <remarks>
        /// This is the username-enumeration boundary, stated as a test. An attacker guessing
        /// passwords is on this path and only this path, and it is indistinguishable from the
        /// answer a username nobody registered gets (<see cref="UnknownAccountIsIndistinguishable"/>),
        /// so the honest lockout message costs nothing an enumerator could spend.
        /// </remarks>
        [Fact]
        public void LockedAccountAnswersAWrongPasswordAsCredentials()
        {
            (AuthService auth, _) = Registered();
            LockOut(auth);

            AuthResult result = auth.Login(Username, WrongPassword, ip: 1);

            Assert.Equal(ErrorCode.WrongCredentials, result.ErrorCode);
        }

        [Fact]
        public void UnknownAccountIsIndistinguishable()
        {
            (AuthService auth, _) = Registered();

            AuthResult result = auth.Login("nobodyhere", RightPassword, ip: 1);

            Assert.Equal(ErrorCode.WrongCredentials, result.ErrorCode);
            Assert.Equal(0, result.RetryAfterSeconds);
        }

        /// <summary>
        /// The lockout reports how long is left, and it is the fifteen minutes the lock is for.
        /// </summary>
        /// <remarks>
        /// Bounded on both sides rather than asserted exactly: the value is derived from a live
        /// clock, so an exact equality would be a flake waiting for a slow machine. The lower
        /// bound is what matters — a client rendering "try again in 3 seconds" for a fifteen
        /// minute lock is the same failure in a new costume.
        /// </remarks>
        [Fact]
        public void LockoutReportsTheWaitItActuallyImposes()
        {
            (AuthService auth, _) = Registered();
            LockOut(auth);

            AuthResult result = auth.Login(Username, RightPassword, ip: 1);

            Assert.InRange(result.RetryAfterSeconds, 14 * 60, 15 * 60);
        }

        [Fact]
        public void BannedAccountSaysSoToItsOwner()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            Ban(database);

            AuthResult result = auth.Login(Username, RightPassword, ip: 1);

            Assert.Equal(ErrorCode.AccountBanned, result.ErrorCode);
        }

        [Fact]
        public void BannedAccountAnswersAWrongPasswordAsCredentials()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            Ban(database);

            AuthResult result = auth.Login(Username, WrongPassword, ip: 1);

            Assert.Equal(ErrorCode.WrongCredentials, result.ErrorCode);
        }

        /// <summary>
        /// The rate limit reports the window it is actually enforcing, not "a few seconds".
        /// </summary>
        /// <remarks>
        /// The budget is five attempts per sixty seconds per source address, and the client's
        /// message promised "a few seconds" — so a player who waited as instructed failed again
        /// and read the same sentence. Anything at or below a handful of seconds here means the
        /// promise is being made again.
        /// </remarks>
        [Fact]
        public void RateLimitReportsTheRestOfItsWindow()
        {
            // Its own service at the SHIPPED budget. The shared fixture raises the limit so that
            // LockOut can make eleven attempts from one address, which would mask this entirely.
            var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database);
            Assert.True(auth.Register(Username, RightPassword, "Gunner").Ok);

            AuthResult refused = default;
            for (int attempt = 0; attempt < AuthService.DefaultRatePerMinute + 1; attempt++)
                refused = auth.Login(Username, WrongPassword, ip: 7);

            Assert.Equal(ErrorCode.RateLimited, refused.ErrorCode);
            Assert.InRange(refused.RetryAfterSeconds, 50, 60);
        }

        /// <summary>
        /// A correct password during a lockout must not mint a session.
        /// </summary>
        /// <remarks>
        /// The refusal and the session are two separate writes, and a refactor that returns the
        /// honest code while still falling through to <c>CreateSession</c> would make the lock
        /// decorative. Counted rather than inspected, because the token is deliberately not
        /// exposed anywhere else.
        /// </remarks>
        [Fact]
        public void LockedLoginMintsNoSession()
        {
            (AuthService auth, _) = Registered();
            LockOut(auth);

            int before = auth.ActiveSessionCount;
            auth.Login(Username, RightPassword, ip: 1);

            Assert.Equal(before, auth.ActiveSessionCount);
        }

        /// <summary>Registers the account under test and hands back both halves.</summary>
        /// <remarks>
        /// The rate limit is raised for the fixture because <see cref="LockOut"/> needs eleven
        /// attempts from one address and the shipped budget is five per minute — which would
        /// otherwise refuse the sixth as <c>RateLimited</c> and never arm the lock at all. The
        /// one test that IS about the budget builds its own expectations from
        /// <see cref="AuthService.DefaultRatePerMinute"/> and uses a different address.
        /// </remarks>
        private static (AuthService, SqliteDatabase) Registered()
        {
            var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database, ratePerMinute: 1000);
            Assert.True(auth.Register(Username, RightPassword, "Gunner").Ok);
            return (auth, database);
        }

        /// <summary>Fails enough logins to arm the fifteen-minute lock.</summary>
        private static void LockOut(AuthService auth)
        {
            for (int attempt = 0; attempt < FailuresToLock; attempt++)
                Assert.Equal(ErrorCode.WrongCredentials, auth.Login(Username, WrongPassword, ip: 1).ErrorCode);
        }

        private static void Ban(SqliteDatabase database)
        {
            AccountRecord? account = database.FindAccount(Username);
            Assert.NotNull(account);
            database.SetBanned(account!.PlayerId, banned: true);
        }
    }
}
