namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Client-to-server message type, carried inside a PAYLOAD datagram.
    /// Range 0x20-0x3F. protocol-spec.md section 4.1.
    /// </summary>
    public enum ClientMessageType : byte
    {
        /// <summary>Input frames. Channel 3 (unreliable-sequenced).</summary>
        Input         = 0x20,
        /// <summary>
        /// Vehicle axes plus turret aim, sent only while seated. Channel 3
        /// (unreliable-sequenced), and unlike <see cref="Input"/> it carries no frame
        /// redundancy — see protocol-spec.md section 4.10.
        /// </summary>
        VehicleInput  = 0x21,
        /// <summary>Weapon selection before spawning. Channel 2.</summary>
        LoadoutSelect = 0x22,
        /// <summary>Requests a respawn at a spawn point. Channel 2.</summary>
        SpawnRequest  = 0x23,
        /// <summary>In-match chat. Channel 2.</summary>
        Chat          = 0x24,
        /// <summary>RTT measurement, carries a client timestamp. Channel 0.</summary>
        Ping          = 0x25,
        /// <summary>
        /// Asks to enter or leave a vehicle seat. Channel 2 (reliable-ordered), because
        /// leaving is the one edge-triggered vehicle action and losing it strands the player.
        /// The server answers with <see cref="ServerMessageType.SeatChange"/> either way.
        /// </summary>
        SeatRequest   = 0x26,
        /// <summary>Confirms snapshot tick N was received, for delta encoding. Channel 2.</summary>
        AckBaseline   = 0x27,
    }

    /// <summary>
    /// Server-to-client message type, carried inside a PAYLOAD datagram.
    /// Range 0x40-0x5F. protocol-spec.md section 4.1.
    /// </summary>
    public enum ServerMessageType : byte
    {
        /// <summary>World state. Channel 1.</summary>
        Snapshot      = 0x40,
        /// <summary>A new actor appeared. Channel 2.</summary>
        SpawnActor    = 0x41,
        /// <summary>An actor disappeared. Channel 2.</summary>
        DespawnActor  = 0x42,
        /// <summary>Hit confirmation, for the hitmarker. Channel 2.</summary>
        HitConfirm    = 0x43,
        /// <summary>Someone died, with a force vector for the local ragdoll. Channel 2.</summary>
        Death         = 0x44,
        /// <summary>Score, time, match state. Channel 2.</summary>
        MatchState    = 0x45,
        /// <summary>A capture point changed state. Channel 2.</summary>
        CapturePoint  = 0x46,
        /// <summary>Chat broadcast. Channel 2.</summary>
        Chat          = 0x47,
        /// <summary>Ping reply, echoes the client timestamp. Channel 0.</summary>
        Pong          = 0x48,
        /// <summary>Another actor just fired, for effects and audio. Channel 1.</summary>
        WeaponFire    = 0x49,
        /// <summary>An explosion at a position, for effects and screen shake. Channel 2.</summary>
        Explosion     = 0x4A,
        /// <summary>actorId to display-name table. Channel 2. The scores are 0x51 (§ 4.13).</summary>
        PlayerList    = 0x4B,
        /// <summary>Vehicle entity stream. Channel 1, alongside <see cref="Snapshot"/>.</summary>
        VehicleSnapshot = 0x4C,
        /// <summary>A vehicle appeared. Channel 2.</summary>
        VehicleSpawn    = 0x4D,
        /// <summary>A vehicle left the world. Channel 2.</summary>
        VehicleDespawn  = 0x4E,
        /// <summary>A projectile was launched, with the parameters to simulate it. Channel 2.</summary>
        ProjectileSpawn = 0x4F,
        /// <summary>Authoritative seat enter/leave, including a rejection. Channel 2.</summary>
        SeatChange      = 0x50,
        /// <summary>
        /// Per-player kills and deaths, for the scoreboard. Channel 2. protocol-spec.md § 4.13.
        /// </summary>
        /// <remarks>
        /// <b>Its own opcode rather than two more fields on <see cref="PlayerList"/></b> (P18
        /// § 1.2). 0x4B's worst case leaves 28 bytes inside <c>MAX_CHANNEL_PAYLOAD</c>, and the
        /// smallest useful widening costs 64 — so a scoreboard bolted onto the name table would
        /// fragment exactly on the full server it is most wanted on. The two also move at
        /// different rates: names on join and on change, these on every death.
        /// </remarks>
        PlayerScores    = 0x51,
    }

    /// <summary>
    /// Master Server Protocol message type (TCP). protocol-spec.md section 11.
    /// Bodies are UTF-8 JSON, unlike GSP which is binary.
    /// </summary>
    public enum MspMessageType : ushort
    {
        // ----- Client <-> Master -----
        LoginRequest     = 0x0001,
        LoginResponse    = 0x0002,
        RegisterRequest  = 0x0003,
        RegisterResponse = 0x0004,

        /// <summary>
        /// Signs in with a remembered token instead of a password. Body
        /// <c>{ "token", "clientVersion", "maps" }</c>; answered with <see cref="LoginResponse"/>.
        /// </summary>
        /// <remarks>
        /// "Remember me" (owner's list of 2026-10-09, item 1). A <see cref="LoginRequest"/> with
        /// <c>"remember": true</c> gets a <c>rememberToken</c> in its response; the client keeps
        /// it and sends it here next time. Each use spends the token and the response carries the
        /// next one. Not a <c>PROTOCOL_VERSION</c> bump, for <see cref="RoomTeamRequest"/>'s
        /// reason: MSP bodies are JSON and an older master answers an unknown opcode with
        /// <see cref="ErrorPush"/>.
        /// </remarks>
        TokenLoginRequest = 0x0005,

        RoomListRequest   = 0x0010,
        RoomListResponse  = 0x0011,
        RoomCreateRequest = 0x0012,
        RoomCreateResponse= 0x0013,
        RoomJoinRequest   = 0x0014,
        RoomJoinResponse  = 0x0015,
        RoomLeaveRequest  = 0x0016,
        RoomStatePush     = 0x0017,
        RoomReadyRequest  = 0x0018,

        /// <summary>
        /// The client asks to move to the other side. Body <c>{ "team": 0|1 }</c>. P16 3.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Its own opcode rather than a field on <see cref="RoomReadyRequest"/></b> (owner
        /// decision, 2026-09-02). Ready and team are independent facts about a member, and
        /// carrying the team on the ready body means neither can move without asserting the
        /// other — a player switching sides would have to re-send a ready value they did not
        /// change, and a nullable sentinel to avoid that is the field admitting it wanted to be
        /// a message. 0x0019 was free at the end of the room range.
        /// </para>
        /// <para>
        /// <b>Not a <c>PROTOCOL_VERSION</c> bump.</b> That constant governs the binary UDP game
        /// protocol; MSP bodies are UTF-8 JSON (protocol-spec.md § 11) and an unknown opcode is
        /// answered with an <see cref="ErrorPush"/> rather than a desync, so an older master and
        /// a newer client still talk.
        /// </para>
        /// </remarks>
        RoomTeamRequest   = 0x0019,

        ChatSend = 0x0020,
        ChatPush = 0x0021,

        MatchmakeRequest  = 0x0030,
        MatchmakeResponse = 0x0031,
        MatchmakeCancel   = 0x0032,

        // ----- Career: global ranking and achievements (owner's list of 2026-10-09, item 4).
        // Additive opcodes, not a PROTOCOL_VERSION bump, for RoomTeamRequest's reason.

        /// <summary>The global ranking. Body <c>{}</c>; answered with <see cref="LeaderboardResponse"/>.</summary>
        LeaderboardRequest  = 0x0040,

        /// <summary>
        /// The best hundred careers by score and the requester's own row. Body
        /// <c>{ "rows": [ { "rank", "playerId", "name", "score", "kills", "deaths", "headshots",
        /// "wins", "matches", "bestStreak" } ], "you": row | null, "players" }</c>.
        /// </summary>
        LeaderboardResponse = 0x0041,

        /// <summary>The requester's achievements. Body <c>{}</c>; answered with <see cref="AchievementsResponse"/>.</summary>
        AchievementsRequest  = 0x0042,

        /// <summary>
        /// Body <c>{ "unlocked": [ { "id", "at" } ], "earned": { id: players }, "players",
        /// "career": { statKey: value } }</c>: what the requester has, how many players have each
        /// one (the page's share), and the career numbers progress is drawn from.
        /// </summary>
        AchievementsResponse = 0x0043,

        /// <summary>
        /// The client reports practice achievements, which only it can see happen. Body
        /// <c>{ "ids": [ ... ] }</c>; the master keeps only ids the catalogue marks as claimed by
        /// the client, and answers with <see cref="AchievementsResponse"/>.
        /// </summary>
        AchievementClaimRequest = 0x0044,

        /// <summary>
        /// Master → client, unasked: achievements just earned. Body <c>{ "ids": [ ... ] }</c>.
        /// Sent at the end of a round for the ones the round earned, so the toast shows in the match.
        /// </summary>
        AchievementUnlockedPush = 0x0045,

        Heartbeat = 0x00F0,
        ErrorPush = 0x00F1,

        // ----- Game Server <-> Master -----
        GsRegister        = 0x0100,
        GsRegisterResponse= 0x0101,
        GsHeartbeat       = 0x0102,
        GsMatchStarted    = 0x0103,
        GsMatchEnded      = 0x0104,
        GsPlayerJoined    = 0x0105,
        GsPlayerLeft      = 0x0106,

        /// <summary>
        /// The master tells a game server the room it now hosts and what that room asked for.
        /// M→G, body <c>{ "serverId", "roomId", "mapId", "botsPerTeam" }</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The first master → game-server push.</b> Every other opcode in this range runs
        /// the other way, and P14 learned the ROOM without one: it arrives signed inside every
        /// join ticket. The room's bot count had no such carrier -- the ticket's 32 signed bytes
        /// are full -- so the create-room form's Bots field reached the master and stopped
        /// there, and every match got the prefab's 16 per team whatever the room asked for
        /// (owner report 2026-09-28: a room set to 0 bots released 32).
        /// </para>
        /// <para>
        /// <b>Sent with every ticket the master issues</b>, not once at allocation: a game server
        /// that re-registered in between would otherwise never hear it, and the frame is a few
        /// dozen bytes per join. The game server keeps the latest one and applies it only to the
        /// room its tickets name.
        /// </para>
        /// <para>
        /// <b>Not a <c>PROTOCOL_VERSION</c> bump</b>, for the reason
        /// <see cref="RoomTeamRequest"/> gives: that constant governs the binary UDP protocol,
        /// and MSP bodies are JSON. A game server built before this opcode hands the frame to
        /// a registration that is no longer pending and nothing reads it, so it keeps its
        /// prefab's roster rather than failing.
        /// </para>
        /// </remarks>
        GsRoomAssigned    = 0x0107,
    }
}
