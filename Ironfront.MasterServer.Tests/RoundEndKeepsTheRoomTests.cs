using System;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A round that ends on the game server does not end the room: the room stays in its match
    /// and keeps its game server until its players leave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game server plays the next round with the same players still connected (the match
    /// state machine resets after its post-match seconds). The master used to answer
    /// <c>GS_MATCH_ENDED</c> by putting the room back to <c>Waiting</c> and releasing the server,
    /// so the next room on the same map was allocated a server still full of the first room's
    /// players, and its tickets were adopted into their round; and the room itself was listed as
    /// open while none of its members could ever ready up for it again.
    /// </para>
    /// <para>
    /// Driven with a real <c>GameServerLink</c>, because the master only accepts a match report
    /// from the connection that holds the room.
    /// </para>
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public sealed class RoundEndKeepsTheRoomTests
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task ARoundThatEndsKeepsItsRoomInTheMatchAndItsServerBusy()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();

            using var gameServer = new GameServerLink();
            await gameServer.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            GameServerRegistrationResult registration = await PumpAsync(
                gameServer.RegisterAsync(new GameServerRegistration
                {
                    ServerSecret = Phase03ServerHarness.SharedSecret,
                    PublicIp = "203.0.113.41",
                    UdpPort = 27015,
                    MaxPlayers = 16,
                    MapIds = new ushort[] { 1 },
                }, cts.Token),
                gameServer);
            Assert.True(registration.Ok);
            ushort serverId = registration.ServerId;

            using var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignInAsync(player, "rounds", "Rounds");

            Room room = server.Lobby.CreateRoom(Session(960, "Host"),
                new RoomCreateRequest("Rounds", 1, 4, 0, false, null)).Room!;
            JoinResult joined = await PumpAsync(player.JoinRoomAsync(room.RoomId, null, cts.Token), player);
            Assert.True(joined.Ok);
            Assert.Equal(serverId, room.AssignedGameServerId);

            gameServer.MatchStarted(room.RoomId);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.State == RoomLifecycleState.InMatch));

            gameServer.MatchEnded(room.RoomId, new[]
            {
                new MatchPlayerResult { PlayerId = playerId, Kills = 4, Deaths = 2, Score = 400 },
            });

            // The report is stored...
            Assert.True(await MasterHostHarness.WaitUntilAsync(
                () => server.Database.FindMatchResults(room.RoomId).Count == 1));

            // ...and the room is still the server's, in its match, with everybody still on it.
            Assert.Equal(RoomLifecycleState.InMatch, room.State);
            Assert.Equal(serverId, room.AssignedGameServerId);
            Assert.True(server.GameServers.TryGet(serverId, out GameServerRecord? record) && record != null);
            Assert.Equal(room.RoomId, record!.AssignedRoomId);
            Assert.True(LobbyService.CanRejoin(playerId, room));

            // The next round starting is reported against the same room and accepted.
            gameServer.MatchStarted(room.RoomId);
            await Task.Delay(100);
            Assert.Equal(RoomLifecycleState.InMatch, room.State);

            // A second room on the only server for that map is refused rather than dropped into
            // the round the first room is still playing.
            using var other = new MasterClient.MasterClient();
            await other.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignInAsync(other, "latecomer", "Latecomer");
            Room second = server.Lobby.CreateRoom(Session(961, "Other host"),
                new RoomCreateRequest("Next", 1, 4, 0, false, null)).Room!;
            JoinResult refused = await PumpAsync(other.JoinRoomAsync(second.RoomId, null, cts.Token), other);

            Assert.False(refused.Ok);
            Assert.Equal((int)ErrorCode.NoGameServerAvailable, refused.ErrorCode);
        }

        // ------------------------------------------------------------------ helpers

        private static Auth.Session Session(int playerId, string name) => new Auth.Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue,
        };

        private static async Task<int> SignInAsync(MasterClient.MasterClient client, string username, string displayName)
        {
            RegisterResult registered = await PumpAsync(client.RegisterAsync(username, PasswordHash, displayName), client);
            Assert.True(registered.Ok);

            LoginResult login = await PumpAsync(client.LoginAsync(username, PasswordHash), client);
            Assert.True(login.Ok);
            return login.PlayerId;
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

        private static async Task<T> PumpAsync<T>(Task<T> task, GameServerLink gameServer)
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
