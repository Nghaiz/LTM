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
        public const byte   PROTOCOL_VERSION  = 13;

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
        public const int    MAX_REWIND_MS     = 400;
        public const int    HITBOX_HISTORY_MS = 1000;

        public const int    MAX_PLAYERS       = 16;

        /// <summary>
        /// The most bots one match may field, both sides together: the create-room slider's top
        /// end and <c>ROOM_CREATE_REQ.botCount</c>'s bound. 100 since protocol 13 (owner,
        /// 2026-09-30), measured on the P29 AI: one game server holds 100 bots at 60 fps.
        /// </summary>
        public const int    MAX_BOTS          = 100;

        /// <summary>
        /// Actor ids, players and bots in one space. 16 players and 100 bots are 116 at once;
        /// the rest absorbs the id quarantine. A client built against 64 dropped every id past
        /// it, which is why this moved with <see cref="PROTOCOL_VERSION"/> 13.
        /// </summary>
        public const int    MAX_ACTORS        = 128;     // = MAX_PLAYERS + MAX_BOTS + headroom

        /// <summary>
        /// The most bots one side may field. A room asks for a TOTAL (<c>ROOM_CREATE_REQ.botCount</c>,
        /// since protocol 13) and the master splits it evenly; <c>GS_ROOM_ASSIGNED.botsPerTeam</c>
        /// is that half.
        /// </summary>
        public const int    MAX_BOTS_PER_TEAM = MAX_BOTS / 2;

        /// <summary>
        /// Bots per team on a game server the master never told a room: <c>_Managers.prefab</c>'s
        /// design roster, which lane runs and a master-less server field.
        /// </summary>
        public const int    DEFAULT_BOTS_PER_TEAM = 16;

        /// <summary>
        /// Bots in a room nobody chose a number for -- where the create-room slider starts, and
        /// what a matchmade room gets, capacity allowing. 50, the original game's own menu default
        /// (owner, 2026-09-29: matches felt slow next to it at 32).
        /// </summary>
        public const int    DEFAULT_ROOM_BOTS = 50;

        /// <summary>
        /// Concurrent vehicles the world may hold. A SEPARATE u16 id space from
        /// <see cref="MAX_ACTORS"/> — a vehicle is not an actor and never occupies an actorId.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 24 rather than "as many as fit": it bounds the vehicle snapshot body at
        /// <c>24 x 30 + 9 = 729</c> bytes, which is what lets the elastic actor body be sized
        /// against whatever the vehicle body actually consumed (protocol-spec.md section 4.10,
        /// co-residency). It also leaves the id quarantine below room to hold ids while a
        /// spawner replaces a wreck.
        /// </para>
        /// <para>
        /// <b>Raised from 16 at v10.</b> Island exhausted the id space in a real match — the
        /// logs show 15/16 and then 16/16, after which spawners either dropped their vehicle or
        /// created one with id 0, which no client can address. Dustbowl peaks higher than its
        /// spawner count alone suggests, because <c>AfterMoved</c> keeps a superseded wreck
        /// mapped while its replacement is already live, so both hold an id at once. 24 covers
        /// the observed peak and leaves six ids of headroom for replacement and quarantine.
        /// </para>
        /// </remarks>
        public const int    MAX_VEHICLES      = 24;

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
        /// = MAX_REWIND_MS * SIM_TICK_RATE / 1000 = 12 ticks. Anti-abuse clamp
        /// (protocol-spec.md section 7.2), sized for a whole round trip plus the interpolation
        /// buffer: compensation stops growing at about 300 ms of ping.
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
        /// Seconds after the last sprinting input frame during which the trigger stays dead,
        /// on both the client's prediction and the server's authority.
        /// </summary>
        /// <remarks>
        /// The original game's own <c>FpsActorController</c> refuses to fire while sprinting and for a
        /// short window after, because the weapon is lowered and has to come back up. Before
        /// v10 only the client knew that: the client's controller declined the shot while the
        /// server, reading the raw Fire bit out of <c>C_INPUT</c>, accepted it and took the
        /// round. The player watched the magazine drain with no muzzle flash and no projectile.
        /// Both sides now read the window from here rather than each carrying a literal — a
        /// client that blocks for 0.2 s against a server that blocks for 0.25 s reintroduces
        /// the same disagreement in a narrower band, which is harder to see and no less wrong.
        /// </remarks>
        public const float  SPRINT_FIRE_BLOCK_SECONDS = 0.2f;

        /// <summary>
        /// Metres from an actor's feet to its eyes while standing. The hitscan origin.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Where the shooter's own camera is</b>, because a shot leaves along the crosshair
        /// and the crosshair is that camera's centre: <c>Player Fps Actor.prefab</c> puts the FP
        /// camera 0.63 m above the capsule centre, and a standing capsule's centre is half of
        /// <c>MovementCore.StandHeight</c> (1.8 m) up — so 0.90 + 0.63.
        /// </para>
        /// <para>
        /// It used to be 1.6, a ratio of the body height rather than a measurement, which put
        /// every shot 7 cm above the crosshair. Playtest 2026-09-28, bug 5;
        /// <c>HitboxGeometryTests</c> (EditMode) reads the camera out of the prefab and fails
        /// when the two drift apart. This assembly is below the replication library and cannot
        /// name <c>MovementCore</c>, so the derivation is written here instead.
        /// </para>
        /// </remarks>
        public const float  EYE_HEIGHT = 1.53f;

        /// <summary>Metres from feet to eyes while crouched or prone.</summary>
        /// <remarks>
        /// <para>
        /// The same camera over a crouched capsule: <c>MovementCore.CrouchHeight</c> (0.5 m)
        /// puts the centre 0.25 m up, and the camera stays 0.63 m above it — 0.88 m. Crouching
        /// in this game shrinks the capsule and leaves the camera's offset alone
        /// (<c>FpsActorController.StartCrouch</c>).
        /// </para>
        /// <para>
        /// It used to be 0.45, 0.89 of the crouched capsule's height, which fired every crouched
        /// shot 43 cm below the crosshair: aimed at a head it struck the chest, aimed at the
        /// chest it struck the ground in front of the target.
        /// </para>
        /// </remarks>
        public const float  EYE_HEIGHT_CROUCHED = 0.88f;

        /// <summary>
        /// How far a full lean moves the eye to the side, in metres. The lean's shot origin.
        /// </summary>
        /// <remarks>
        /// <c>PlayerFpParent.LateUpdate</c> moves the FP camera <c>0.4 * lean</c> along its own
        /// right vector, stopping short of a wall a 0.3 m sphere would touch. The server offsets
        /// a leaning shot the same way (<c>ServerCombatAuthority.ShotOrigin</c>) — without it
        /// every shot taken while leaning left from 40 cm to the side of the crosshair.
        /// </remarks>
        public const float  LEAN_EYE_OFFSET = 0.4f;

        /// <summary>
        /// A seated player's eye above the seat, in metres, along the seat's own up.
        /// </summary>
        /// <remarks>
        /// <c>FpsActorController.StartSeated</c> parents the FP camera to the seat at
        /// <c>Vector3.up * 0.85f + Vector3.forward * 0.2f</c>, and a seated body sits on the seat's
        /// own transform (<c>Actor.EnterSeat</c>, local zero). The server fires a seated player's
        /// carried weapon from there; <c>SeatedEyeTests</c> pins these to that line.
        /// </remarks>
        public const float  SEATED_EYE_HEIGHT = 0.85f;

        /// <summary>
        /// A seated player's eye ahead of the seat, in metres, along the seat's own forward. See
        /// <see cref="SEATED_EYE_HEIGHT"/>.
        /// </summary>
        public const float  SEATED_EYE_FORWARD = 0.2f;
    }
}
