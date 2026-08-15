// This file is compiled twice: by Unity, where nullable is off, and by
// Ironfront.Client.Flow.Tests, where it is on and warnings are errors. Enabling it per file
// is what lets one piece of source satisfy both — without it, the `?` on OnStateChanged is a
// CS8632 warning in Unity, and removing the `?` is a CS8618 error in the test project.
#nullable enable

using System;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A move the transition table does not list was attempted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own type, rather than a bare <see cref="InvalidOperationException"/>, because callers
    /// have to be able to tell it apart from one. <c>MasterClient</c> throws
    /// <see cref="InvalidOperationException"/> for "not connected" and "already connected", so a
    /// <c>catch (InvalidOperationException)</c> around a request that also transitions would
    /// swallow a state-machine bug and report it as a dead network link — a failure that is
    /// reported, but as the wrong thing, which is harder to find than one that is not reported
    /// at all.
    /// </para>
    /// <para>
    /// It derives from <see cref="InvalidOperationException"/> so that existing handlers and
    /// phase-03's own sketch, which throws that type, keep working.
    /// </para>
    /// </remarks>
    public sealed class IllegalGameFlowTransitionException : InvalidOperationException
    {
        public IllegalGameFlowTransitionException(GameFlowState from, GameFlowState to)
            : base($"Invalid state transition: {from} -> {to}")
        {
            From = from;
            To = to;
        }

        public GameFlowState From { get; }
        public GameFlowState To { get; }
    }

    /// <summary>
    /// The game-flow state machine: the ten states of phase-03 task 1, the transition table
    /// between them, and a guard that refuses every move the table does not list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Written by the lead's assist track
    /// (plans/assist-dev-a/step-05-game-flow.md). Closes phase-03 acceptance criterion 9.
    /// </para>
    /// <para>
    /// <b>It is a plain class, and it has to be.</b> phase-03 sketches
    /// <c>GameFlowController : MonoBehaviour</c> and then asks, in criterion 9, for unit tests
    /// on it. Those two cannot both happen here: the .NET test projects cannot reference
    /// <c>UnityEngine</c>, and <c>Assets/</c> contains no <c>.asmdef</c>, so the Unity Test
    /// Framework is not available either. As a <c>MonoBehaviour</c> the criterion is
    /// unimplementable. As a plain class it costs nothing —
    /// <c>Ironfront.Client.Flow.Tests</c> compiles this file directly, and a
    /// <c>MonoBehaviour</c> that wants one simply owns a reference.
    /// </para>
    /// <para>
    /// <b>An illegal transition throws, and this is the one place in the client where that is
    /// right.</b> It is a programming error rather than bad input: it happens once, at the call
    /// site that got it wrong, and the alternative failure is the bug phase-03 names — "we're
    /// in the lobby but the match HUD is still showing" — which has no error message and is
    /// found by staring at the screen. That does not contradict conventions.md § 3.2, which
    /// governs the packet path, where malformed input arrives from the network and is routine.
    /// Nothing here comes off a wire.
    /// </para>
    /// <para>
    /// <b>UI code should ask <see cref="CanTransition"/> rather than catch.</b> The exception
    /// exists to catch a wrong call, not to be a control-flow branch — a button that would make
    /// an illegal move should be disabled, not pressed and rescued.
    /// </para>
    /// </remarks>
    public sealed class GameFlowController
    {
        /// <summary>
        /// The transition table, declared in full from the <c>stateDiagram-v2</c> block under
        /// phase-03 task 1, including its failure edges.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Indexed by <c>(int)GameFlowState</c> rather than held in a dictionary: the states are
        /// contiguous from zero, the rows are at most two entries long, and a lookup is then an
        /// array index and a two-element scan with nothing hashed and nothing allocated.
        /// </para>
        /// <para>
        /// <b>Two edges a reader may expect are deliberately absent, because the diagram does
        /// not have them:</b> there is no way out of <see cref="GameFlowState.RoomLobby"/>
        /// except into <see cref="GameFlowState.ConnectingGame"/> (so no "leave room"), and no
        /// edge from the lobby side back to <see cref="GameFlowState.LoginScreen"/> (so a
        /// master disconnect while browsing rooms has nowhere to go). Both are real gaps in the
        /// specification rather than oversights here, and adding them silently would put this
        /// table and the diagram out of sync — which is the one thing the table must never be.
        /// </para>
        /// </remarks>
        private static readonly GameFlowState[][] Allowed = BuildTable();

        private static GameFlowState[][] BuildTable()
        {
            var table = new GameFlowState[StateCount][];

            table[(int)GameFlowState.Booting] = new[] { GameFlowState.LoginScreen };
            table[(int)GameFlowState.LoginScreen] = new[] { GameFlowState.Authenticating };

            // Authenticating --> LoginScreen: failed (show error)
            table[(int)GameFlowState.Authenticating] =
                new[] { GameFlowState.Lobby, GameFlowState.LoginScreen };

            table[(int)GameFlowState.Lobby] = new[] { GameFlowState.RoomBrowser };
            table[(int)GameFlowState.RoomBrowser] = new[] { GameFlowState.JoiningRoom };

            // JoiningRoom --> RoomBrowser: error (room full...)
            table[(int)GameFlowState.JoiningRoom] =
                new[] { GameFlowState.RoomLobby, GameFlowState.RoomBrowser };

            table[(int)GameFlowState.RoomLobby] = new[] { GameFlowState.ConnectingGame };

            // ConnectingGame --> RoomLobby: connection failed
            table[(int)GameFlowState.ConnectingGame] =
                new[] { GameFlowState.InMatch, GameFlowState.RoomLobby };

            // InMatch --> Lobby: disconnected
            table[(int)GameFlowState.InMatch] =
                new[] { GameFlowState.MatchEnd, GameFlowState.Lobby };

            // MatchEnd --> Lobby: after 15 seconds or on Continue
            table[(int)GameFlowState.MatchEnd] = new[] { GameFlowState.Lobby };

            return table;
        }

        /// <summary>How many states the enum declares. The table's row count.</summary>
        public const int StateCount = 10;

        /// <summary>
        /// Seconds the final scoreboard is held before returning to the lobby by itself.
        /// phase-03 task 6.
        /// </summary>
        public const float MatchEndHoldSeconds = 15f;

        /// <summary>Where the client is. Starts at <see cref="GameFlowState.Booting"/>.</summary>
        public GameFlowState State { get; private set; } = GameFlowState.Booting;

        /// <summary>Transitions that were made. A match cycle adds several.</summary>
        public long TransitionCount { get; private set; }

        /// <summary>Fires after <see cref="State"/> has moved. Carries (previous, current).</summary>
        public event Action<GameFlowState, GameFlowState>? OnStateChanged;

        /// <summary>Whether <paramref name="next"/> is reachable from the current state.</summary>
        public bool CanTransition(GameFlowState next) => IsLegal(State, next);

        /// <summary>
        /// Moves to <paramref name="next"/>.
        /// </summary>
        /// <exception cref="IllegalGameFlowTransitionException">
        /// The table does not list <paramref name="next"/> as reachable from <see cref="State"/>.
        /// </exception>
        public void Transition(GameFlowState next)
        {
            if (!IsLegal(State, next)) throw new IllegalGameFlowTransitionException(State, next);

            GameFlowState previous = State;
            State = next;
            TransitionCount++;
            OnStateChanged?.Invoke(previous, next);
        }

        /// <summary>
        /// Moves to <paramref name="next"/> if the table allows it, and reports whether it did.
        /// </summary>
        /// <remarks>
        /// For the handful of call sites where losing a race is expected rather than wrong — a
        /// connect timeout firing on the same frame the connection succeeds, say. Everything
        /// else should call <see cref="Transition"/> and be fixed when it throws.
        /// </remarks>
        public bool TryTransition(GameFlowState next)
        {
            if (!IsLegal(State, next)) return false;
            Transition(next);
            return true;
        }

        /// <summary>
        /// Returns to <see cref="GameFlowState.Booting"/>, ignoring the table.
        /// </summary>
        /// <remarks>
        /// Not a transition and deliberately not expressible as one: it is the teardown a fresh
        /// process would have done, used when a match cycle is abandoned outright. It fires
        /// <see cref="OnStateChanged"/> so listeners can tear down with it, and it is the only
        /// method here that does not consult <see cref="Allowed"/> — which is why its
        /// destination is fixed rather than a parameter.
        /// </remarks>
        public void Reset()
        {
            if (State == GameFlowState.Booting) return;

            GameFlowState previous = State;
            State = GameFlowState.Booting;
            OnStateChanged?.Invoke(previous, GameFlowState.Booting);
        }

        /// <summary>Whether the table lists an edge from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static bool IsLegal(GameFlowState from, GameFlowState to)
        {
            int row = (int)from;
            if (row < 0 || row >= Allowed.Length) return false;

            // A row can be null only if the enum grew without BuildTable growing with it. That is
            // a programming error, but answering it with "illegal" rather than a NullReference
            // keeps the promise this method makes to every caller: it reports, it never throws.
            GameFlowState[] destinations = Allowed[row];
            if (destinations == null) return false;

            for (int i = 0; i < destinations.Length; i++)
                if (destinations[i] == to) return true;

            return false;
        }

        /// <summary>
        /// The states reachable from <paramref name="from"/>, as a fresh array.
        /// </summary>
        /// <remarks>
        /// Copies rather than handing out the table's own row, so a caller cannot rewrite the
        /// state machine by assigning into what it was given. Called by a UI deciding which
        /// buttons to enable and by the tests — never per frame, so the allocation is fine.
        /// </remarks>
        public static GameFlowState[] DestinationsFrom(GameFlowState from)
        {
            int row = (int)from;
            if (row < 0 || row >= Allowed.Length || Allowed[row] == null) return Array.Empty<GameFlowState>();

            var copy = new GameFlowState[Allowed[row].Length];
            Array.Copy(Allowed[row], copy, copy.Length);
            return copy;
        }
    }
}
