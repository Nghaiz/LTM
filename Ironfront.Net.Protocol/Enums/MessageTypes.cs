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
        /// <summary>Player list and scores, for the scoreboard. Channel 2.</summary>
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

        RoomListRequest   = 0x0010,
        RoomListResponse  = 0x0011,
        RoomCreateRequest = 0x0012,
        RoomCreateResponse= 0x0013,
        RoomJoinRequest   = 0x0014,
        RoomJoinResponse  = 0x0015,
        RoomLeaveRequest  = 0x0016,
        RoomStatePush     = 0x0017,
        RoomReadyRequest  = 0x0018,

        ChatSend = 0x0020,
        ChatPush = 0x0021,

        MatchmakeRequest  = 0x0030,
        MatchmakeResponse = 0x0031,
        MatchmakeCancel   = 0x0032,

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
    }
}
