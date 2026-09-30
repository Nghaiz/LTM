using System;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Leaving a match gives its room up, and a match this player left is one press away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The 2026-09-30 playtest: "we went out to the menu and tried to get back in, and we were
    /// blocked".</b> Leaving a match dropped the game-server link and kept the player a member of
    /// the room on the master. The browser listed that room as "In match" and refused it, and every
    /// other room answered <c>AlreadyInAnotherRoom</c>; quitting the game was the only way out, and
    /// both players took it twice. The master now remembers who played in a running match and lets
    /// exactly those people back in (<c>RoomBrowserAndTicketRefreshTests</c> covers that half).
    /// </para>
    /// </remarks>
    public sealed class MatchRejoinTests
    {
        private sealed class Harness
        {
            public readonly FakeMasterClient Master = new FakeMasterClient();
            public readonly FakeTransportClient Game = new FakeTransportClient();
            public readonly GameFlowController Flow = new GameFlowController();
            public readonly MasterSession Session;

            public Harness() => Session = new MasterSession(Master, Flow, Game, _ => 1);

            public async Task<Harness> AtRoomBrowserAsync()
            {
                Flow.Transition(GameFlowState.LoginScreen);
                await Session.LoginAsync("tester", "hunter2");
                await Session.OpenRoomBrowserAsync();
                return this;
            }

            public async Task<Harness> InMatchAsync()
            {
                await AtRoomBrowserAsync();
                await Session.JoinRoomAsync(3, null);
                Session.EnterMatch();
                Game.Accept();
                Session.OnSceneReady();
                Assert.Equal(GameFlowState.InMatch, Flow.State);
                return this;
            }
        }

        [Fact]
        public async Task LeavingAMatchGivesItsRoomUp()
        {
            Harness h = await new Harness().InMatchAsync();

            h.Session.LeaveMatch();

            Assert.Equal(GameFlowState.Lobby, h.Flow.State);
            Assert.Equal(1, h.Master.LeaveRoomCalls);
            Assert.Equal(0, h.Session.JoinedRoomId);
            Assert.Null(h.Session.Room);
        }

        [Fact]
        public async Task ADroppedMatchGivesItsRoomUp()
        {
            Harness h = await new Harness().InMatchAsync();

            h.Game.Drop(DisconnectReason.Timeout);

            Assert.Equal(GameFlowState.Lobby, h.Flow.State);
            Assert.Equal(1, h.Master.LeaveRoomCalls);
            Assert.Equal(0, h.Session.JoinedRoomId);
        }

        [Fact]
        public async Task GivingUpADialKeepsTheRoom()
        {
            // The map-not-in-build path leaves a match that never started: the player goes back to
            // the room lobby of a room they are still in, and its next start push must still work.
            Harness h = await new Harness().AtRoomBrowserAsync();
            await h.Session.JoinRoomAsync(3, null);
            h.Session.EnterMatch();

            h.Session.LeaveMatch();

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
            Assert.Equal(0, h.Master.LeaveRoomCalls);
            Assert.Equal(3, h.Session.JoinedRoomId);
        }

        [Fact]
        public async Task ALeftPlayerCanCreateTheNextRoom()
        {
            // The other half of the report: every other room answered AlreadyInAnotherRoom.
            Harness h = await new Harness().InMatchAsync();
            h.Session.LeaveMatch();

            await h.Session.OpenRoomBrowserAsync();
            Assert.True(await h.Session.CreateRoomAsync("again", 1, 4, 0, null));

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
        }

        [Fact]
        public async Task ARejoinDialsTheRunningMatchStraightAway()
        {
            Harness h = await new Harness().AtRoomBrowserAsync();

            Assert.True(await h.Session.RejoinMatchAsync(7));

            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);
            Assert.Equal(7, h.Master.LastRoomId);
            Assert.Null(h.Master.LastRoomPasswordHash);
            Assert.Equal(1, h.Game.ConnectCount);
            Assert.Equal(27015, h.Game.LastPort);
            Assert.Equal(7, h.Session.JoinedRoomId);
        }

        [Fact]
        public async Task ARejoinDialsOnceWhateverTheRoomPushesSay()
        {
            // The master pushes the room (InMatch) with its answer and again on every roster
            // change. Each would fetch a second ticket and dial a second time if the rejoin had not
            // already claimed the entry.
            Harness h = await new Harness().AtRoomBrowserAsync();
            h.Master.PushDuringNextJoin = new RoomState { RoomId = 7, State = (byte)RoomLifecycleState.InMatch };

            await h.Session.RejoinMatchAsync(7);
            h.Master.PushRoomState(7, RoomLifecycleState.InMatch);

            Assert.Equal(1, h.Master.JoinRoomCalls);
            Assert.Equal(1, h.Game.ConnectCount);
        }

        [Fact]
        public async Task ARefusedRejoinStaysInTheBrowserAndSaysWhy()
        {
            Harness h = await new Harness().AtRoomBrowserAsync();
            h.Master.NextJoin = new JoinResult { Ok = false, ErrorCode = (int)ErrorCode.MatchAlreadyStarted };

            Assert.False(await h.Session.RejoinMatchAsync(7));

            Assert.Equal(GameFlowState.RoomBrowser, h.Flow.State);
            Assert.Equal(0, h.Game.ConnectCount);
            Assert.Equal(0, h.Session.JoinedRoomId);
            Assert.NotEqual(string.Empty, h.Session.LastError);
        }
    }
}
