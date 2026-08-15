using System;
using System.Collections.Generic;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// phase-03 acceptance criterion 9 — "invalid state transitions throw", verified by unit
    /// test rather than by playing.
    /// </summary>
    /// <remarks>
    /// The edge list below is transcribed from the <c>stateDiagram-v2</c> block under phase-03
    /// task 1 and is the specification this whole file grades against. Nothing here is derived
    /// from <see cref="GameFlowController"/> itself — if it were, the tests would only prove
    /// the table agrees with itself, which is exactly the failure conventions.md § 7 separates
    /// implementers from verifiers to avoid.
    /// </remarks>
    public sealed class GameFlowControllerTests
    {
        /// <summary>Every edge in the phase-03 diagram, transcribed by hand.</summary>
        private static readonly (GameFlowState From, GameFlowState To)[] DiagramEdges =
        {
            (GameFlowState.Booting,        GameFlowState.LoginScreen),
            (GameFlowState.LoginScreen,    GameFlowState.Authenticating),
            (GameFlowState.Authenticating, GameFlowState.LoginScreen),     // failed (show error)
            (GameFlowState.Authenticating, GameFlowState.Lobby),           // LOGIN_RES ok
            (GameFlowState.Lobby,          GameFlowState.RoomBrowser),
            (GameFlowState.RoomBrowser,    GameFlowState.JoiningRoom),     // room selected
            (GameFlowState.JoiningRoom,    GameFlowState.RoomLobby),       // ROOM_JOIN_RES ok
            (GameFlowState.JoiningRoom,    GameFlowState.RoomBrowser),     // error (room full...)
            (GameFlowState.RoomLobby,      GameFlowState.ConnectingGame),  // match is starting
            (GameFlowState.ConnectingGame, GameFlowState.InMatch),         // CONNECT_ACCEPTED
            (GameFlowState.ConnectingGame, GameFlowState.RoomLobby),       // connection failed
            (GameFlowState.InMatch,        GameFlowState.MatchEnd),        // S_MATCH_STATE Ended
            (GameFlowState.InMatch,        GameFlowState.Lobby),           // disconnected
            (GameFlowState.MatchEnd,       GameFlowState.Lobby),           // 15 s or Continue
        };

        private static GameFlowState[] AllStates => (GameFlowState[])Enum.GetValues(typeof(GameFlowState));

        /// <summary>Walks the machine to <paramref name="target"/> along the happy path.</summary>
        private static GameFlowController At(GameFlowState target)
        {
            var flow = new GameFlowController();
            if (target == GameFlowState.Booting) return flow;

            GameFlowState[] route =
            {
                GameFlowState.LoginScreen, GameFlowState.Authenticating, GameFlowState.Lobby,
                GameFlowState.RoomBrowser, GameFlowState.JoiningRoom, GameFlowState.RoomLobby,
                GameFlowState.ConnectingGame, GameFlowState.InMatch, GameFlowState.MatchEnd,
            };

            foreach (GameFlowState step in route)
            {
                flow.Transition(step);
                if (step == target) return flow;
            }

            throw new InvalidOperationException($"{target} is not on the happy path.");
        }

        // --------------------------------------------------------- the table matches the diagram

        [Fact]
        public void EveryEdgeInTheDiagramIsLegal()
        {
            foreach ((GameFlowState from, GameFlowState to) in DiagramEdges)
                Assert.True(
                    GameFlowController.IsLegal(from, to),
                    $"the diagram has {from} -> {to} and the table does not");
        }

        [Fact]
        public void TheTableHasNoEdgeTheDiagramDoesNot()
        {
            // The other direction, and the one that catches a helpful edge added by hand: a
            // table that is a superset of the diagram still passes every test above.
            var expected = new HashSet<(GameFlowState, GameFlowState)>(DiagramEdges);

            foreach (GameFlowState from in AllStates)
                foreach (GameFlowState to in GameFlowController.DestinationsFrom(from))
                    Assert.True(
                        expected.Contains((from, to)),
                        $"the table has {from} -> {to} and the diagram does not");
        }

        [Fact]
        public void EveryEdgeInTheDiagramCanActuallyBeTaken()
        {
            foreach ((GameFlowState from, GameFlowState to) in DiagramEdges)
            {
                GameFlowController flow = At(from);
                flow.Transition(to);
                Assert.Equal(to, flow.State);
            }
        }

        [Fact]
        public void EveryTransitionOutsideTheDiagramThrows()
        {
            var legal = new HashSet<(GameFlowState, GameFlowState)>(DiagramEdges);

            foreach (GameFlowState from in AllStates)
            {
                foreach (GameFlowState to in AllStates)
                {
                    if (legal.Contains((from, to))) continue;

                    GameFlowController flow = At(from);
                    Assert.False(flow.CanTransition(to));
                    Assert.False(flow.TryTransition(to));

                    IllegalGameFlowTransitionException error =
                        Assert.Throws<IllegalGameFlowTransitionException>(() => flow.Transition(to));

                    Assert.Equal(from, error.From);
                    Assert.Equal(to, error.To);
                    Assert.Contains(from.ToString(), error.Message);
                    Assert.Contains(to.ToString(), error.Message);
                    Assert.Equal(from, flow.State);   // a refused move changes nothing
                }
            }
        }

        // --------------------------------------------------------- the diagram itself is sound

        [Fact]
        public void EveryStateIsReachableFromBooting()
        {
            var seen = new HashSet<GameFlowState> { GameFlowState.Booting };
            var frontier = new Stack<GameFlowState>();
            frontier.Push(GameFlowState.Booting);

            while (frontier.Count > 0)
            {
                foreach (GameFlowState next in GameFlowController.DestinationsFrom(frontier.Pop()))
                    if (seen.Add(next)) frontier.Push(next);
            }

            foreach (GameFlowState state in AllStates)
                Assert.True(seen.Contains(state), $"{state} cannot be reached from Booting");
        }

        [Fact]
        public void NoStateIsADeadEnd()
        {
            // A state with no way out is a client that has to be restarted to recover, and it
            // is invisible in the diagram until someone walks it.
            foreach (GameFlowState state in AllStates)
                Assert.True(
                    GameFlowController.DestinationsFrom(state).Length > 0,
                    $"{state} has no outgoing edge");
        }

        [Fact]
        public void TheStateCountConstantMatchesTheEnum()
        {
            // The table is indexed by (int)state, so a new state added to the enum without a
            // row would index past the end. This is the cheapest place to notice.
            Assert.Equal(GameFlowController.StateCount, AllStates.Length);

            for (int i = 0; i < AllStates.Length; i++)
                Assert.Equal(i, (int)AllStates[i]);
        }

        // --------------------------------------------------------- behaviour

        [Fact]
        public void ANewControllerIsBooting()
        {
            var flow = new GameFlowController();

            Assert.Equal(GameFlowState.Booting, flow.State);
            Assert.Equal(0, flow.TransitionCount);
        }

        [Fact]
        public void StateChangedCarriesBothEnds()
        {
            var flow = new GameFlowController();
            var seen = new List<(GameFlowState, GameFlowState)>();
            flow.OnStateChanged += (from, to) => seen.Add((from, to));

            flow.Transition(GameFlowState.LoginScreen);
            flow.Transition(GameFlowState.Authenticating);

            Assert.Equal(
                new[]
                {
                    (GameFlowState.Booting, GameFlowState.LoginScreen),
                    (GameFlowState.LoginScreen, GameFlowState.Authenticating),
                },
                seen);
            Assert.Equal(2, flow.TransitionCount);
        }

        [Fact]
        public void ARefusedTransitionRaisesNothing()
        {
            var flow = new GameFlowController();
            int raised = 0;
            flow.OnStateChanged += (_, _) => raised++;

            Assert.Throws<IllegalGameFlowTransitionException>(() => flow.Transition(GameFlowState.InMatch));
            Assert.False(flow.TryTransition(GameFlowState.InMatch));

            Assert.Equal(0, raised);
            Assert.Equal(0, flow.TransitionCount);
        }

        [Fact]
        public void TryTransitionTakesTheMoveWhenItIsLegal()
        {
            var flow = new GameFlowController();

            Assert.True(flow.TryTransition(GameFlowState.LoginScreen));
            Assert.Equal(GameFlowState.LoginScreen, flow.State);
            Assert.Equal(1, flow.TransitionCount);
        }

        [Fact]
        public void ResetGoesBackToBootingFromAnywhereAndSaysSo()
        {
            GameFlowController flow = At(GameFlowState.InMatch);
            var seen = new List<(GameFlowState, GameFlowState)>();
            flow.OnStateChanged += (from, to) => seen.Add((from, to));

            flow.Reset();

            Assert.Equal(GameFlowState.Booting, flow.State);
            Assert.Equal(new[] { (GameFlowState.InMatch, GameFlowState.Booting) }, seen);
        }

        [Fact]
        public void ResetFromBootingIsSilent()
        {
            var flow = new GameFlowController();
            int raised = 0;
            flow.OnStateChanged += (_, _) => raised++;

            flow.Reset();

            Assert.Equal(0, raised);
        }

        [Fact]
        public void DestinationsCannotBeRewrittenByACaller()
        {
            // The table is static and shared; handing out its own row would let one caller
            // change the state machine for the whole process.
            GameFlowState[] first = GameFlowController.DestinationsFrom(GameFlowState.Authenticating);
            first[0] = GameFlowState.InMatch;

            Assert.False(GameFlowController.IsLegal(GameFlowState.Authenticating, GameFlowState.InMatch));
            Assert.True(GameFlowController.IsLegal(GameFlowState.Authenticating, GameFlowState.Lobby));
        }

        [Fact]
        public void TheIllegalTransitionExceptionIsDistinguishableFromTheFrameworksOwn()
        {
            // MasterClient throws InvalidOperationException for "not connected"; a catch around
            // a request that also transitions must be able to tell the two apart, or a bug here
            // is laundered into "lost the connection to the master server".
            var flow = new GameFlowController();

            Exception error = Assert.Throws<IllegalGameFlowTransitionException>(
                () => flow.Transition(GameFlowState.InMatch));

            Assert.IsNotType<InvalidOperationException>(error);   // the exact base, not this type
            Assert.IsAssignableFrom<InvalidOperationException>(error);
        }

        [Fact]
        public void AnUndeclaredStateValueIsIllegalRatherThanACrash()
        {
            // A cast from an int off a config file or a save is the realistic source.
            Assert.False(GameFlowController.IsLegal((GameFlowState)99, GameFlowState.Lobby));
            Assert.Empty(GameFlowController.DestinationsFrom((GameFlowState)99));
        }

        // --------------------------------------------------------- the round trip

        [Fact]
        public void AFullMatchCycleWalksBackToTheLobbyAndCanStartAnother()
        {
            // phase-03 criterion 5: a second match starts without errors. The state machine's
            // half of that is simply that the cycle closes.
            GameFlowController flow = At(GameFlowState.MatchEnd);
            flow.Transition(GameFlowState.Lobby);

            flow.Transition(GameFlowState.RoomBrowser);
            flow.Transition(GameFlowState.JoiningRoom);
            flow.Transition(GameFlowState.RoomLobby);
            flow.Transition(GameFlowState.ConnectingGame);
            flow.Transition(GameFlowState.InMatch);

            Assert.Equal(GameFlowState.InMatch, flow.State);
        }

        [Fact]
        public void EveryFailurePathInTheDiagramGoesSomewhereUsable()
        {
            // The four failure edges, walked in one go: a wrong password, a full room, a
            // refused game server, and a mid-match disconnect.
            var flow = new GameFlowController();
            flow.Transition(GameFlowState.LoginScreen);
            flow.Transition(GameFlowState.Authenticating);
            flow.Transition(GameFlowState.LoginScreen);          // wrong password
            flow.Transition(GameFlowState.Authenticating);
            flow.Transition(GameFlowState.Lobby);
            flow.Transition(GameFlowState.RoomBrowser);
            flow.Transition(GameFlowState.JoiningRoom);
            flow.Transition(GameFlowState.RoomBrowser);          // room full
            flow.Transition(GameFlowState.JoiningRoom);
            flow.Transition(GameFlowState.RoomLobby);
            flow.Transition(GameFlowState.ConnectingGame);
            flow.Transition(GameFlowState.RoomLobby);            // game server refused
            flow.Transition(GameFlowState.ConnectingGame);
            flow.Transition(GameFlowState.InMatch);
            flow.Transition(GameFlowState.Lobby);                // disconnected mid-match

            Assert.Equal(GameFlowState.Lobby, flow.State);
        }
    }
}
