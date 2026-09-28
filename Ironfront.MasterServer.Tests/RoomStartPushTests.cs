using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// P14 criterion 2 and 3, over a real socket: two players in a room mark themselves ready
    /// and the master's own push is what tells them the match has been called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists beside <c>RoomStartsTheMatchTests</c>.</b> Those drive
    /// <c>LobbyService</c> directly and prove the RULE. They cannot see the delivery: the rule
    /// runs on the host's housekeeping thread, the push is framed by the dispatcher and written
    /// to a TCP connection, and the client only surfaces it inside <c>Poll()</c>. A room that
    /// reaches <c>Starting</c> and never tells anybody is indistinguishable, from the player's
    /// chair, from a room that never started — and it is exactly what the first end-to-end run
    /// of this phase produced.
    /// </para>
    /// <para>
    /// <b>Two accounts and a registered game server, because the join path needs all three.</b>
    /// Creating a room joins you to it, so the creator cannot also be the joiner, and
    /// <c>JoinRoom</c> answers <c>NoGameServerAvailable</c> until a game server has registered
    /// and advertised the room's map.
    /// </para>
    /// <para>
    /// The countdown is shortened to a fraction of a second. Its LENGTH is
    /// <c>RoomStartsTheMatchTests</c>'s business; what is under test here is that its expiry
    /// reaches two clients.
    /// </para>
    /// </remarks>
    public sealed class RoomStartPushTests
    {
        private const string PasswordHash =
            "b14c00000000000000000000000000000000000000000000000000000000b14c";

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

        [Fact]
        public async Task BothPlayersAreToldTheRoomIsStartingAfterEveryoneMarksReady()
        {
            await using var server = new Phase03ServerHarness();

            // Short enough that the test costs nothing, long enough that "armed" and "expired"
            // remain two observable events rather than one.
            server.Lobby.StartCountdownMs = 200;

            using var gameServer = new GameServerLink();
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

            Assert.True(registered.Ok, "no game server registered, so the join below could never be allocated one");

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();

            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);

            await LoginAsync(alpha, "p14_alpha");
            await LoginAsync(beta,  "p14_beta");

            CreateRoomResult created = await PumpAsync(
                alpha.CreateRoomAsync(new CreateRoomRequest { Name = "p14", MapId = 1, MaxPlayers = 8 }), alpha, beta);
            Assert.True(created.Ok);

            JoinResult joined = await PumpAsync(beta.JoinRoomAsync(created.RoomId, null), alpha, beta);
            Assert.True(joined.Ok, $"beta could not join, errorCode {joined.ErrorCode}");

            // Subscribed AFTER the joins, so what these record is caused by the ready calls and
            // nothing earlier.
            bool alphaSawStarting = false;
            bool betaSawStarting  = false;
            var alphaPushes = new System.Collections.Generic.List<RoomLifecycleState>();
            var betaPushes  = new System.Collections.Generic.List<RoomLifecycleState>();

            // The full sequence is recorded, not just a flag: a failure that says "three
            // pushes arrived and all three read Waiting" names a broken DECODE, while "no pushes
            // arrived" names a broken BROADCAST. Those are different files, and the first run of
            // this test could not tell them apart.
            alpha.OnRoomStatePush += state => { alphaPushes.Add(state.Lifecycle); alphaSawStarting |= state.Lifecycle == RoomLifecycleState.Starting; };
            beta.OnRoomStatePush  += state => { betaPushes.Add(state.Lifecycle);  betaSawStarting  |= state.Lifecycle == RoomLifecycleState.Starting; };

            await PumpAsync(alpha.SetReadyAsync(true), alpha, beta);
            await PumpAsync(beta.SetReadyAsync(true),  alpha, beta);

            bool delivered = await PumpUntilAsync(() => alphaSawStarting && betaSawStarting, alpha, beta);

            // The master's own view, so a failure separates "the rule never fired" from "it
            // fired and nobody was told" without a second run.
            server.Lobby.TryGetRoomById(created.RoomId, out Lobby.Room? room);
            string master = room is null
                ? "the room is gone"
                : $"master says state={room.State}, members={room.Members.Count}, "
                  + $"ready={room.Members.FindAll(m => m.Ready).Count}, counting={room.IsCountingDown}";

            Assert.True(
                delivered,
                $"{master}; alpha pushes=[{string.Join(',', alphaPushes)}] beta pushes=[{string.Join(',', betaPushes)}]; the push did not reach both clients "
                + $"(alpha {alphaSawStarting}, beta {betaSawStarting}). MasterSession enters the match ON "
                + "THIS PUSH, so a room that starts silently leaves every player sitting in the lobby — "
                + "which is what the P14 debug button used to paper over.");
        }

        [Fact]
        public async Task UnReadyingBeforeTheCountdownExpiresLeavesTheRoomWaiting()
        {
            await using var server = new Phase03ServerHarness();

            // Long enough to un-ready inside, short enough not to stall the suite.
            server.Lobby.StartCountdownMs = 3_000;

            using var gameServer = new GameServerLink();
            await gameServer.ConnectAsync("127.0.0.1", server.Port);
            await PumpAsync(
                gameServer.RegisterAsync(new GameServerRegistration
                {
                    ServerSecret = Phase03ServerHarness.SharedSecret,
                    PublicIp     = "127.0.0.1",
                    UdpPort      = 27015,
                    MaxPlayers   = 16,
                    MapIds       = new ushort[] { 1 },
                }),
                gameServer);

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();
            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(alpha, "p14_cancel_alpha");
            await LoginAsync(beta,  "p14_cancel_beta");

            CreateRoomResult created = await PumpAsync(
                alpha.CreateRoomAsync(new CreateRoomRequest { Name = "p14c", MapId = 1, MaxPlayers = 8 }), alpha, beta);
            JoinResult joined = await PumpAsync(beta.JoinRoomAsync(created.RoomId, null), alpha, beta);
            Assert.True(joined.Ok);

            bool anyoneSawStarting = false;
            alpha.OnRoomStatePush += state => anyoneSawStarting |= state.Lifecycle == RoomLifecycleState.Starting;
            beta.OnRoomStatePush  += state => anyoneSawStarting |= state.Lifecycle == RoomLifecycleState.Starting;

            await PumpAsync(alpha.SetReadyAsync(true), alpha, beta);
            await PumpAsync(beta.SetReadyAsync(true),  alpha, beta);
            await PumpAsync(beta.SetReadyAsync(false), alpha, beta);

            // Wait out more than the countdown would have been. A cancel that only DELAYED the
            // start would pass a shorter wait and fail a player who thought they had stopped it.
            await PumpForAsync(TimeSpan.FromMilliseconds(4_500), alpha, beta);

            Assert.False(anyoneSawStarting, "the room started after a player withdrew their ready");
        }

        /// <summary>
        /// The decode itself, isolated from every clock and every socket race: one known frame
        /// in, one <see cref="RoomState"/> out.
        /// </summary>
        /// <remarks>
        /// The wire body is written by hand and matches <c>MspMessageDispatcher.BroadcastRoom</c>
        /// byte for byte — flat <c>roomId</c>, <c>members</c>, <c>state</c>. That is the shape
        /// the client used to read out of a nested <c>roomState</c> object that no field ever
        /// populated, so every push arrived default-constructed and every lifecycle read as
        /// Waiting.
        /// </remarks>
        [Fact]
        public async Task ARoomStatePushFrameDecodesItsStateAndRoster()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            using var client = new MasterClient.MasterClient();
            Task connect = client.ConnectAsync("127.0.0.1", port);
            using System.Net.Sockets.TcpClient peer = await listener.AcceptTcpClientAsync();
            await connect;

            RoomState? seen = null;
            client.OnRoomStatePush += state => seen = state;

            await WriteFrameAsync(
                peer,
                MspMessageType.RoomStatePush,
                "{\"state\":1,\"roomId\":7,\"members\":[{\"playerId\":11,\"name\":\"Một\",\"team\":1,\"ready\":true}]}");

            var elapsed = Stopwatch.StartNew();
            while (seen is null && elapsed.Elapsed < Budget)
            {
                client.Poll();
                await Task.Delay(5);
            }

            Assert.NotNull(seen);            Assert.Equal(RoomLifecycleState.Starting, seen!.Lifecycle);
            Assert.Equal(7, seen.RoomId);
            Assert.Single(seen.Members);
            Assert.Equal(11, seen.Members[0].PlayerId);
            Assert.Equal("Một", seen.Members[0].Name);
            Assert.Equal((byte)1, seen.Members[0].Team);
            Assert.True(seen.Members[0].Ready);
        }

        private static async Task WriteFrameAsync(
            System.Net.Sockets.TcpClient client, MspMessageType type, string json)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            Assert.Equal(frame.Length, MspFrame.Write(frame, type, body));
            await client.GetStream().WriteAsync(frame, 0, frame.Length);
        }

        [Fact]
        public async Task ARoomStatePushCarriesItsRosterAndAChatPushCarriesItsText()
        {
            // The sibling of the bug above, and pinned here rather than left to be re-found.
            // Both push bodies are FLAT on the wire and the client used to read them out of
            // nested objects that no field populated, so a room push arrived with no members
            // and a chat push arrived empty, from player 0, with no text. Neither had a caller
            // that checked, which is the only reason it went unnoticed.
            await using var server = new Phase03ServerHarness();

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();
            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(alpha, "p14_push_alpha");
            await LoginAsync(beta,  "p14_push_beta");

            RoomState? seen = null;
            beta.OnRoomStatePush += state => seen = state;

            ChatMessage? chat = null;
            beta.OnChat += message => chat = message;

            CreateRoomResult created = await PumpAsync(
                alpha.CreateRoomAsync(new CreateRoomRequest { Name = "p14p", MapId = 1, MaxPlayers = 8 }), alpha, beta);
            Assert.True(created.Ok);

            // No game server is registered here on purpose: this test is about what a push
            // CARRIES, and beta cannot join without an allocation. beta gets its push by being
            // the recipient of a global chat instead, and the roster arrives from alpha's own
            // ready flip inside its one-member room.
            await PumpAsync(alpha.SetReadyAsync(true), alpha, beta);
            await PumpUntilAsync(() => false, alpha, beta, TimeSpan.FromMilliseconds(300));

            await PumpAsync(alpha.SendChatAsync(0, "san sàng"), alpha, beta);
            bool arrived = await PumpUntilAsync(() => chat is not null, alpha, beta);

            Assert.True(arrived, "no chat push arrived at all");
            Assert.Equal("san sàng", chat!.Text);
            Assert.NotEqual(0, chat.FromPlayerId);
            Assert.False(string.IsNullOrEmpty(chat.FromName), "the sender's name was dropped");
        }

        /// <summary>
        /// A room-channel line reaches the room and NOBODY else. The leak this was filed for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two clients, one room, and the outsider is the assertion.</b> The lobby chat box is
        /// drawn inside the RoomLobby panel, and every line typed into it went to every player
        /// logged into the master — the Unity client sent channel 0, which
        /// <c>MspMessageDispatcher.SendChat</c> broadcasts to every connection it holds. Nothing
        /// caught it because protocol-spec.md named the <c>channel</c> field and never named its
        /// values, so both halves were self-consistent and disagreed.
        /// </para>
        /// <para>
        /// <b>The negative is checked AFTER a positive delivery, not on a timeout alone.</b>
        /// Asserting only "beta saw nothing" would pass just as happily if chat were broken
        /// outright, or if the pump never ran — the classic green that proves nothing. Alpha
        /// receiving its own line first is what establishes the message was actually sent,
        /// routed, and pushed; beta's silence then means routing, not absence.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ARoomChatLineDoesNotReachSomebodyOutsideTheRoom()
        {
            await using var server = new Phase03ServerHarness();

            using var inside  = new MasterClient.MasterClient();
            using var outside = new MasterClient.MasterClient();
            await inside.ConnectAsync("127.0.0.1", server.Port);
            await outside.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(inside,  "chan_inside");
            await LoginAsync(outside, "chan_outside");

            ChatMessage? heardInside = null;
            ChatMessage? heardOutside = null;
            inside.OnChat  += message => heardInside = message;
            outside.OnChat += message => heardOutside = message;

            CreateRoomResult created = await PumpAsync(
                inside.CreateRoomAsync(new CreateRoomRequest { Name = "chan", MapId = 1, MaxPlayers = 8 }),
                inside, outside);
            Assert.True(created.Ok);

            await PumpAsync(inside.SendChatAsync(MspChatChannel.Room, "only us"), inside, outside);
            bool arrived = await PumpUntilAsync(() => heardInside is not null, inside, outside);

            Assert.True(arrived, "the room's own member never received the line");
            Assert.Equal("only us", heardInside!.Text);
            Assert.Equal(MspChatChannel.Room, heardInside.Channel);

            // Kept pumping past the delivery, so a late push would still be seen.
            await PumpUntilAsync(() => false, inside, outside, TimeSpan.FromMilliseconds(300));
            Assert.Null(heardOutside);
        }

        /// <summary>
        /// The global channel still reaches everybody. The other half of the same routing.
        /// </summary>
        /// <remarks>
        /// Without this, narrowing the room channel could be "fixed" by breaking delivery
        /// altogether and the test above would not notice.
        /// </remarks>
        [Fact]
        public async Task AGlobalChatLineStillReachesEverybody()
        {
            await using var server = new Phase03ServerHarness();

            using var alpha = new MasterClient.MasterClient();
            using var beta  = new MasterClient.MasterClient();
            await alpha.ConnectAsync("127.0.0.1", server.Port);
            await beta.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(alpha, "chan_global_a");
            await LoginAsync(beta,  "chan_global_b");

            ChatMessage? heard = null;
            beta.OnChat += message => heard = message;

            await PumpAsync(alpha.SendChatAsync(MspChatChannel.Global, "everyone"), alpha, beta);

            Assert.True(await PumpUntilAsync(() => heard is not null, alpha, beta));
            Assert.Equal("everyone", heard!.Text);
        }

        /// <summary>
        /// A room line from a player in no room is refused, not dropped in silence.
        /// </summary>
        /// <remarks>
        /// The old code fell off the end of <c>SendChat</c>, so the sender watched a line they
        /// had typed simply never appear — which from their chair is identical to a message
        /// delivered to a room where nobody answered.
        /// </remarks>
        [Fact]
        public async Task ARoomLineWithNoRoomIsRefusedOutLoud()
        {
            await using var server = new Phase03ServerHarness();

            using var lonely = new MasterClient.MasterClient();
            await lonely.ConnectAsync("127.0.0.1", server.Port);
            await LoginAsync(lonely, "chan_roomless");

            int code = 0;
            lonely.OnError += (errorCode, _) => code = errorCode;

            await PumpAsync(lonely.SendChatAsync(MspChatChannel.Room, "anyone there"), lonely);
            await PumpUntilAsync(() => code != 0, lonely);

            Assert.Equal((int)ErrorCode.NotInARoom, code);
        }

        private static async Task LoginAsync(IMasterClient client, string username)
        {
            try
            {
                await PumpAsync(client.RegisterAsync(username, PasswordHash, username), client);
            }
            catch (Exception)
            {
                // Already registered from an earlier run against a surviving temp database.
            }

            LoginResult login = await PumpAsync(client.LoginAsync(username, PasswordHash), client);
            Assert.True(login.Ok, $"'{username}' could not log in, errorCode {login.ErrorCode}");
        }

        private static async Task<T> PumpAsync<T>(Task<T> task, params IMasterClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < Budget)
            {
                for (int i = 0; i < clients.Length; i++) clients[i].Poll();
                await Task.Delay(5);
            }

            for (int i = 0; i < clients.Length; i++) clients[i].Poll();
            Assert.True(task.IsCompleted, "a master request never completed inside the budget");
            return await task;
        }

        private static async Task PumpAsync(Task task, params IMasterClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < Budget)
            {
                for (int i = 0; i < clients.Length; i++) clients[i].Poll();
                await Task.Delay(5);
            }

            for (int i = 0; i < clients.Length; i++) clients[i].Poll();
            await task;
        }

        private static async Task<T> PumpAsync<T>(Task<T> task, GameServerLink gameServer)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < Budget)
            {
                gameServer.Poll();
                await Task.Delay(5);
            }

            gameServer.Poll();
            return await task;
        }

        private static Task<bool> PumpUntilAsync(Func<bool> condition, params IMasterClient[] clients)
            => PumpUntilAsync(condition, clients, Budget);

        private static Task<bool> PumpUntilAsync(
            Func<bool> condition, IMasterClient a, IMasterClient b, TimeSpan budget)
            => PumpUntilAsync(condition, new[] { a, b }, budget);

        private static async Task<bool> PumpUntilAsync(
            Func<bool> condition, IMasterClient[] clients, TimeSpan budget)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < budget)
            {
                for (int i = 0; i < clients.Length; i++) clients[i].Poll();
                if (condition()) return true;
                await Task.Delay(5);
            }

            for (int i = 0; i < clients.Length; i++) clients[i].Poll();
            return condition();
        }

        private static async Task PumpForAsync(TimeSpan duration, params IMasterClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < duration)
            {
                for (int i = 0; i < clients.Length; i++) clients[i].Poll();
                await Task.Delay(5);
            }
        }
    }
}
