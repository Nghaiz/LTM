namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The single source of truth for every protocol constant.
    /// Mirrors plans/00-shared/protocol-spec.md section 1 exactly.
    /// </summary>
    /// <remarks>
    /// Re-hardcoding any of these numbers anywhere else in the solution is forbidden
    /// (protocol-spec.md line 10). tools/SpecChecker verifies this file against the
    /// spec document on every CI run — if you change a value here without changing
    /// the spec (and bumping PROTOCOL_VERSION), the build fails.
    /// </remarks>
    public static class ProtocolConstants
    {
        public const ushort PROTOCOL_ID       = 0x4946;  // 'IF' — filters out junk packets
        public const byte   PROTOCOL_VERSION  = 4;

        public const int    MTU_SAFE          = 1200;    // safe through any router
        public const int    GSP_HEADER_SIZE   = 16;
        public const int    MAX_PAYLOAD       = MTU_SAFE - GSP_HEADER_SIZE;  // 1184

        /// <summary>
        /// The transport's per-channel header, between the GSP header and the section-4
        /// payload frame. See <see cref="ChannelEnvelope"/>.
        /// </summary>
        public const int    CHANNEL_ENVELOPE_SIZE = 3;

        /// <summary>
        /// Payload-frame budget once the channel envelope is accounted for: 1181 bytes.
        /// </summary>
        /// <remarks>
        /// Anything sizing a buffer against <see cref="MAX_PAYLOAD"/> and then writing a
        /// payload frame into it is over by exactly <see cref="CHANNEL_ENVELOPE_SIZE"/>.
        /// </remarks>
        public const int    MAX_CHANNEL_PAYLOAD = MAX_PAYLOAD - CHANNEL_ENVELOPE_SIZE;

        public const int    SIM_TICK_RATE     = 30;      // Hz
        public const int    SNAPSHOT_RATE     = 20;      // Hz
        public const int    INPUT_SEND_RATE   = 30;      // Hz
        public const int    INPUT_REDUNDANCY  = 3;       // frames repeated per packet

        public const int    KEEPALIVE_MS      = 1000;
        public const int    TIMEOUT_MS        = 10000;
        public const int    ACK_BITFIELD_BITS = 32;

        public const int    MAX_FRAGMENTS     = 64;      // → max logical payload ~75 KB
        public const int    FRAGMENT_TIMEOUT_MS = 2000;

        public const int    INTERP_BUFFER_MS  = 100;
        public const int    MAX_REWIND_MS     = 200;
        public const int    HITBOX_HISTORY_MS = 1000;

        public const int    MAX_PLAYERS       = 16;
        public const int    MAX_BOTS          = 32;
        public const int    MAX_ACTORS        = 64;      // = MAX_PLAYERS + MAX_BOTS + headroom

        /// <summary>
        /// Concurrent vehicles the world may hold. A SEPARATE u16 id space from
        /// <see cref="MAX_ACTORS"/> — a vehicle is not an actor and never occupies an actorId.
        /// </summary>
        /// <remarks>
        /// 16 rather than "as many as fit": it bounds the vehicle snapshot body at
        /// <c>16 x 30 + 9 = 489</c> bytes, which is what lets the elastic actor body be sized
        /// against whatever the vehicle body actually consumed (protocol-spec.md section 4.10,
        /// co-residency). It also leaves the id quarantine below room to hold ids while a
        /// spawner replaces a wreck.
        /// </remarks>
        public const int    MAX_VEHICLES      = 16;

        /// <summary>
        /// Ticks a retired vehicleId is held before it may be reissued. 150 ticks = 5 s at
        /// <see cref="SIM_TICK_RATE"/>, the same quarantine actorIds get (section 4.3.1).
        /// </summary>
        /// <remarks>
        /// For the same reason: snapshots and events naming a destroyed vehicle are in flight
        /// for up to one interpolation buffer plus retransmits, and reissuing the id
        /// immediately makes the client apply a wreck's tail packets to its replacement.
        /// </remarks>
        public const int    VEHICLE_ID_QUARANTINE_TICKS = 150;

        // ===== Derived values — computed here so nobody recomputes them inline =====

        /// <summary>Milliseconds per simulation tick (33.33 ms at 30 Hz).</summary>
        public const float  MS_PER_TICK = 1000f / SIM_TICK_RATE;

        /// <summary>
        /// Maximum ticks the server may rewind hitboxes for lag compensation.
        /// = MAX_REWIND_MS * SIM_TICK_RATE / 1000 = 6 ticks. Anti-abuse clamp
        /// (protocol-spec.md section 7.2).
        /// </summary>
        public const int    MAX_REWIND_TICKS = MAX_REWIND_MS * SIM_TICK_RATE / 1000;

        /// <summary>Hitbox history ring-buffer length, in ticks (1 second = 30).</summary>
        public const int    HITBOX_HISTORY_TICKS = HITBOX_HISTORY_MS * SIM_TICK_RATE / 1000;

        /// <summary>
        /// Maximum number of fragment groups a single connection may have awaiting
        /// reassembly. Mandatory anti-DoS limit (protocol-spec.md section 6) — without it
        /// an attacker sends fragmentCount=64 plus one fragment, repeatedly, until the
        /// server runs out of memory.
        /// </summary>
        public const int    MAX_PENDING_FRAGMENT_GROUPS = 8;

        /// <summary>
        /// Maximum MSP frame body size. Anything larger closes the connection
        /// (protocol-spec.md section 10, memory-exhaustion defense).
        /// </summary>
        public const int    MSP_MAX_FRAME_LENGTH = 64 * 1024;

        /// <summary>joinTicket total size in bytes (protocol-spec.md section 12).</summary>
        public const int    JOIN_TICKET_SIZE = 64;

        // ===== Shared gameplay constants =====
        //
        // These are NOT wire-format values, so protocol-spec.md does not declare them and
        // tools/SpecChecker does not grade them — it only walks the constants the spec names.
        // They live here anyway because they are the one thing a wire constant and a gameplay
        // constant have in common: the client and the server must agree on them exactly, and
        // this is the file both sides already reference. A reload the client believes takes 2 s
        // and the server believes takes 2.5 s produces a clip that refills twice, which is the
        // same class of bug as a field the two sides pack differently.

        /// <summary>
        /// Seconds a reload takes, on both the client's prediction and the server's clock.
        /// </summary>
        /// <remarks>
        /// Read by <c>ClientCombatState.DefaultReloadSeconds</c> and by
        /// <c>ServerReloadPolicy</c>. Neither declares its own literal.
        /// </remarks>
        public const float  RELOAD_SECONDS  = 2f;

        /// <summary>Seconds after death before a respawn may be requested, on both sides.</summary>
        public const float  RESPAWN_SECONDS = 3f;

        /// <summary>
        /// Metres from an actor's feet to its eyes while standing. The hitscan origin.
        /// </summary>
        /// <remarks>
        /// Derived from <c>MovementCore.StandHeight</c> (1.8 m) at the ~0.89 of full height a
        /// humanoid's eyes sit at. It cannot reference that constant directly — this assembly
        /// is below the replication library and must stay that way — so the derivation is
        /// recorded here instead of implied.
        /// </remarks>
        public const float  EYE_HEIGHT = 1.6f;

        /// <summary>Metres from feet to eyes while crouched or prone.</summary>
        /// <remarks>
        /// The same 0.89 ratio applied to <c>MovementCore.CrouchHeight</c> (0.5 m). Low, and
        /// deliberately so: a crouched shooter firing from standing eye height is the bug this
        /// constant exists to stop, and it only shows up as "shots that should have cleared a
        /// wall did not".
        /// </remarks>
        public const float  EYE_HEIGHT_CROUCHED = 0.45f;
    }
}
