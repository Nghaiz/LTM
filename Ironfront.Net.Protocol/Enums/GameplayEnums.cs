using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// C_INPUT buttons bitfield (u16). protocol-spec.md section 4.2.
    /// </summary>
    [Flags]
    public enum InputButtons : ushort
    {
        None          = 0,
        Fire          = 1 << 0,
        Aim           = 1 << 1,
        Reload        = 1 << 2,
        Jump          = 1 << 3,
        Crouch        = 1 << 4,
        Sprint        = 1 << 5,
        Prone         = 1 << 6,
        ThrowGrenade  = 1 << 7,
        LeanLeft      = 1 << 8,
        LeanRight     = 1 << 9,
        Use           = 1 << 10,
        SwitchWeapon0 = 1 << 11,
        SwitchWeapon1 = 1 << 12,
        SwitchWeapon2 = 1 << 13,
        SwitchWeapon3 = 1 << 14,
        // Bit 15 reserved.
    }

    /// <summary>
    /// Per-actor stateFlags byte inside a snapshot. protocol-spec.md section 4.3.
    /// </summary>
    [Flags]
    public enum ActorStateFlags : byte
    {
        None        = 0,
        IsAlive     = 1 << 0,
        IsCrouching = 1 << 1,
        IsProne     = 1 << 2,
        IsSprinting = 1 << 3,
        IsAiming    = 1 << 4,
        IsInWater   = 1 << 5,
        /// <summary>Dead; the client enables its own ragdoll. Corpses are never synced (AD-4).</summary>
        IsRagdoll   = 1 << 6,
        IsSeated    = 1 << 7,
    }

    /// <summary>
    /// Snapshot changeMask bits. Bit i = 1 means field i is present in this packet.
    /// protocol-spec.md section 4.3.
    /// </summary>
    [Flags]
    public enum SnapshotField : byte
    {
        None       = 0,
        /// <summary>i16 x 3, quantized position. 6 bytes.</summary>
        Position   = 1 << 0,
        /// <summary>u16 yaw + i8 pitch. 3 bytes.</summary>
        Rotation   = 1 << 1,
        /// <summary>i8 x 3, quantized velocity. 3 bytes.</summary>
        Velocity   = 1 << 2,
        /// <summary>u8 <see cref="ActorStateFlags"/>. 1 byte.</summary>
        StateFlags = 1 << 3,
        /// <summary>u8, 0..100. 1 byte.</summary>
        Health     = 1 << 4,
        /// <summary>u8 weaponId + u8 ammoInClip. 2 bytes.</summary>
        Weapon     = 1 << 5,
        /// <summary>u8. Only sent on change (rare). 1 byte.</summary>
        Team       = 1 << 6,
        /// <summary>u16 vehicleId + u8 seatIndex (stretch goal). 3 bytes.</summary>
        SeatInfo   = 1 << 7,

        /// <summary>
        /// Every field a v1 actor actually uses — bits 0..6, excluding the stretch-goal
        /// seat field. This is the "full snapshot" mask, and it encodes to the 20 bytes
        /// per actor that protocol-spec.md section 4.3 budgets for.
        /// </summary>
        FullNoSeat = Position | Rotation | Velocity | StateFlags | Health | Weapon | Team,
    }

    /// <summary>S_HIT_CONFIRM hitboxType. protocol-spec.md section 4.5.</summary>
    public enum HitboxType : byte
    {
        Body = 0,
        Head = 1,
        Limb = 2,
    }

    /// <summary>S_HIT_CONFIRM flags byte. protocol-spec.md section 4.5.</summary>
    [Flags]
    public enum HitFlags : byte
    {
        None     = 0,
        Killed   = 1 << 0,
        Headshot = 1 << 1,
    }

    /// <summary>
    /// S_SPAWN_ACTOR flags byte. Layout defined by <see cref="SpawnActorMessage"/>, not by
    /// the spec — see that type's remarks.
    /// </summary>
    [Flags]
    public enum SpawnFlags : byte
    {
        None = 0,
        /// <summary>Server-driven AI. The client never predicts these.</summary>
        IsBot = 1 << 0,
        /// <summary>The receiving client's own player. Drives camera + prediction attachment.</summary>
        IsLocalPlayer = 1 << 1,
    }

    /// <summary>
    /// S_DESPAWN_ACTOR reason. Layout defined by <see cref="DespawnActorMessage"/>, not by
    /// the spec.
    /// </summary>
    public enum DespawnReason : byte
    {
        /// <summary>Player disconnected or bot was removed. The id may be reused later.</summary>
        Left = 0,
        /// <summary>Destroyed in the world. Distinct from death, which keeps the actor.</summary>
        Destroyed = 1,
        /// <summary>
        /// Left the viewer's interest set. Reserved — the v1 server never sends it, because
        /// interest management keeps every actor inside 500 m at Far rather than culling it.
        /// </summary>
        Culled = 2,
    }

    /// <summary>S_EXPLOSION kind. Layout defined by <see cref="ExplosionMessage"/>, not by the spec.</summary>
    public enum ExplosionKind : byte
    {
        Grenade = 0,
        Rocket = 1,
        Vehicle = 2,
        Environment = 3,
    }

    /// <summary>
    /// Where a match is in its lifecycle. Carried by <see cref="MatchStateMessage"/> and by
    /// the <c>state</c> field of GS_HEARTBEAT (protocol-spec.md section 11).
    /// </summary>
    /// <remarks>
    /// Declared in the protocol rather than in the replication library because two parties
    /// outside the replication track read it: the client renders a different HUD per phase, and the master
    /// server decides whether a server is joinable from the heartbeat's copy of it. A second
    /// enum on either side is the duplicate source of truth the conventions forbid.
    /// </remarks>
    public enum MatchPhase : byte
    {
        /// <summary>Not enough humans to start. Bots may still be running.</summary>
        WaitingForPlayers = 0,
        /// <summary>Countdown before the round opens. Spawns allowed, damage is not.</summary>
        Warmup = 1,
        /// <summary>Live round. The only phase in which tickets drain.</summary>
        Playing = 2,
        /// <summary>Round decided; the scoreboard is up and the reset timer is running.</summary>
        Ended = 3,
        /// <summary>Tearing the world down. A single tick in practice — see <c>MatchStateMachine</c>.</summary>
        Resetting = 4,
    }

    /// <summary>
    /// The team that owns something, where "nobody" is a legal answer.
    /// </summary>
    /// <remarks>
    /// A <c>byte</c> rather than a nullable, because it goes on the wire. 255 is used for the
    /// absent case instead of 2 so that a client which switches on 0/1 and forgets the third
    /// case falls through rather than rendering the neutral state as a third team.
    /// </remarks>
    public static class TeamId
    {
        public const byte Team0 = 0;
        public const byte Team1 = 1;
        /// <summary>Neutral, contested, or undecided.</summary>
        public const byte None = 255;
    }

    /// <summary>S_CAPTURE_POINT flags byte. Layout defined by <see cref="CapturePointMessage"/>.</summary>
    [Flags]
    public enum CaptureFlags : byte
    {
        None = 0,
        /// <summary>
        /// Both teams have somebody inside the radius. NOT derivable from the ownership
        /// value — a point can be fully owned and contested at the same time — so it is the
        /// one bit here that is genuinely new information.
        /// </summary>
        Contested = 1 << 0,
    }

    /// <summary>S_DEATH causeOfDeath. protocol-spec.md section 4.6.</summary>
    public enum CauseOfDeath : byte
    {
        Bullet    = 0,
        Explosion = 1,
        Fall      = 2,
        Drown     = 3,
        Vehicle   = 4,
    }

    /// <summary>
    /// Shared error codes returned by MSP responses. protocol-spec.md section 13.
    /// </summary>
    public enum ErrorCode : ushort
    {
        Ok = 0,

        WrongCredentials  = 1000,
        UsernameTaken     = 1001,
        /// <summary>Length 3-16, only a-z0-9_.</summary>
        InvalidUsername   = 1002,
        SessionExpired    = 1003,
        WrongClientVersion= 1004,

        RoomNotFound      = 2000,
        RoomFull          = 2001,
        WrongRoomPassword = 2002,
        MatchAlreadyStarted = 2003,
        AlreadyInAnotherRoom= 2004,

        NoGameServerAvailable = 3000,
        GameServerNotResponding = 3001,

        InternalServerError = 9000,
        RateLimited         = 9001,
    }
}
