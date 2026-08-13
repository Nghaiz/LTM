using System;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    public sealed class PhaseOneTwoServiceTests
    {
        private const string PasswordHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string SharedSecret = "this-is-a-long-enough-shared-secret-for-tests";

        [Fact]
        public void RegisterAndLoginStoresBcryptAndBindsSessionToIp()
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);

            RegisterResult registration = auth.Register("playerone", PasswordHash, "Người chơi");
            AuthResult login = auth.Login("playerone", PasswordHash, 0x7f000001);

            Assert.True(registration.Ok);
            Assert.True(login.Ok);
            Assert.NotNull(login.Session);
            AccountRecord account = Assert.IsType<AccountRecord>(database.FindAccount("PLAYERONE"));
            Assert.NotEqual(PasswordHash, account.PasswordHash);
            Assert.True(BCrypt.Net.BCrypt.Verify(PasswordHash, account.PasswordHash));
            Assert.True(auth.TryGetSession(login.Session!.Token, 0x7f000001, out _));
            Assert.False(auth.TryGetSession(login.Session.Token, 0x7f000002, out _));
        }

        [Fact]
        public void LoginRateLimitsSixthAttemptPerIp()
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);
            auth.Register("PlayerOne", PasswordHash, "Player One");

            for (int attempt = 0; attempt < 5; attempt++)
                Assert.Equal(ErrorCode.WrongCredentials, auth.Login("PlayerOne", OtherHash(), 1).ErrorCode);

            Assert.Equal(ErrorCode.RateLimited, auth.Login("PlayerOne", OtherHash(), 1).ErrorCode);
        }

        [Fact]
        public void UppercaseUsernameIsRejected()
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);

            Assert.False(auth.Register("PlayerOne", PasswordHash, "Player One").Ok);
        }

        [Fact]
        public void LobbyBalancesTeamsAndRequiresHashedPrivatePassword()
        {
            var lobby = new LobbyService();
            Session first = SessionFor(1, "Một");
            Session second = SessionFor(2, "Hai");
            Session third = SessionFor(3, "Ba");

            ServiceResult created = lobby.CreateRoom(first, new RoomCreateRequest("Private", 1, 4, 0, true, PasswordHash));
            Assert.True(created.Ok);
            Assert.Equal(ErrorCode.WrongRoomPassword, lobby.JoinRoom(second, created.Room!.RoomId, OtherHash()).ErrorCode);
            Assert.True(lobby.JoinRoom(second, created.Room.RoomId, PasswordHash).Ok);
            Assert.True(lobby.JoinRoom(third, created.Room.RoomId, PasswordHash).Ok);

            Assert.Equal((byte)0, created.Room.Members[0].Team);
            Assert.Equal((byte)1, created.Room.Members[1].Team);
            Assert.Equal((byte)0, created.Room.Members[2].Team);
        }

        [Fact]
        public void RegistryAuthenticatesOwnerAndReleasesAssignedRoomOnDisconnect()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Assert.False(registry.TryRegister(1, "wrong", "127.0.0.1", 27001, 16, new ushort[] { 1 }, now, out _));
            Assert.True(registry.TryRegister(1, SharedSecret, "127.0.0.1", 27001, 16, new ushort[] { 1 }, now, out GameServerRecord? server));
            Assert.NotNull(server);
            Assert.False(registry.Heartbeat(2, server!.ServerId, 0, 1, 1, 0, now));
            Assert.True(registry.Heartbeat(1, server.ServerId, 0, 1, 1, 0, now));
            Assert.Same(server, registry.Allocate(1, 42, now));
            Assert.Equal(new[] { 42 }, registry.RemoveConnection(1));
        }

        [Fact]
        public void ChatStripsControlsAndEnforcesWindowLimit()
        {
            var chat = new ChatService();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int message = 0; message < 5; message++)
                Assert.True(chat.TryCreate(0, 1, "Một", "hello\nworld", now, out _));

            Assert.False(chat.TryCreate(0, 1, "Một", "blocked", now, out _));
            Assert.True(chat.TryCreate(0, 2, "Hai", "xin‮chào\n", now, out ChatMessage? sanitized));
            Assert.NotNull(sanitized);
            Assert.Equal("xinchào", sanitized!.Text);
        }

        private static SqliteDatabase CreateDatabase() => new SqliteDatabase(":memory:");

        private static Session SessionFor(int playerId, string name) => new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue
        };

        private static string OtherHash() => "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    }
}
