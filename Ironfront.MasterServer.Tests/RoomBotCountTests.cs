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
    /// Since protocol 13 the count is the match's TOTAL, 0..<see cref="ProtocolConstants.MAX_BOTS"/>
    /// and even (owner, 2026-09-30: a 0-100 slider), bounded by what the game-server host can still
    /// carry (<see cref="BotCapacity"/>). It travels as half a side in <c>GS_ROOM_ASSIGNED</c>
    /// (0x0107), the first push the master makes to a game server.
    /// </remarks>
    public sealed class RoomBotCountTests
    {
        private const string PasswordHash =
            "b07c00000000000000000000000000000000000000000000000000000000b07c";

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

        // ------------------------------------------------------------------ the lobby's rule

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(50)]
        [InlineData(100)]
        public void ARoomKeepsTheBotTotalItWasCreatedWith(byte bots)
        {
            var lobby = new LobbyService();

            // A 2-seat room may still ask for 100: bots do not take seats.
            ServiceResult created = lobby.CreateRoom(SessionFor(1, "Một"),
                new RoomCreateRequest("Bots", 1, 2, bots, false, null));

            Assert.True(created.Ok);
            Assert.Equal(bots, created.Room!.BotCount);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(49)]
        [InlineData(102)]
        [InlineData(255)]
        public void AnOddOrOversizedTotalIsRefused(byte bots)
        {
            var lobby = new LobbyService();

            ServiceResult created = lobby.CreateRoom(SessionFor(1, "Một"),
                new RoomCreateRequest("Bots", 1, 8, bots, false, null));

            Assert.False(created.Ok);
            Assert.Empty(lobby.Rooms);
        }

        [Fact]
        public void AMatchmadeRoomGetsTheRoomDefault()
        {
            // Nobody chose a count for it; it gets what a fresh create form starts at.
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long now = 1_000_000;

            matchmaking.Enqueue(SessionFor(1, "Một"), 1, now);
            matchmaking.Enqueue(SessionFor(2, "Hai"), 1, now);
            List<Lobby.MatchmakeResult> matched = matchmaking.Tick(now);

            Assert.True(lobby.TryGetRoomById(matched[0].RoomId, out Room? room));
            Assert.Equal(ProtocolConstants.DEFAULT_ROOM_BOTS, room!.BotCount);
        }

        // ------------------------------------------------------------------ the host's capacity

        /// <summary>
        /// The owner's two measured ceilings are the same budget: two matches of 100 bots, or three
        /// of 50, and not a bot more.
        /// </summary>
        [Fact]
        public void TwoRoomsOfAHundredOrThreeOfFiftyFillTheHost()
        {
            var two = new LobbyService();
            Assert.Equal(100, two.MaxBotsForNewRoom());
            Assert.True(two.CreateRoom(SessionFor(1, "A"), new RoomCreateRequest("a", 1, 4, 100, false, null)).Ok);
            Assert.Equal(100, two.MaxBotsForNewRoom());
            Assert.True(two.CreateRoom(SessionFor(2, "B"), new RoomCreateRequest("b", 2, 4, 100, false, null)).Ok);
            Assert.Equal(0, two.MaxBotsForNewRoom());
            Assert.Equal(ErrorCode.ServerAtBotCapacity,
                two.CreateRoom(SessionFor(3, "C"), new RoomCreateRequest("c", 1, 4, 0, false, null)).ErrorCode);

            var three = new LobbyService();
            Assert.True(three.CreateRoom(SessionFor(1, "A"), new RoomCreateRequest("a", 1, 4, 50, false, null)).Ok);
            Assert.True(three.CreateRoom(SessionFor(2, "B"), new RoomCreateRequest("b", 2, 4, 50, false, null)).Ok);
            Assert.Equal(50, three.MaxBotsForNewRoom());
            Assert.Equal(ErrorCode.ServerAtBotCapacity,
                three.CreateRoom(SessionFor(3, "C"), new RoomCreateRequest("c", 1, 4, 52, false, null)).ErrorCode);
            Assert.True(three.CreateRoom(SessionFor(3, "C"), new RoomCreateRequest("c", 1, 4, 50, false, null)).Ok);
            Assert.Equal(0, three.MaxBotsForNewRoom());
        }

        /// <summary>
        /// The owner's example: five rooms of 16 already overrun the host, so a sixth gets nothing;
        /// a room that closes gives its share back.
        /// </summary>
        [Fact]
        public void TheBudgetFollowsTheRoomsThatExist()
        {
            var lobby = new LobbyService();
            for (int i = 1; i <= 3; i++)
                Assert.True(lobby.CreateRoom(SessionFor(i, "P" + i), new RoomCreateRequest("r" + i, 1, 4, 16, false, null)).Ok);

            // 300 - 3 x (50 + 16) - 50 = 52 left for a fourth room.
            Assert.Equal(52, lobby.MaxBotsForNewRoom());

            Assert.True(lobby.LeaveRoom(1).Ok);
            Assert.Equal(100, lobby.MaxBotsForNewRoom());
        }

        [Fact]
        public void TheBudgetIsTheMastersToConfigure()
        {
            var lobby = new LobbyService { Capacity = new BotCapacity(budgetUnits: 600, matchCostUnits: 20) };
            Assert.True(lobby.CreateRoom(SessionFor(1, "A"), new RoomCreateRequest("a", 1, 4, 100, false, null)).Ok);

            // 600 - 120 - 20 = 460, but no one match fields more than MAX_BOTS.
            Assert.Equal(ProtocolConstants.MAX_BOTS, lobby.MaxBotsForNewRoom());
        }

        // ------------------------------------------------------------------ over a real socket

        [Fact]
        public async Task EveryTicketTellsTheGameServerTheRoomAndHalfItsBotsPerSide()
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
            await using var heartbeats = new GameServerHeartbeatLoop(gameServer, registered.ServerId);

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();
            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(alpha, "bots_alpha");
            await LoginAsync(beta,  "bots_beta");

            // A total of 40: the game server fields half of it on each side.
            CreateRoomResult created = await PumpAsync(
                alpha.CreateRoomAsync(new CreateRoomRequest { Name = "forty", MapId = 1, MaxPlayers = 4, BotCount = 40 }),
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
                Assert.Equal(20, assignment.BotsPerTeam);
            }

            // And the push did not answer anything by accident: the link still works for the
            // requests that follow it.
            Assert.Equal(MasterConnectionState.Connected, gameServer.State);
        }

        /// <summary>
        /// The create form's numbers arrive with the room list, and the create is held to them:
        /// the third 100-bot room is refused with <c>ServerAtBotCapacity</c>.
        /// </summary>
        [Fact]
        public async Task TheRoomListCarriesTheHostsCapacityAndTheCreateIsHeldToIt()
        {
            await using var server = new Phase03ServerHarness();

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();
            using var gamma = new MasterClient.MasterClient();
            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);
            await gamma.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(alpha, "cap_alpha");
            await LoginAsync(beta,  "cap_beta");
            await LoginAsync(gamma, "cap_gamma");

            RoomList empty = await PumpAsync(alpha.GetRoomListAsync(), null, alpha, beta, gamma);
            Assert.NotNull(empty.Capacity);
            Assert.Equal(100, empty.Capacity!.MaxBotsForNewRoom);
            Assert.True(empty.Capacity.CanCreateRoom);
            Assert.Equal(ProtocolConstants.MAX_BOTS, empty.Capacity.MaxBotsPerMatch);
            Assert.Equal(BotCapacity.DefaultBudgetUnits, empty.Capacity.BudgetUnits);
            Assert.Equal(0, empty.Capacity.UnitsInUse);

            RoomState? pushed = null;
            alpha.OnRoomStatePush += state => pushed = state;
            CreateRoomResult first = await PumpAsync(
                alpha.CreateRoomAsync(new CreateRoomRequest { Name = "hundred", MapId = 2, MaxPlayers = 4, BotCount = 100 }),
                null, alpha, beta, gamma);
            Assert.True(first.Ok);
            Assert.True(await PumpUntilAsync(() => pushed != null, null, alpha, beta, gamma), "no room push after the create");
            Assert.Equal(100, pushed!.BotCount);
            Assert.Equal(2, pushed.MapId);
            Assert.Equal(4, pushed.MaxPlayers);

            RoomList one = await PumpAsync(beta.GetRoomListAsync(), null, alpha, beta, gamma);
            Assert.Equal(100, Assert.Single(one.Rooms).BotCount);
            Assert.Equal(1, one.Capacity!.RoomsOpen);
            Assert.Equal(100, one.Capacity.BotsInPlay);
            Assert.Equal(150, one.Capacity.UnitsInUse);
            Assert.Equal(100, one.Capacity.MaxBotsForNewRoom);

            CreateRoomResult second = await PumpAsync(
                beta.CreateRoomAsync(new CreateRoomRequest { Name = "another", MapId = 1, MaxPlayers = 4, BotCount = 100 }),
                null, alpha, beta, gamma);
            Assert.True(second.Ok);

            RoomList full = await PumpAsync(gamma.GetRoomListAsync(), null, alpha, beta, gamma);
            Assert.Equal(0, full.Capacity!.MaxBotsForNewRoom);
            Assert.False(full.Capacity.CanCreateRoom);

            CreateRoomResult third = await PumpAsync(
                gamma.CreateRoomAsync(new CreateRoomRequest { Name = "one too many", MapId = 1, MaxPlayers = 4, BotCount = 0 }),
                null, alpha, beta, gamma);
            Assert.False(third.Ok);
            Assert.Equal((int)ErrorCode.ServerAtBotCapacity, third.ErrorCode);
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

        private static async Task<bool> PumpUntilAsync(Func<bool> condition, GameServerLink? gameServer, params IMasterClient[] clients)
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
