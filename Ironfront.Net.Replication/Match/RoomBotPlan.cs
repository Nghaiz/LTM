using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>How many bots each side of a round's roster gets, and why.</summary>
    public readonly struct BotRosterSize
    {
        public BotRosterSize(int team0, int team1, string reason)
        {
            Team0 = team0;
            Team1 = team1;
            Reason = reason ?? string.Empty;
        }

        public int Team0 { get; }

        public int Team1 { get; }

        /// <summary>
        /// Where the numbers came from, as a clause for the server's "bots released" line, so a
        /// room that got the prefab roster says why instead of looking like it was obeyed.
        /// </summary>
        public string Reason { get; }
    }

    /// <summary>
    /// The bot roster a game server fields for the room it hosts: the room's own count when the
    /// master has said what it is, the authored roster otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> The create-room form's Bots field was validated, sent as
    /// <c>ROOM_CREATE_REQ.botCount</c>, stored on the master's room -- and read by no game
    /// server, because nothing carried it there. Every match released
    /// <c>_Managers.prefab</c>'s 16 per team: a room created with 0 bots got 32 (owner report,
    /// 2026-09-28). The master now pushes it with every ticket (<c>GS_ROOM_ASSIGNED</c>).
    /// </para>
    /// <para>
    /// <b>Applied only to the room this server's tickets name.</b> The ROOM reaches a game
    /// server signed inside each join ticket (<c>ServerRoomIdentity</c>); the push only adds the
    /// room's settings. So a push is matched against that room rather than trusted on its own:
    /// a push for a different room means the master and this server disagree about what is
    /// being hosted, and the tickets are the authenticated side of that disagreement.
    /// </para>
    /// <para>
    /// <b>The fallback is the authored roster, said out loud.</b> No push at all is the ordinary
    /// case for a server nobody allocated -- a standalone run, a harness with its own tickets,
    /// or a master that predates the opcode -- and those have always run the prefab roster.
    /// <see cref="BotRosterSize.Reason"/> names which case it was.
    /// </para>
    /// <para>
    /// <b>Per team, both teams alike</b> (owner ruling, 2026-09-28): the field is "bots per
    /// team", so a room asking for N gets N on each side.
    /// </para>
    /// </remarks>
    public sealed class RoomBotPlan
    {
        private ushort _roomId;
        private int _requested;

        /// <summary>The room the latest push named, or 0 when none has arrived.</summary>
        public ushort AssignedRoomId => _roomId;

        /// <summary>The bots per team the latest push asked for, clamped to the legal range.</summary>
        public int AssignedBotsPerTeam => Clamp(_requested);

        /// <summary>Records a <c>GS_ROOM_ASSIGNED</c>. The latest one wins.</summary>
        /// <param name="roomId">The room as the join tickets carry it. 0 names no room.</param>
        /// <param name="botsPerTeam">
        /// What the room asked for. Kept as sent, so the roster's reason can say when it had to
        /// be clamped into 0..<see cref="ProtocolConstants.MAX_BOTS_PER_TEAM"/>.
        /// </param>
        public void Assign(ushort roomId, int botsPerTeam)
        {
            _roomId = roomId;
            _requested = botsPerTeam;
        }

        /// <summary>
        /// The roster for the room this server hosts.
        /// </summary>
        /// <param name="hostedRoomId">
        /// The room this server's verified tickets name (<c>ServerRoomIdentity.RoomId</c>), or 0
        /// when it hosts none.
        /// </param>
        /// <param name="authoredTeam0">The authored roster, used when the room's count does not apply.</param>
        /// <param name="authoredTeam1">The authored roster, used when the room's count does not apply.</param>
        public BotRosterSize Resolve(ushort hostedRoomId, int authoredTeam0, int authoredTeam1)
        {
            if (_roomId == 0)
            {
                return new BotRosterSize(authoredTeam0, authoredTeam1,
                    "no room settings from the master, so the prefab roster");
            }

            if (hostedRoomId == 0)
            {
                return new BotRosterSize(authoredTeam0, authoredTeam1,
                    $"the master assigned room {_roomId} but no ticket for it has arrived, so the prefab roster");
            }

            if (hostedRoomId != _roomId)
            {
                return new BotRosterSize(authoredTeam0, authoredTeam1,
                    $"the master's settings are for room {_roomId} but this server hosts room {hostedRoomId}, "
                    + "so the prefab roster");
            }

            int perTeam = Clamp(_requested);
            string reason = perTeam == _requested
                ? $"room {_roomId} asked for {perTeam} per team"
                : $"room {_roomId} asked for {_requested} per team, clamped to {perTeam}";
            return new BotRosterSize(perTeam, perTeam, reason);
        }

        private static int Clamp(int botsPerTeam)
            => Math.Max(0, Math.Min(botsPerTeam, ProtocolConstants.MAX_BOTS_PER_TEAM));
    }
}
