namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Which lobby room this game server is hosting, learned from the signed join tickets that
    /// arrive at it. Phase P14 task 3.1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The number used to be typed into a prefab.</b> <c>ServerMasterReporter</c> carried a
    /// <c>[SerializeField] private int _roomId</c> whose own tooltip said "0 in standalone", and
    /// every match report was stamped with whatever was authored there. That is not a cosmetic
    /// untidiness: <c>MspMessageDispatcher.HandleMatchStarted</c> opens with
    /// <c>_gameServers.OwnsRoom(connection.Id, serverId, roomId)</c>, and <c>OwnsRoom</c>
    /// requires <c>server.AssignedRoomId == roomId</c>. A hand-typed number that does not match
    /// the room the master allocated is dropped with no error and no log, so the room stays
    /// <c>Waiting</c> for ever and the fix that sent the message looks applied.
    /// </para>
    /// <para>
    /// <b>Nothing tells a game server which room it was allocated to.</b> Every opcode in the
    /// <c>0x0100</c>–<c>0x0106</c> range is game-server → master; there is no assignment push,
    /// and P14 deliberately did not add one. The number arrives anyway, signed: every joining
    /// client hands over an HMAC-verified <c>JoinTicket</c> carrying the <c>u16 roomId</c> the
    /// master put it in. So the server LEARNS its room from the first ticket it verifies — no
    /// new opcode, no spec change, and no unauthenticated input, because a forged ticket fails
    /// <c>BadSignature</c> before these bytes are read.
    /// </para>
    /// <para>
    /// <b>A second room's ticket is refused, never adopted.</b> Silently re-pointing would mean
    /// two rooms' players sharing one match with only one of them reported, and the anomaly it
    /// implies — the master allocating one server to two rooms — is worth a loud line. See
    /// <see cref="Observe"/>.
    /// </para>
    /// <para>
    /// <b>Zero is the honest answer, not a fabricated one.</b> With no client connected the
    /// server genuinely has no room, and a ticketless join (the loopback wire, a development
    /// stub whose ticket payload is all zeroes) carries no room either. Both leave
    /// <see cref="RoomId"/> at 0, which is exactly the standalone behaviour the deleted
    /// tooltip described.
    /// </para>
    /// </remarks>
    public sealed class ServerRoomIdentity
    {
        /// <summary>The room this server is hosting, or 0 when it is hosting none.</summary>
        public ushort RoomId { get; private set; }

        /// <summary>True once a ticket has named a room.</summary>
        public bool HasRoom => RoomId != 0;

        /// <summary>
        /// Tickets refused because they named a different room. Counted rather than only
        /// logged, so a test can assert the refusal happened without reading a log.
        /// </summary>
        public long ConflictingTickets { get; private set; }

        /// <summary>
        /// Takes the room a verified ticket named. Adopts it when this server has none, agrees
        /// when it matches, and refuses when it does not.
        /// </summary>
        /// <param name="roomId">
        /// The room from the ticket. <b>Call only after the HMAC has been verified</b> — before
        /// that these bytes are the caller's rather than the master's.
        /// </param>
        /// <param name="conflict">
        /// Empty on success; on refusal, the line to log. Carries both room numbers, because
        /// "the ticket disagreed" without saying what it disagreed with is not diagnosable.
        /// </param>
        /// <returns>False only when the ticket named a different room than the one held.</returns>
        public bool Observe(ushort roomId, out string conflict)
        {
            conflict = string.Empty;

            // A ticketless join names no room. It is not a conflict and it does not clear the
            // room already held: a standalone client connecting by IP to a server that IS
            // hosting a room must not blank the number the match report is stamped with.
            if (roomId == 0) return true;

            if (RoomId == 0)
            {
                RoomId = roomId;
                return true;
            }

            if (RoomId == roomId) return true;

            ConflictingTickets++;
            conflict =
                $"a join ticket named room {roomId} but this server is hosting room {RoomId}. "
                + "Refusing it rather than re-pointing: the master has allocated one game "
                + "server to two rooms, and adopting the newer number would report the running "
                + "match under the wrong room and leave the old one Waiting for ever.";
            return false;
        }

        /// <summary>
        /// Forgets the room, so the next ticket adopts a new one.
        /// </summary>
        /// <remarks>
        /// Called when the match ends, which is the same moment the master runs
        /// <c>_gameServers.Release</c> and the server becomes allocatable again. Without it the
        /// first room a process ever hosted would be the only one it could host: every later
        /// allocation's tickets would hit <see cref="Observe"/>'s refusal and be turned away by
        /// a server that is, in fact, free.
        /// </remarks>
        public void Release() => RoomId = 0;
    }
}
