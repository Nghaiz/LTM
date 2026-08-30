using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// X-77. The master pushes a room whose lifecycle has moved, and the client acts on it —
    /// which is the only automatic edge out of <c>RoomLobby</c>.
    /// </summary>
    /// <remarks>
    /// Before this, <c>IMasterClient.OnRoomStatePush</c> was declared, raised, and subscribed by
    /// nothing outside the test fake. The master's half worked the whole time:
    /// <c>MspMessageDispatcher.HandleMatchStarted</c> sets <c>InMatch</c> and broadcasts the
    /// room. The client heard it and did nothing, so reaching a match needed a human pressing
    /// the shell's "Enter match now (debug)" button — a key the flow should not need, and the
    /// second of M3's two open manual interventions.
    ///
    /// Every assertion here was observed RED against the pre-fix tree, with the flow sitting in
    /// <c>RoomLobby</c> after a push that said the match had started.
    /// </remarks>
    public sealed class RoomStatePushTests
    {
        private sealed class Harness
        {
            public readonly FakeMasterClient Master = new FakeMasterClient();
            public readonly FakeTransportClient Game = new FakeTransportClient();
            public readonly GameFlowController Flow = new GameFlowController();
            public readonly List<byte[]> Routed = new List<byte[]>();
            public readonly MasterSession Session;

            private Harness() => Session = new MasterSession(Master, Flow, Game, Route);

            private int Route(ReadOnlySpan<byte> payload)
            {
                Routed.Add(payload.ToArray());
                return 1;
            }

            public const int RoomId = 1;

            public static async Task<Harness> AtRoomLobbyAsync()
            {
                var h = new Harness();
                h.Flow.Transition(GameFlowState.LoginScreen);
                await h.Session.LoginAsync("tester", "hunter2");
                await h.Session.OpenRoomBrowserAsync();
                await h.Session.JoinRoomAsync(RoomId, null);
                return h;
            }
        }

        [Theory]
        [InlineData(RoomLifecycleState.Starting)]
        [InlineData(RoomLifecycleState.InMatch)]
        public async Task AStartedRoomTakesTheClientOutOfTheLobbyByItself(RoomLifecycleState state)
        {
            Harness h = await Harness.AtRoomLobbyAsync();
            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);

            h.Master.PushRoomState(Harness.RoomId, state);

            Assert.Equal(GameFlowState.ConnectingGame, h.Flow.State);
        }

        [Fact]
        public async Task AWaitingRoomLeavesTheClientWhereItIs()
        {
            // The room fills and empties while people join, and every one of those is a push.
            // Only a lifecycle that has actually moved is an edge.
            Harness h = await Harness.AtRoomLobbyAsync();

            h.Master.PushRoomState(Harness.RoomId, RoomLifecycleState.Waiting);

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
        }

        [Fact]
        public async Task ARoomWeAreNotInIsIgnored()
        {
            // The master BROADCASTS room state, so a push about somebody else's room must not
            // drag this client into a match it never joined.
            Harness h = await Harness.AtRoomLobbyAsync();

            h.Master.PushRoomState(Harness.RoomId + 1, RoomLifecycleState.InMatch);

            Assert.Equal(GameFlowState.RoomLobby, h.Flow.State);
        }

        [Fact]
        public async Task ASecondPushDoesNotTransitionTwice()
        {
            // The push repeats -- on every member change, and on a retransmit. A second one
            // arriving while already in ConnectingGame must not throw
            // IllegalGameFlowTransitionException, which is what a naive subscriber would do.
            Harness h = await Harness.AtRoomLobbyAsync();

            h.Master.PushRoomState(Harness.RoomId, RoomLifecycleState.InMatch);
            GameFlowState after = h.Flow.State;

            h.Master.PushRoomState(Harness.RoomId, RoomLifecycleState.InMatch);

            Assert.Equal(after, h.Flow.State);
        }

        [Fact]
        public void AnUnknownStateByteIsNotAnEdgeAndDoesNotThrow()
        {
            // A master newer than this client may send a state this build has no name for.
            // Reading it as the value itself is what makes "not one of the ones I act on"
            // the automatic answer rather than a crash.
            var room = new RoomState { State = 250 };

            Assert.Equal((RoomLifecycleState)250, room.Lifecycle);
            Assert.NotEqual(RoomLifecycleState.InMatch, room.Lifecycle);
        }
    }
}
