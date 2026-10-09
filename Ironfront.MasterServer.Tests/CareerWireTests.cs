using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
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
                    Stats = new Dictionary<string, long>
                    {
                        ["kills"] = 3, ["deaths"] = 1, ["score"] = 9, ["botKills"] = 3, ["meleeKills"] = 1,
                        ["finished"] = 1, ["won"] = 1, ["secondsPlayed"] = 400, ["hornAfterRoadkill"] = 1,
                    },
                },
            });

            Assert.True(await WaitWhilePumping(() => pushed.Count > 0, player));
            Assert.Contains("roll_call", pushed);
            Assert.Contains("victory_lap", pushed);
            Assert.DoesNotContain("taste_of_victory", pushed);

            AchievementState state = await Pump(player.GetAchievementsAsync(cts.Token), player);
            Assert.Contains(state.Unlocked, u => u.Id == "roll_call" && u.At > 0);
            Assert.Equal(3, state.Career["kills"]);
            Assert.Equal(1, state.Career["roundsWon"]);
            Assert.Equal(1, state.Earned["roll_call"]);
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

            var progress = new Dictionary<string, long> { ["prGuidePages"] = 255, ["kills"] = 999 };
            AchievementState state = await Pump(player.ClaimAchievementsAsync(new[] { "by_the_book", "grim_arithmetic" }, progress, cts.Token), player);

            Assert.Equal(new[] { "by_the_book" }, state.Unlocked.Select(u => u.Id));
            Assert.Equal(new[] { "by_the_book" }, pushed);
            Assert.Equal(255, state.Career["prGuidePages"]);
            Assert.False(state.Career.ContainsKey("kills"));
            Leaderboard board = await Pump(player.GetLeaderboardAsync(cts.Token), player);
            Assert.Empty(board.Rows);
            Assert.Null(board.You);
        }

        [Fact]
        public async Task ARoundInProgressUnlocksAtOnceAndALeaversRoundIsKept()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();

            using var gameServer = new GameServerLink();
            await gameServer.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            GameServerRegistrationResult registration = await Pump(gameServer.RegisterAsync(new GameServerRegistration
            {
                ServerSecret = Phase03ServerHarness.SharedSecret, PublicIp = "203.0.113.43", UdpPort = 27016,
                MaxPlayers = 16, MapIds = new ushort[] { 1 },
            }, cts.Token), gameServer);
            Assert.True(registration.Ok);
            await using var heartbeats = new GameServerHeartbeatLoop(gameServer, registration.ServerId);

            using var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignIn(player, "streaker");
            var pushed = new List<string>();
            player.OnAchievementsUnlocked += ids => pushed.AddRange(ids);

            Room room = server.Lobby.CreateRoom(Session(971), new RoomCreateRequest("Progress", 1, 4, 0, false, null)).Room!;
            Assert.True((await Pump(player.JoinRoomAsync(room.RoomId, null, cts.Token), player)).Ok);
            gameServer.MatchStarted(room.RoomId);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.State == RoomLifecycleState.InMatch));

            // Mid-round: a 15-kill streak is earned for good, the round is not written yet.
            gameServer.MatchProgress(room.RoomId, new[]
            {
                new MatchPlayerResult
                {
                    PlayerId = playerId, Kills = 15,
                    Stats = new Dictionary<string, long> { ["kills"] = 15, ["botKills"] = 15, ["bestStreak"] = 15 },
                },
            }, final: false);

            Assert.True(await WaitWhilePumping(() => pushed.Contains("unbroken"), player));
            AchievementState during = await Pump(player.GetAchievementsAsync(cts.Token), player);
            Assert.False(during.Career.ContainsKey("kills"));

            // The player leaves: their round so far goes into the career, unfinished.
            gameServer.MatchProgress(room.RoomId, new[]
            {
                new MatchPlayerResult
                {
                    PlayerId = playerId, Kills = 16,
                    Stats = new Dictionary<string, long> { ["kills"] = 16, ["botKills"] = 16, ["bestStreak"] = 16, ["finished"] = 0, ["secondsPlayed"] = 900 },
                },
            }, final: true);

            AchievementState after = during;
            for (int i = 0; i < 100 && !after.Career.ContainsKey("kills"); i++)
            {
                await Task.Delay(20);
                after = await Pump(player.GetAchievementsAsync(cts.Token), player);
            }
            Assert.Equal(16, after.Career["kills"]);
            Assert.False(after.Career.ContainsKey("roundsFinished"));
            Assert.Single(after.Unlocked, u => u.Id == "unbroken");
        }

        [Fact]
        public async Task AnotherPlayersProfileArrivesWithTheHiddenRuleKept()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();

            using var rival = new MasterClient.MasterClient();
            await rival.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int rivalId = await SignIn(rival, "rival");
            using var viewer = new MasterClient.MasterClient();
            await viewer.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignIn(viewer, "viewer");

            server.Database.WriteCareer(rivalId, new Dictionary<string, long>
            {
                ["matches"] = 3, ["score"] = 40, ["kills"] = 12, ["longestShotgunKillMetres"] = 41,
            });
            Assert.True(server.Database.InsertAchievement(rivalId, "roll_call", 5));
            Assert.True(server.Database.InsertAchievement(rivalId, "buckshot_sniper", 6));

            PlayerProfile profile = await Pump(viewer.GetPlayerProfileAsync(rivalId, cts.Token), viewer);
            Assert.Equal("rival", profile.Player!.Name);
            Assert.Equal(1, profile.Player.Rank);
            Assert.Equal(2, profile.Player.Achievements);
            Assert.Equal(new[] { "roll_call" }, profile.Unlocked.Select(u => u.Id));
            Assert.Equal(1, profile.Hidden);
            Assert.Equal(5, profile.Tiers.Length);
            Assert.Equal(12, profile.Career["kills"]);
            Assert.False(profile.Career.ContainsKey("longestShotgunKillMetres"));

            Leaderboard board = await Pump(viewer.GetLeaderboardAsync(cts.Token), viewer);
            LeaderboardRow row = Assert.Single(board.Rows);
            Assert.Equal(2, row.Achievements);
            Assert.Equal(AchievementTotals.Of(new[] { "roll_call", "buckshot_sniper" }).Points, row.Points);

            PlayerProfile nobody = await Pump(viewer.GetPlayerProfileAsync(987_654, cts.Token), viewer);
            Assert.Null(nobody.Player);
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
