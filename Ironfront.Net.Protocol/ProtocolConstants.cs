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
        public const byte   PROTOCOL_VERSION  = 1;

        public const int    MTU_SAFE          = 1200;    // safe through any router
        public const int    GSP_HEADER_SIZE   = 16;
        public const int    MAX_PAYLOAD       = MTU_SAFE - GSP_HEADER_SIZE;  // 1184

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
    }
}
