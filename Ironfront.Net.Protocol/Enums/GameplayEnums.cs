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
        /// <summary>
        /// Deliberately unassigned. Was <c>ThrowGrenade</c>, declared at the freeze with zero
        /// producers and zero consumers repo-wide, and retired by phase-V7 D10 rather than
        /// implemented.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The game has no dedicated grenade input and never did: throwing is <i>switch to the
        /// gear slot, then Fire</i>, which routes through <c>Actor.SwitchWeapon</c> and
        /// <c>ThrowableWeapon.Fire</c> — a path V6 already made server-authoritative. Wiring
        /// this bit would add a <b>second route to firing</b> that does not pass
        /// <c>Weapon.CanFire()</c>, and a second route is the one nobody writes the rapid-fire
        /// test for.
        /// </para>
        /// <para>
        /// <b>Renaming is not a wire change.</b> No producer ever set the bit, so no packet's
        /// bytes move and <see cref="ProtocolConstants.PROTOCOL_VERSION"/> is unchanged. The
        /// value is kept rather than deleted so the neighbouring bits do not renumber.
        /// </para>
        /// </remarks>
        Reserved7     = 1 << 7,
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
        /// <summary>
        /// u16 vehicleId + u8 seatIndex. 3 bytes. <b>vehicleId 0 means "not seated"</b>, which
        /// is how leaving a vehicle is expressed on a field that is only sent on change.
        /// </summary>
        SeatInfo   = 1 << 7,

        /// <summary>
        /// Bits 0..6 — every field an actor on foot has. Still the right mask for an unseated
        /// actor: <see cref="SeatInfo"/> describes a relationship such an actor does not have,
        /// and claiming it would spend 3 bytes saying "no vehicle" on every actor in the game.
        /// Encodes to 20 bytes.
        /// </summary>
        FullNoSeat = Position | Rotation | Velocity | StateFlags | Health | Weapon | Team,

        /// <summary>
        /// All 8 bits — every field a SEATED actor has. Encodes to 23 bytes, which is the width
        /// <c>InterestManager</c> must project against, because the projection has to be the
        /// worst case rather than the common one.
        /// </summary>
        Full = FullNoSeat | SeatInfo,
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

        /// <summary>A display name was supplied and is not usable. Blank is NOT this: it is
        /// accepted, and the master falls back to the username.</summary>
        /// <remarks>
        /// Its own code rather than <see cref="WrongCredentials"/>, for the reason
        /// <see cref="TeamsWouldUnbalance"/> records: the client renders the refusal, and this
        /// one names a field the player can fix. Until 2026-09-03 a display-name problem was
        /// reported as WrongCredentials, so the register screen answered every attempt with
        /// "Wrong username or password." on a form where no credentials existed yet -- which
        /// sent the player to re-type a password that was never the problem.
        /// </remarks>
        InvalidDisplayName= 1005,

        /// <summary>
        /// The account exists, the password was RIGHT, and it is locked out after too many
        /// failed attempts. Carries <c>retryAfterSec</c> on <c>MSP_LOGIN_RES</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only ever returned to somebody who proved they own the account.</b> That is the
        /// whole answer to the username-enumeration objection this code raises. A lockout state
        /// with a name is a statement that the account exists, so it is withheld from anybody
        /// who has not supplied the correct password — a guesser sees <see cref="WrongCredentials"/>
        /// on every attempt against a locked account exactly as they do against one that does not
        /// exist, and learns nothing they could not learn before. The player typing their own
        /// password learns why the door is shut. There is no branch where the trade-off is paid.
        /// </para>
        /// <para>
        /// Until 2026-09-03 this was <see cref="WrongCredentials"/>, so ten fat-fingered
        /// attempts bought fifteen minutes during which the correct password was answered
        /// "Wrong username or password." — advice that sends the player to reset a password that
        /// was never wrong, and cannot work, because the reset does not clear the lock either.
        /// </para>
        /// </remarks>
        AccountLocked     = 1006,

        /// <summary>
        /// The account exists, the password was RIGHT, and it is banned. Withheld from a wrong
        /// password for the reason <see cref="AccountLocked"/> gives.
        /// </summary>
        AccountBanned     = 1007,

        RoomNotFound      = 2000,
        RoomFull          = 2001,
        WrongRoomPassword = 2002,
        MatchAlreadyStarted = 2003,
        AlreadyInAnotherRoom= 2004,
        /// <summary>
        /// The requested side change would leave the two sides differing by more than one.
        /// P16 3.5.
        /// </summary>
        /// <remarks>
        /// Its own code rather than <see cref="InternalServerError"/> because the client renders
        /// the refusal, and the player can act on this one: the other side has room again as
        /// soon as somebody joins or switches. A generic "internal error" would read as a bug
        /// and send them to a bug report instead of to the button.
        /// </remarks>
        TeamsWouldUnbalance = 2005,

        NoGameServerAvailable = 3000,
        GameServerNotResponding = 3001,

        /// <summary>
        /// The chat line was longer than <see cref="MspChatLimits.MaxTextCharacters"/>.
        /// </summary>
        /// <remarks>
        /// <b>Its own code because the player's next move differs.</b> Every chat refusal used to
        /// arrive as <see cref="RateLimited"/>, whose message is "wait and try again" — correct
        /// advice for flooding and useless for a long message, which is still too long after the
        /// wait. A player following it re-sent the same text and got the same sentence forever.
        /// </remarks>
        ChatMessageTooLong  = 4000,

        /// <summary>Nothing survived trimming and control-character stripping.</summary>
        ChatMessageEmpty    = 4001,

        /// <summary>The channel byte is not one <see cref="MspChatChannel"/> defines.</summary>
        ChatChannelInvalid  = 4002,

        /// <summary>
        /// A room-channel line from a sender who is in no room. Previously dropped in silence,
        /// which is indistinguishable from a delivery nobody answered.
        /// </summary>
        NotInARoom          = 4003,

        /// <summary>
        /// Over the per-player chat flood budget. This one genuinely IS "wait and try again".
        /// </summary>
        /// <remarks>
        /// <b>Separate from <see cref="RateLimited"/> because the two windows differ and the
        /// client can only phrase what the code tells it.</b> Chat flooding is five messages per
        /// ten seconds per player; the login budget is five attempts per sixty seconds per source
        /// address. One code for both forces a message that is wrong about one of them —
        /// and it was wrong about both, since it also carried every non-flood chat refusal.
        /// </remarks>
        ChatTooFast         = 4004,

        InternalServerError = 9000,

        /// <summary>
        /// Too many attempts inside the window. <c>MSP_LOGIN_RES</c> carries
        /// <c>retryAfterSec</c> with the seconds left; <c>MSP_ERROR_PUSH</c> carries the wait in
        /// its message.
        /// </summary>
        /// <remarks>
        /// The login budget is counted <b>per source address</b> over a 60-second window, so two
        /// people behind one home router share it. That is deliberate — it is the control against
        /// a brute-force attempt from one address, and splitting it per account would let an
        /// attacker buy a fresh budget per username they guess — but it means the message has to
        /// say <i>network</i> rather than <i>you</i>, or the second person in the house reads a
        /// true statement as a lie.
        /// </remarks>
        RateLimited         = 9001,
    }
}
