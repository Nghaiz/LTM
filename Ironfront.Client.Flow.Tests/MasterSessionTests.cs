using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The online flow: login, room list, join, and the TCP-to-UDP junction. phase-03 tasks 2
    /// and 3.
    /// </summary>
    /// <remarks>
    /// What is graded here is every decision the flow makes and none of the transport it makes
    /// them over. phase-03 criterion 1 — launch, log in, pick a room, play, back to the lobby —
    /// needs a master, a game server and a client at once, and stays a video criterion.
    /// </remarks>
    public sealed class MasterSessionTests
    {
        private sealed class Harness
        {
            public readonly FakeMasterClient Master = new FakeMasterClient();
            public readonly FakeTransportClient Game = new FakeTransportClient();
            public readonly GameFlowController Flow = new GameFlowController();
            public readonly List<byte[]> Routed = new List<byte[]>();
            public readonly MasterSession Session;

            public Harness()
            {
                Session = new MasterSession(Master, Flow, Game, Route);
            }

            private int Route(ReadOnlySpan<byte> payload)
            {
                Routed.Add(payload.ToArray());
                return 1;
            }

            /// <summary>Walks to the login screen, which is where every flow test starts.</summary>
            public Harness AtLoginScreen()
            {
                Flow.Transition(GameFlowState.LoginScreen);
                return this;
            }

            /// <summary>Logs in and opens the browser, leaving the flow at RoomBrowser.</summary>
            public async Task<Harness> AtRoomBrowserAsync()
            {
                AtLoginScreen();
                await Session.LoginAsync("tester", "hunter2");
                await Session.OpenRoomBrowserAsync();
                return this;
            }

            /// <summary>Joins a room, leaving the flow at RoomLobby with a valid PendingJoin.</summary>
            public async Task<Harness> AtRoomLobbyAsync()
            {
                await AtRoomBrowserAsync();
                await Session.JoinRoomAsync(1, null);
                return this;
            }
        }

        // --------------------------------------------------------- login

        [Fact]
        public async Task LoginWalksToTheLobbyAndKeepsTheSession()
        {
            var h = new Harness().AtLoginScreen();

            Assert.True(await h.Session.LoginAsync("tester", "hunter2"));

            Assert.Equal(GameFlowState.Lobby, h.Flow.State);
            Assert.Equal("token", h.Session.SessionToken);
            Assert.Equal(42, h.Session.PlayerId);
            Assert.Equal("Tester", h.Session.DisplayName);
            Assert.True(h.Session.IsLoggedIn);
            Assert.Equal(string.Empty, h.Session.LastError);
        }

        [Fact]
        public async Task ThePlaintextPasswordNeverLeavesTheClient()
        {
            // phase-03 trap 2. The hash is checked against an independent computation rather
            // than against PasswordHasher, so a change to either is a failure here.
            var h = new Harness().AtLoginScreen();

            await h.Session.LoginAsync("Tester", "hunter2");

            Assert.NotEqual("hunter2", h.Master.LastPasswordHash);
            Assert.Equal(64, h.Master.LastPasswordHash!.Length);
            Assert.Equal(
                Sha256Hex("hunter2" + "tester"),   // the username is lowercased first
                h.Master.LastPasswordHash);
        }

        [Fact]
        public async Task AWrongPasswordGoesBackToTheLoginScreenWithSomethingReadable()
        {
            // phase-03 criterion 7.
            var h = new Harness().AtLoginScreen();
            h.Master.NextLogin = new LoginResult(false, (int)ErrorCode.WrongCredentials, string.Empty, 0, string.Empty);

            string? reported = null;
            h.Session.OnError += message => reported = message;

            Assert.False(await h.Session.LoginAsync("tester", "wrong"));

            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.False(h.Session.IsLoggedIn);
            Assert.Equal("Wrong username or password.", h.Session.LastError);
            Assert.Equal(h.Session.LastError, reported);
        }

        [Fact]
        public async Task AnErrorPushDuringLoginIsDescribedTheSameWay()
        {
            // The master reports failures two ways: a response with ok=false, and an ErrorPush
            // that surfaces as MasterServerException. Both must land in the same place.
            var h = new Harness().AtLoginScreen();
            h.Master.ThrowOnNextCall = new MasterServerException((int)ErrorCode.RateLimited, "slow down");

            Assert.False(await h.Session.LoginAsync("tester", "hunter2"));

            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.Equal("Too many attempts. Wait a few seconds and try again.", h.Session.LastError);
        }

        [Fact]
        public async Task TheMasterLinkDyingMidLoginIsNotACrash()
        {
            var h = new Harness().AtLoginScreen();
            h.Master.ThrowOnNextCall = new IOException("connection reset");

            Assert.False(await h.Session.LoginAsync("tester", "hunter2"));

            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.Equal("Lost the connection to the master server.", h.Session.LastError);
        }

        // --------------------------------------------------------- rooms

        [Fact]
        public async Task OpeningTheBrowserFetchesTheList()
        {
            var h = new Harness().AtLoginScreen();
            await h.Session.LoginAsync("tester", "hunter2");
            h.Master.NextRooms = new[] { new RoomInfo { RoomId = 3, Name = "de_dust" } };

            Assert.True(await h.Session.OpenRoomBrowserAsync());

            Assert.Equal(GameFlowState.RoomBrowser, h.Flow.State);
            Assert.Single(h.Session.Rooms);
            Assert.Equal("de_dust", h.Session.Rooms[0].Name);
        }

        [Fact]
        public async Task AFailedFetchLeavesThePlayerOnTheBrowserToRetry()
        {
            // Bouncing back to the lobby would take away the screen the error is drawn on.
            var h = new Harness().AtLoginScreen();
            await h.Session.LoginAsync("tester", "hunter2");
            h.Master.ThrowOnNextCall = new MasterServerException((int)ErrorCode.InternalServerError, "boom");

            Assert.False(await h.Session.OpenRoomBrowserAsync());

            Assert.Equal(GameFlowState.RoomBrowser, h.Flow.State);
            Assert.Equal("The master server hit an internal error. Try again.", h.Session.LastError);
        }

        [Fact]
        public async Task AJoinBecomesAWellFormedPendingJoin()
        {
            Harness h = await new Harness().AtRoomBrowserAsync();

            Assert.True(await h.Session.JoinRoomAsync(7, null));

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
            Assert.Equal(7, h.Master.LastRoomId);
            Assert.True(h.Session.PendingJoin.IsValid);
            Assert.Equal("203.0.113.7", h.Session.PendingJoin.Ip);
            Assert.Equal(27015, h.Session.PendingJoin.Port);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, h.Session.PendingJoin.Ticket);
        }

        [Fact]
        public async Task ARoomPasswordIsHashedUnsaltedSoJoinMatchesCreate()
        {
            // The master bcrypt-verifies this against what the room's creator sent, so any salt
            // the two sides cannot both compute makes a correct password fail.
            Harness h = await new Harness().AtRoomBrowserAsync();

            await h.Session.JoinRoomAsync(7, "letmein");

            Assert.Equal(Sha256Hex("letmein"), h.Master.LastRoomPasswordHash);
        }

        [Fact]
        public async Task NoRoomPasswordSendsNoneRatherThanTheHashOfNothing()
        {
            Harness h = await new Harness().AtRoomBrowserAsync();

            await h.Session.JoinRoomAsync(7, null);

            Assert.Null(h.Master.LastRoomPasswordHash);
        }

        [Fact]
        public async Task AFullRoomGoesBackToTheBrowser()
        {
            Harness h = await new Harness().AtRoomBrowserAsync();
            h.Master.NextJoin = new JoinResult { Ok = false, ErrorCode = (int)ErrorCode.RoomFull };

            Assert.False(await h.Session.JoinRoomAsync(7, null));

            Assert.Equal(GameFlowState.RoomBrowser, h.Flow.State);
            Assert.Equal("That room is full.", h.Session.LastError);
            Assert.False(h.Session.PendingJoin.IsValid);
        }

        [Fact]
        public async Task AnOkJoinNamingNowhereIsReportedAsAJoinFailure()
        {
            // Carrying it forward would surface as a UDP connect timeout ten seconds later,
            // blaming the game server for something the master did.
            Harness h = await new Harness().AtRoomBrowserAsync();
            h.Master.NextJoin = new JoinResult { Ok = true, GameServerIp = string.Empty, GameServerPort = 0 };

            Assert.False(await h.Session.JoinRoomAsync(7, null));

            Assert.Equal(GameFlowState.RoomBrowser, h.Flow.State);
            Assert.False(h.Session.PendingJoin.IsValid);
            Assert.Contains("did not name a game server", h.Session.LastError);
        }

        // --------------------------------------------------------- the junction

        [Fact]
        public async Task EnteringAMatchDialsTheAddressAndTicketTheMasterGave()
        {
            Harness h = await new Harness().AtRoomLobbyAsync();

            Assert.True(h.Session.EnterMatch());

            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);
            Assert.Equal(1, h.Game.ConnectCount);
            Assert.Equal("203.0.113.7", h.Game.LastHost);
            Assert.Equal(27015, h.Game.LastPort);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, h.Game.LastTicket);
            Assert.True(h.Session.Inbound.IsHolding);
        }

        [Fact]
        public async Task TheConnectTimeoutFiresAndGoesBackToTheRoomLobby()
        {
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.ConnectTimeoutSeconds = 10f;
            h.Session.EnterMatch();

            string? failure = null;
            h.Session.OnGameServerFailed += message => failure = message;

            for (int frame = 0; frame < 9; frame++) h.Session.Tick(1f);
            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);   // 9 s: still waiting

            h.Session.Tick(1f);

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
            Assert.Equal(1, h.Game.DisconnectCount);
            Assert.False(h.Session.Inbound.IsHolding);
            Assert.NotNull(failure);
            Assert.Contains("did not answer", failure!);
        }

        [Fact]
        public async Task TheTimeoutSitsWellInsideTheTicketsLifetime()
        {
            // The ticket is valid for 60 s. Waiting it out would make the retry fail too, for a
            // different reason and with a worse error.
            Assert.True(MasterSession.DefaultConnectTimeoutSeconds < 60f);
            Assert.Equal(10f, MasterSession.DefaultConnectTimeoutSeconds);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task ARefusedConnectionGoesBackToTheRoomLobbyImmediately()
        {
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();

            h.Game.Drop(DisconnectReason.InvalidTicket);

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
            Assert.Contains("InvalidTicket", h.Session.LastError);
            Assert.False(h.Session.Inbound.IsHolding);
        }

        [Fact]
        public async Task AcceptingDoesNotEnterTheMatchUntilTheSceneIsUp()
        {
            // phase-03 task 3 puts the InMatch transition in the scene load's completion
            // callback, so the HUD cannot appear over a half-loaded map.
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();

            ConnectResult? accepted = null;
            h.Session.OnGameServerConnected += r => accepted = r;

            h.Game.Accept(connectionId: 5, serverTick: 900);

            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);
            Assert.True(accepted.HasValue);
            Assert.Equal(5, accepted!.Value.ConnectionId);

            h.Session.OnSceneReady();

            Assert.Equal(GameFlowState.InMatch, h.Flow.State);
        }

        [Fact]
        public async Task EverythingThatArrivesDuringTheSceneLoadIsReplayedInOrder()
        {
            // phase-03 trap 3. Replayed rather than filtered: the snapshots are delta-encoded
            // against baselines the client must hold, so dropping the middle breaks the chain.
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();
            h.Game.Accept();

            for (byte i = 1; i <= 5; i++)
                Assert.True(h.Session.HoldIfLoading(new byte[] { i }));

            Assert.Empty(h.Routed);

            int replayed = h.Session.OnSceneReady();

            Assert.Equal(5, replayed);
            Assert.Equal(5, h.Routed.Count);
            for (int i = 0; i < 5; i++) Assert.Equal((byte)(i + 1), h.Routed[i][0]);
        }

        [Fact]
        public async Task OnceTheSceneIsUpPayloadsGoStraightThrough()
        {
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();
            h.Game.Accept();
            h.Session.OnSceneReady();

            Assert.False(h.Session.HoldIfLoading(new byte[] { 9 }));
            Assert.Empty(h.Routed);   // the caller routes it; the session only declines to hold
        }

        [Fact]
        public async Task ATimedOutJoinThrowsAwayWhatItBuffered()
        {
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();
            h.Session.HoldIfLoading(new byte[] { 1 });

            h.Session.Tick(MasterSession.DefaultConnectTimeoutSeconds);

            Assert.Equal(0, h.Session.Inbound.Count);
            Assert.Empty(h.Routed);
        }

        // --------------------------------------------------------- in match

        [Fact]
        public async Task DroppingMidMatchReturnsToTheLobbyWithAMessage()
        {
            // phase-03 criterion 6.
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();
            h.Game.Accept();
            h.Session.OnSceneReady();

            h.Game.Drop(DisconnectReason.Timeout);

            Assert.Equal(GameFlowState.Lobby, h.Flow.State);
            Assert.Contains("Disconnected from the game server", h.Session.LastError);
        }

        [Fact]
        public async Task LeavingAMatchKeepsTheMasterLinkUp()
        {
            // phase-03 task 6: closing the TCP link here would log the player out every time a
            // match ended.
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();
            h.Game.Accept();
            h.Session.OnSceneReady();

            h.Session.LeaveMatch();

            Assert.Equal(GameFlowState.Lobby, h.Flow.State);
            Assert.Equal(1, h.Game.DisconnectCount);
            Assert.True(h.Session.IsLoggedIn);
            Assert.Equal("token", h.Session.SessionToken);
            Assert.False(h.Session.PendingJoin.IsValid);
        }

        [Fact]
        public async Task ASecondMatchCanBeStartedAfterTheFirst()
        {
            // phase-03 criterion 5, as far as the session can prove it.
            Harness h = await new Harness().AtRoomLobbyAsync();
            h.Session.EnterMatch();
            h.Game.Accept();
            h.Session.OnSceneReady();
            h.Session.LeaveMatch();

            await h.Session.OpenRoomBrowserAsync();
            Assert.True(await h.Session.JoinRoomAsync(2, null));
            Assert.True(h.Session.EnterMatch());

            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);
            Assert.Equal(2, h.Game.ConnectCount);
        }

        // --------------------------------------------------------- direct connect

        [Fact]
        public void DirectConnectDialsWithNoTicketAndLeavesTheFlowAlone()
        {
            // phase-03 UI item 14, and its stated contingency for the master not being ready.
            // The diagram has no edge into ConnectingGame that does not come from RoomLobby, so
            // this path reports through events instead of inventing one.
            var h = new Harness();
            h.Flow.Transition(GameFlowState.LoginScreen);

            h.Session.ConnectDirect("10.0.0.5", 27015);

            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.Equal("10.0.0.5", h.Game.LastHost);
            Assert.Empty(h.Game.LastTicket);
            Assert.True(h.Session.Inbound.IsHolding);
        }

        [Fact]
        public void ADirectConnectTimeoutReportsWithoutMovingTheFlow()
        {
            var h = new Harness();
            h.Flow.Transition(GameFlowState.LoginScreen);
            h.Session.ConnectDirect("10.0.0.5", 27015);

            string? failure = null;
            h.Session.OnGameServerFailed += message => failure = message;

            h.Session.Tick(MasterSession.DefaultConnectTimeoutSeconds);

            Assert.Equal(GameFlowState.LoginScreen, h.Flow.State);
            Assert.NotNull(failure);
        }

        [Fact]
        public void EnteringAMatchWithNothingPendingIsRefusedRatherThanDialled()
        {
            var h = new Harness();

            Assert.False(h.Session.EnterMatch());

            Assert.Equal(0, h.Game.ConnectCount);
            Assert.Equal("There is no room to join.", h.Session.LastError);
        }

        // --------------------------------------------------------- threading

        [Fact]
        public void TickPumpsTheMasterLink()
        {
            // phase-03 trap 1, settled by reading rather than by asking: MasterClient runs every
            // response and every push from Poll(), so they fire on the thread that called it.
            // Tick() is that call, which is why there is no ConcurrentQueue marshaller here.
            var h = new Harness();

            h.Session.Tick(0.016f);
            h.Session.Tick(0.016f);
            h.Session.Tick(0.016f);

            Assert.Equal(3, h.Master.PollCount);
        }

        [Fact]
        public void DisposeUnsubscribesFromTheTransport()
        {
            var h = new Harness();
            h.Session.Dispose();

            h.Game.Drop(DisconnectReason.Timeout);   // must not touch the flow any more

            Assert.Equal(GameFlowState.Booting, h.Flow.State);
            Assert.Equal(string.Empty, h.Session.LastError);
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
