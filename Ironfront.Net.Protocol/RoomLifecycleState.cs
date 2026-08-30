namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Where a lobby room is in its match cycle. One byte on the wire, in
    /// <c>MSP_ROOM_STATE_PUSH</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it lives here and not in <c>Ironfront.MasterServer.Lobby</c>, where it was born.</b>
    /// It is a value the master SENDS, so both ends need to read it -- and the client must not
    /// reference the master server. <c>Ironfront.Net.Protocol</c> is the one assembly both ends
    /// already reference, which makes it the only home that does not require a new edge in the
    /// dependency graph.
    /// </para>
    /// <para>
    /// That choice is what X-77 was blocked on: <c>RoomState.State</c> was a raw <c>byte</c> on
    /// the client and this enum on the server, so nothing on the client could say what a pushed
    /// state MEANT, and the one edge out of <c>RoomLobby</c> was left to a debug button. The
    /// numbering is unchanged and the wire is unchanged -- this names what was already being
    /// sent.
    /// </para>
    /// </remarks>
    public enum RoomLifecycleState : byte
    {
        /// <summary>Filling. Players may still join and leave.</summary>
        Waiting = 0,

        /// <summary>The match has been called and clients should dial the game server.</summary>
        Starting = 1,

        /// <summary>The match is running.</summary>
        InMatch = 2,

        /// <summary>The match is over and the room is returning to <see cref="Waiting"/>.</summary>
        Ending = 3,
    }
}
