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
            // Lowercase on purpose: AuthService rejects an uppercase username outright, so
            // "PlayerOne" would come back InvalidUsername and never reach the credential check
            // this test is about. See UppercaseUsernameIsRejected.
            auth.Register("playerone", PasswordHash, "Player One");

            for (int attempt = 0; attempt < 5; attempt++)
                Assert.Equal(ErrorCode.WrongCredentials, auth.Login("playerone", OtherHash(), 1).ErrorCode);

            Assert.Equal(ErrorCode.RateLimited, auth.Login("playerone", OtherHash(), 1).ErrorCode);
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

        /// <summary>
        /// The team the lobby balanced onto a member is the team its join ticket carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The step that did not exist.</b> The lobby computed a side on every join and
        /// then had nowhere to put it: the ticket's 32 signed bytes were exactly full, so the
        /// game server re-derived a team from slot parity and the lobby's answer was thrown
        /// away. This asserts the two lines the dispatcher now runs between them — find the
        /// member the join created, and sign THAT member's team into the ticket.
        /// </para>
        /// <para>
        /// <b>Scope, stated rather than implied.</b> This drives <c>LobbyService</c> and
        /// <c>JoinTicket</c> directly, not <c>MspMessageDispatcher</c> over a socket — there is
        /// no dispatcher-level RoomJoin harness in this project to extend. It pins the contract
        /// the dispatcher implements; it does not prove the dispatcher calls it. What proves
        /// that end to end is criterion 4's two-client lane-B run, read off the SERVER's log.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheTeamTheLobbyBalancedIsTheTeamTheTicketCarries()
        {
            var lobby = new LobbyService();
            Session host = SessionFor(1, "Một");
            Session guest = SessionFor(2, "Hai");

            ServiceResult created = lobby.CreateRoom(
                host, new RoomCreateRequest("Balanced", 1, 4, 0, false, null));
            Assert.True(created.Ok);
            Assert.True(lobby.JoinRoom(guest, created.Room!.RoomId, null).Ok);

            byte[] secret = System.Text.Encoding.UTF8.GetBytes(SharedSecret);

            foreach (RoomMember member in created.Room.Members)
            {
                var ticket = new byte[JoinTicket.Size];
                Assert.Equal(JoinTicket.Size, JoinTicket.Issue(
                    ticket,
                    playerId: (uint)member.PlayerId,
                    serverId: 7,
                    roomId: (ushort)created.Room.RoomId,
                    expiresAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000,
                    team: member.Team,
                    displayName: member.DisplayName,
                    sharedSecret: secret));

                Assert.True(JoinTicket.TryReadFields(
                    ticket, out uint playerId, out _, out _, out _,
                    out byte team, out _));

                Assert.Equal((uint)member.PlayerId, playerId);
                Assert.Equal(member.Team, team);
            }

            // The two members are on opposite sides, so the loop above proved both values
            // rather than proving 0 twice.
            Assert.Equal((byte)0, created.Room.Members[0].Team);
            Assert.Equal((byte)1, created.Room.Members[1].Team);
        }

        /// <summary>
        /// <c>RoomMember.Team</c> can be written after construction.
        /// </summary>
        /// <remarks>
        /// It was <c>{ get; init; }</c>, which made a lobby side-switch impossible to express
        /// at all — the field could not change once the member existed, so no endpoint could
        /// have been written against it. The switch message and the UI that sends it are P16's;
        /// this is the field they need and deliberately nothing more. The test exists because a
        /// property whose only observable difference is that it COMPILES has nothing else to
        /// catch a revert.
        /// </remarks>
        [Fact]
        public void AMembersTeamCanBeChangedAfterTheJoin()
        {
            var lobby = new LobbyService();
            Session host = SessionFor(1, "Một");

            ServiceResult created = lobby.CreateRoom(
                host, new RoomCreateRequest("Switchable", 1, 4, 0, false, null));
            Assert.True(created.Ok);

            RoomMember member = created.Room!.Members[0];
            Assert.Equal((byte)0, member.Team);

            member.Team = 1;

            Assert.Equal((byte)1, created.Room.Members[0].Team);
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
