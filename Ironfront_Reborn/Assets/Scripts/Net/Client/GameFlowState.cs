namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Where the client is between launching the game and the end of a match. phase-03 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Written by the lead's assist track
    /// (plans/assist-dev-a/step-05-game-flow.md).
    /// </para>
    /// <para>
    /// The values are contiguous from zero and are indexed as such by
    /// <see cref="GameFlowController"/>'s transition table. Insert a new state at the end, or
    /// fix the table in the same edit.
    /// </para>
    /// </remarks>
    public enum GameFlowState
    {
        /// <summary>Process start, before anything has been shown.</summary>
        Booting = 0,

        /// <summary>Username and password on screen, nothing in flight.</summary>
        LoginScreen = 1,

        /// <summary>LOGIN_REQ sent, waiting on the master.</summary>
        Authenticating = 2,

        /// <summary>Logged in. The top-level menu.</summary>
        Lobby = 3,

        /// <summary>The room list is up.</summary>
        RoomBrowser = 4,

        /// <summary>ROOM_JOIN_REQ sent, waiting on the master.</summary>
        JoiningRoom = 5,

        /// <summary>In a room, waiting for the match to start.</summary>
        RoomLobby = 6,

        /// <summary>Dialling the game server over UDP with the joinTicket.</summary>
        ConnectingGame = 7,

        /// <summary>Playing.</summary>
        InMatch = 8,

        /// <summary>The round is decided and the final scoreboard is up.</summary>
        MatchEnd = 9,
    }
}
