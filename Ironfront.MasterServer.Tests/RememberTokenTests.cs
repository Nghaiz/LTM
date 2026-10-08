using System;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// "Remember me" signs a player back in without the password, and nothing else does (owner's
    /// list of 2026-10-09, item 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard tests are <see cref="ATokenWorksOnce"/> (delete the DELETE in
    /// <c>TakeRememberToken</c> and it goes red) and <see cref="OnlyTheHashIsStored"/> (store the
    /// plaintext and it goes red).
    /// </para>
    /// </remarks>
    public sealed class RememberTokenTests
    {
        private const string Username = "gunner";
        private const string Password = "aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899";
        private const string WrongPassword = "1111111111111111111111111111111111111111111111111111111111111111";

        [Fact]
        public void ARememberedTokenSignsTheOwnerIn()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            int playerId = database.FindAccount(Username)!.PlayerId;

            string token = auth.IssueRememberToken(playerId);
            AuthResult result = auth.LoginWithRememberToken(token, ip: 7);

            Assert.True(result.Ok);
            Assert.Equal(playerId, result.Session!.PlayerId);
            Assert.Equal("Gunner", result.Session.DisplayName);
            Assert.True(AuthService.IsValidRememberToken(token));
        }

        [Fact]
        public void ATokenWorksOnce()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            string token = auth.IssueRememberToken(database.FindAccount(Username)!.PlayerId);

            Assert.True(auth.LoginWithRememberToken(token, ip: 7).Ok);
            AuthResult second = auth.LoginWithRememberToken(token, ip: 7);

            Assert.False(second.Ok);
            Assert.Equal(ErrorCode.SessionExpired, second.ErrorCode);
            Assert.Null(second.Session);
        }

        [Fact]
        public void AnUnknownOrMalformedTokenIsExpiredNotWrongCredentials()
        {
            (AuthService auth, _) = Registered();

            Assert.Equal(ErrorCode.SessionExpired, auth.LoginWithRememberToken(new string('A', AuthService.RememberTokenLength), ip: 7).ErrorCode);
            Assert.Equal(ErrorCode.SessionExpired, auth.LoginWithRememberToken("short", ip: 7).ErrorCode);
            Assert.Equal(ErrorCode.SessionExpired, auth.LoginWithRememberToken(new string('*', AuthService.RememberTokenLength), ip: 7).ErrorCode);
        }

        [Fact]
        public void AnExpiredTokenIsRefusedAndRemoved()
        {
            (_, SqliteDatabase database) = Registered();
            int playerId = database.FindAccount(Username)!.PlayerId;
            database.InsertRememberToken("HASH", playerId, now: 1_000, expiresAt: 2_000);

            Assert.Null(database.TakeRememberToken("HASH", now: 2_000));
            Assert.Equal(0, database.CountRememberTokens(playerId));
        }

        [Fact]
        public void OnlyTheHashIsStored()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            int playerId = database.FindAccount(Username)!.PlayerId;
            string token = auth.IssueRememberToken(playerId);

            // The plaintext is not a key into the table: only its hash finds the row.
            Assert.Null(database.TakeRememberToken(token, now: 0));
            Assert.Equal(1, database.CountRememberTokens(playerId));
        }

        [Fact]
        public void AnAccountKeepsAtMostItsCapOfRememberedMachines()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            int playerId = database.FindAccount(Username)!.PlayerId;

            string first = auth.IssueRememberToken(playerId);
            for (int i = 0; i < SqliteDatabase.MaxRememberTokensPerPlayer; i++) auth.IssueRememberToken(playerId);

            Assert.Equal(SqliteDatabase.MaxRememberTokensPerPlayer, database.CountRememberTokens(playerId));
            Assert.False(auth.LoginWithRememberToken(first, ip: 7).Ok);
        }

        [Fact]
        public void ABannedAccountIsNamedToTheTokenHolder()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            int playerId = database.FindAccount(Username)!.PlayerId;
            string token = auth.IssueRememberToken(playerId);
            database.SetBanned(playerId, banned: true);

            Assert.Equal(ErrorCode.AccountBanned, auth.LoginWithRememberToken(token, ip: 7).ErrorCode);
        }

        [Fact]
        public void ALockedAccountIsLockedForTheTokenToo()
        {
            (AuthService auth, SqliteDatabase database) = Registered();
            string token = auth.IssueRememberToken(database.FindAccount(Username)!.PlayerId);
            for (int attempt = 0; attempt < 10; attempt++) auth.Login(Username, WrongPassword, ip: 1);

            AuthResult result = auth.LoginWithRememberToken(token, ip: 7);

            Assert.Equal(ErrorCode.AccountLocked, result.ErrorCode);
            Assert.True(result.RetryAfterSeconds > 0);
        }

        [Fact]
        public void TokenAttemptsShareTheAddressRateLimit()
        {
            var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database, ratePerMinute: 2);
            Assert.True(auth.Register(Username, Password, "Gunner").Ok);

            auth.LoginWithRememberToken("short", ip: 9);
            auth.LoginWithRememberToken("short", ip: 9);

            Assert.Equal(ErrorCode.RateLimited, auth.LoginWithRememberToken("short", ip: 9).ErrorCode);
        }

        private static (AuthService, SqliteDatabase) Registered()
        {
            var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database, ratePerMinute: 1000);
            Assert.True(auth.Register(Username, Password, "Gunner").Ok);
            return (auth, database);
        }

        /// <summary>The same rules through the real dispatcher and client, over TCP.</summary>
        [Collection(SocketTestCollection.Name)]
        public sealed class OverTheWire
        {
            private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
            private static readonly ushort[] Maps = { 1, 2, 3 };

            [Fact]
            public async Task ARememberedLoginSignsInAgainOnANewConnectionAndRotates()
            {
                using var cts = new CancellationTokenSource(Timeout);
                await using var server = new Phase03ServerHarness();

                string token;
                using (var first = new MasterClient.MasterClient())
                {
                    await first.ConnectAsync("127.0.0.1", server.Port, cts.Token);
                    Assert.True((await PumpAsync(first.RegisterAsync(Username, Password, "Gunner"), first)).Ok);
                    LoginResult login = await PumpAsync(first.LoginAsync(Username, Password, Maps, remember: true), first);
                    Assert.True(login.Ok);
                    Assert.True(AuthService.IsValidRememberToken(login.RememberToken));
                    token = login.RememberToken;
                }

                using var second = new MasterClient.MasterClient();
                await second.ConnectAsync("127.0.0.1", server.Port, cts.Token);
                LoginResult remembered = await PumpAsync(second.TokenLoginAsync(token, Maps), second);
                Assert.True(remembered.Ok);
                Assert.Equal("Gunner", remembered.DisplayName);
                Assert.NotEqual(token, remembered.RememberToken);
                Assert.True(AuthService.IsValidRememberToken(remembered.RememberToken));

                using var third = new MasterClient.MasterClient();
                await third.ConnectAsync("127.0.0.1", server.Port, cts.Token);
                LoginResult spent = await PumpAsync(third.TokenLoginAsync(token, Maps), third);
                Assert.False(spent.Ok);
                Assert.Equal((int)ErrorCode.SessionExpired, spent.ErrorCode);
            }

            [Fact]
            public async Task APlainLoginGetsNoToken()
            {
                using var cts = new CancellationTokenSource(Timeout);
                await using var server = new Phase03ServerHarness();
                using var client = new MasterClient.MasterClient();
                await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
                Assert.True((await PumpAsync(client.RegisterAsync(Username, Password, "Gunner"), client)).Ok);

                LoginResult login = await PumpAsync(client.LoginAsync(Username, Password, Maps), client);

                Assert.True(login.Ok);
                Assert.Equal(string.Empty, login.RememberToken);
            }

            private static async Task<T> PumpAsync<T>(Task<T> task, MasterClient.MasterClient client)
            {
                while (!task.IsCompleted)
                {
                    client.Poll();
                    await Task.Delay(5);
                }

                client.Poll();
                return await task;
            }
        }
    }
}
