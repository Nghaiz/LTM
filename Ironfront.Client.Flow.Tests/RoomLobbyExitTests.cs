using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The other of M3's two open manual interventions: a client that joined a room could not
    /// leave it. The transition table had no <c>RoomLobby -&gt; RoomBrowser</c> edge, so the only
    /// way back was to quit the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The master half was never actually unknown.</b> The intervention audit recorded the
    /// cost of this as "S for the client half, unknown for the master half", on the reasoning
    /// that a `ROOM_LEAVE` might have to be designed. It already exists on both ends:
    /// <c>MspMessageType.RoomLeaveRequest</c> is sent by <c>MasterClient.LeaveRoomAsync</c> and
    /// handled by <c>MspMessageDispatcher</c>. So the whole cost was the client edge.
    /// </para>
    /// <para>
    /// <b>This adds an edge the phase-03 diagram does not have</b>, deliberately, and the
    /// diagram transcription in <c>GameFlowControllerTests</c> is updated in the same change so
    /// the two stay in step -- that pair of tests exists exactly to catch a helpful edge added
    /// by hand, and a superset table passing quietly is what it prevents.
    /// </para>
    /// <para>
    /// Both assertions were observed RED against the pre-fix tree.
    /// </para>
    /// </remarks>
    public sealed class RoomLobbyExitTests
    {
        private sealed class Harness
        {
            public readonly FakeMasterClient Master = new FakeMasterClient();
            public readonly FakeTransportClient Game = new FakeTransportClient();
            public readonly GameFlowController Flow = new GameFlowController();
            public readonly MasterSession Session;

            private Harness() => Session = new MasterSession(Master, Flow, Game, Route);

            private int Route(ReadOnlySpan<byte> payload) => 1;

            public static async Task<Harness> AtRoomLobbyAsync()
            {
                var h = new Harness();
                h.Flow.Transition(GameFlowState.LoginScreen);
                await h.Session.LoginAsync("tester", "hunter2");
                await h.Session.OpenRoomBrowserAsync();
                await h.Session.JoinRoomAsync(1, null);
                return h;
            }
        }

        [Fact]
        public void TheDiagramHasAnEdgeOutOfTheRoomLobby()
        {
            Assert.True(
                GameFlowController.IsLegal(GameFlowState.RoomLobby, GameFlowState.RoomBrowser),
                "a client that joined a room cannot leave it");
        }

        [Fact]
        public async Task LeavingTheRoomTellsTheMasterAndReturnsToTheBrowser()
        {
            Harness h = await Harness.AtRoomLobbyAsync();

            Assert.True(await h.Session.LeaveRoomAsync());

            Assert.Equal(1, h.Master.LeaveRoomCalls);
            Assert.Equal(GameFlowState.RoomBrowser, h.Flow.State);
        }

        [Fact]
        public async Task LeavingClearsTheJoinSoNothingCanDialTheOldRoom()
        {
            // PendingJoin carries a signed ticket for a room this client is no longer in.
            // Leaving it behind would let EnterMatch dial a game server for a room the master
            // has already removed us from.
            Harness h = await Harness.AtRoomLobbyAsync();

            await h.Session.LeaveRoomAsync();

            Assert.False(h.Session.PendingJoin.IsValid);
            Assert.Equal(0, h.Session.JoinedRoomId);
            Assert.Equal(0, h.Session.JoinedMapId);
        }

        [Fact]
        public async Task LeavingFromAnywhereElseIsRefusedRatherThanThrowing()
        {
            // The button exists only on the room screen, but a queued click landing one frame
            // after a match start must not throw IllegalGameFlowTransition out of the UI.
            Harness h = await Harness.AtRoomLobbyAsync();
            h.Flow.Transition(GameFlowState.ConnectingGame);

            Assert.False(await h.Session.LeaveRoomAsync());

            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);
            Assert.Equal(0, h.Master.LeaveRoomCalls);
        }
    }
}
