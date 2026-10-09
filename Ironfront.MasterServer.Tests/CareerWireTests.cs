using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The career end to end through the real dispatcher (owner's list of 2026-10-09, item 4): a
    /// game server's round report becomes a career, an unlock push, a ranking row and an
    /// achievement list; a client's practice claim becomes an unlock and nothing else does.
    /// </summary>
    [Collection(SocketTestCollection.Name)]
    public sealed class CareerWireTests
    {
        private const string PasswordHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task ARoundReportUnlocksRanksAndIsPushedToThePlayer()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();

            using var gameServer = new GameServerLink();
            await gameServer.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            GameServerRegistrationResult registration = await Pump(gameServer.RegisterAsync(new GameServerRegistration
            {
                ServerSecret = Phase03ServerHarness.SharedSecret, PublicIp = "203.0.113.42", UdpPort = 27015,
                MaxPlayers = 16, MapIds = new ushort[] { 1 },
            }, cts.Token), gameServer);
            Assert.True(registration.Ok);
            await using var heartbeats = new GameServerHeartbeatLoop(gameServer, registration.ServerId);

            using var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignIn(player, "careerist");
            var pushed = new List<string>();
            player.OnAchievementsUnlocked += ids => pushed.AddRange(ids);

            Room room = server.Lobby.CreateRoom(Session(970), new RoomCreateRequest("Career", 1, 4, 0, false, null)).Room!;
            Assert.True((await Pump(player.JoinRoomAsync(room.RoomId, null, cts.Token), player)).Ok);
            gameServer.MatchStarted(room.RoomId);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.State == RoomLifecycleState.InMatch));

            gameServer.MatchEnded(room.RoomId, new[]
            {
                new MatchPlayerResult
                {
                    PlayerId = playerId, Kills = 3, Deaths = 1, Score = 9,
                    Stats = new Dictionary<string, long> { ["kills"] = 3, ["deaths"] = 1, ["score"] = 9, ["meleeKills"] = 1, ["bestMultiKill"] = 2 },
                },
            });

            Assert.True(await WaitWhilePumping(() => pushed.Count > 0, player));
            Assert.Contains("boots_on_the_ground", pushed);
            Assert.Contains("first_blood", pushed);
            Assert.Contains("up_close", pushed);
            Assert.Contains("two_for_one", pushed);

            AchievementState state = await Pump(player.GetAchievementsAsync(cts.Token), player);
            Assert.Contains(state.Unlocked, u => u.Id == "up_close" && u.At > 0);
            Assert.Equal(3, state.Career["kills"]);
            Assert.Equal(1, state.Earned["first_blood"]);
            Assert.Equal(1, state.Players);

            Leaderboard board = await Pump(player.GetLeaderboardAsync(cts.Token), player);
            LeaderboardRow row = Assert.Single(board.Rows);
            Assert.Equal(1, row.Rank);
            Assert.Equal("careerist", row.Name);
            Assert.Equal(9, row.Score);
            Assert.Equal(1, board.You!.Rank);
        }

        [Fact]
        public async Task OnlyPracticeAchievementsAreAcceptedFromTheClient()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignIn(player, "trainee");
            var pushed = new List<string>();
            player.OnAchievementsUnlocked += ids => pushed.AddRange(ids);

            AchievementState state = await Pump(player.ClaimAchievementsAsync(new[] { "basic_training", "war_machine" }, cts.Token), player);

            Assert.Equal(new[] { "basic_training" }, state.Unlocked.Select(u => u.Id));
            Assert.Equal(new[] { "basic_training" }, pushed);
            Leaderboard board = await Pump(player.GetLeaderboardAsync(cts.Token), player);
            Assert.Empty(board.Rows);
            Assert.Null(board.You);
        }

        private static Auth.Session Session(int playerId) => new Auth.Session
        {
            Token = Guid.NewGuid().ToString("N"), PlayerId = playerId, DisplayName = "Host", Ip = 1, ExpiresAt = long.MaxValue,
        };

        private static async Task<int> SignIn(MasterClient.MasterClient client, string username)
        {
            Assert.True((await Pump(client.RegisterAsync(username, PasswordHash, username), client)).Ok);
            LoginResult login = await Pump(client.LoginAsync(username, PasswordHash), client);
            Assert.True(login.Ok);
            return login.PlayerId;
        }

        private static async Task<bool> WaitWhilePumping(Func<bool> condition, MasterClient.MasterClient client)
        {
            for (int i = 0; i < 400 && !condition(); i++)
            {
                client.Poll();
                await Task.Delay(10);
            }
            return condition();
        }

        private static async Task<T> Pump<T>(Task<T> task, MasterClient.MasterClient client)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Delay(5);
            }
            client.Poll();
            return await task;
        }

        private static async Task<T> Pump<T>(Task<T> task, GameServerLink gameServer)
        {
            while (!task.IsCompleted)
            {
                gameServer.Poll();
                await Task.Delay(5);
            }
            gameServer.Poll();
            return await task;
        }
    }
}
