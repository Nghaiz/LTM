using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A room's bot count reaches the game server that hosts it. Owner report 2026-09-28: a room
    /// created with 0 bots released 16 a side, because the count stopped at the master.
    /// </summary>
    /// <remarks>
    /// The count is bots PER TEAM, 0..<see cref="ProtocolConstants.MAX_BOTS_PER_TEAM"/> (owner
    /// ruling, same day), and it travels as <c>GS_ROOM_ASSIGNED</c> (0x0107), the first push the
    /// master makes to a game server.
    /// </remarks>
    public sealed class RoomBotCountTests
    {
        private const string PasswordHash =
            "b07c00000000000000000000000000000000000000000000000000000000b07c";

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

        // ------------------------------------------------------------------ the lobby's rule

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(16)]
        public void ARoomKeepsTheBotsPerTeamItWasCreatedWith(byte botsPerTeam)
        {
            var lobby = new LobbyService();

            // A 2-seat room may still ask for 16 a side: bots do not take seats.
            ServiceResult created = lobby.CreateRoom(SessionFor(1, "Một"),
                new RoomCreateRequest("Bots", 1, 2, botsPerTeam, false, null));

            Assert.True(created.Ok);
            Assert.Equal(botsPerTeam, created.Room!.BotCount);
        }

        [Theory]
        [InlineData(17)]
        [InlineData(32)]
        [InlineData(255)]
        public void MoreThanHalfTheMatchsBotBudgetPerTeamIsRefused(byte botsPerTeam)
        {
            var lobby = new LobbyService();

            ServiceResult created = lobby.CreateRoom(SessionFor(1, "Một"),
                new RoomCreateRequest("Bots", 1, 8, botsPerTeam, false, null));

            Assert.False(created.Ok);
            Assert.Empty(lobby.Rooms);
        }

        [Fact]
        public void AMatchmadeRoomGetsTheDesignRoster()
        {
            // Nobody typed a count for it. It used to ask for 0 -- harmless while nothing read the
            // number, and an empty match the moment something did.
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long now = 1_000_000;

            matchmaking.Enqueue(SessionFor(1, "Một"), 1, now);
            matchmaking.Enqueue(SessionFor(2, "Hai"), 1, now);
            List<Lobby.MatchmakeResult> matched = matchmaking.Tick(now);

            Assert.True(lobby.TryGetRoomById(matched[0].RoomId, out Room? room));
            Assert.Equal(ProtocolConstants.DEFAULT_BOTS_PER_TEAM, room!.BotCount);
        }

        // ------------------------------------------------------------------ over a real socket

        [Fact]
        public async Task EveryTicketTellsTheGameServerTheRoomAndItsBotsPerTeam()
        {
            await using var server = new Phase03ServerHarness();

            using var gameServer = new GameServerLink();
            var assignments = new List<GameServerRoomAssignment>();
            gameServer.OnRoomAssigned += assignments.Add;
            await gameServer.ConnectAsync("127.0.0.1", server.Port);
            GameServerRegistrationResult registered = await PumpAsync(
                gameServer.RegisterAsync(new GameServerRegistration
                {
                    ServerSecret = Phase03ServerHarness.SharedSecret,
                    PublicIp     = "127.0.0.1",
                    UdpPort      = 27015,
                    MaxPlayers   = 16,
                    MapIds       = new ushort[] { 1 },
                }),
                gameServer);
            Assert.True(registered.Ok, "no game server registered, so no room could be allocated to it");

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();
            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(alpha, "bots_alpha");
            await LoginAsync(beta,  "bots_beta");

            // 0 is the owner's case: a real request, not an absent one.
            CreateRoomResult created = await PumpAsync(
                alpha.CreateRoomAsync(new CreateRoomRequest { Name = "none", MapId = 1, MaxPlayers = 4, BotCount = 0 }),
                gameServer, alpha, beta);
            Assert.True(created.Ok);
            Assert.Empty(assignments);   // nothing allocated yet, so nobody to tell

            JoinResult joined = await PumpAsync(beta.JoinRoomAsync(created.RoomId, null), gameServer, alpha, beta);
            Assert.True(joined.Ok, $"beta could not join, errorCode {joined.ErrorCode}");

            // The creator's own ticket (the refresh arm) is a second ticket for the same room.
            JoinResult refreshed = await PumpAsync(alpha.JoinRoomAsync(created.RoomId, null), gameServer, alpha, beta);
            Assert.True(refreshed.Ok, $"alpha's ticket refresh failed, errorCode {refreshed.ErrorCode}");

            bool arrived = await PumpUntilAsync(() => assignments.Count >= 2, gameServer, alpha, beta);
            Assert.True(arrived, $"GS_ROOM_ASSIGNED reached the game server {assignments.Count} time(s) for two tickets");

            foreach (GameServerRoomAssignment assignment in assignments)
            {
                Assert.Equal(registered.ServerId, assignment.ServerId);
                Assert.Equal(created.RoomId, assignment.RoomId);
                Assert.Equal(1, assignment.MapId);
                Assert.Equal(0, assignment.BotsPerTeam);
            }

            // And the push did not answer anything by accident: the link still works for the
            // requests that follow it.
            Assert.Equal(MasterConnectionState.Connected, gameServer.State);
        }

        // ------------------------------------------------------------------ helpers

        private static Session SessionFor(int playerId, string name) => new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue,
        };

        private static async Task LoginAsync(IMasterClient client, string username)
        {
            try
            {
                await PumpAsync(client.RegisterAsync(username, PasswordHash, username), null, client);
            }
            catch (Exception)
            {
                // Already registered from an earlier run against a surviving temp database.
            }

            LoginResult login = await PumpAsync(client.LoginAsync(username, PasswordHash), null, client);
            Assert.True(login.Ok, $"'{username}' could not log in, errorCode {login.ErrorCode}");
        }

        private static async Task<T> PumpAsync<T>(Task<T> task, GameServerLink? gameServer, params IMasterClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < Budget)
            {
                Poll(gameServer, clients);
                await Task.Delay(5);
            }

            Poll(gameServer, clients);
            Assert.True(task.IsCompleted, "a master request never completed inside the budget");
            return await task;
        }

        private static async Task PumpAsync(Task task, GameServerLink? gameServer, params IMasterClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < Budget)
            {
                Poll(gameServer, clients);
                await Task.Delay(5);
            }

            Poll(gameServer, clients);
            await task;
        }

        private static async Task<bool> PumpUntilAsync(Func<bool> condition, GameServerLink gameServer, params IMasterClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < Budget)
            {
                Poll(gameServer, clients);
                if (condition()) return true;
                await Task.Delay(5);
            }

            Poll(gameServer, clients);
            return condition();
        }

        private static void Poll(GameServerLink? gameServer, IMasterClient[] clients)
        {
            gameServer?.Poll();
            for (int i = 0; i < clients.Length; i++) clients[i].Poll();
        }
    }
}
