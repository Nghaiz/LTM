using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// <c>S_VEHICLE_SNAPSHOT</c> changeMask bits. Bit i = 1 means field i is present in this
    /// entry. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>u16</c> rather than the actor entry's <c>u8</c>, and deliberately only half used.
    /// <see cref="SnapshotField"/> allocated all 8 of its bits before the first vehicle
    /// existed, so a ninth actor field needs the mask itself widened. Starting at 8 of 16 means a
    /// new vehicle field takes a spare bit in a mask that is already the right width.
    /// </para>
    /// <para>
    /// <b>That is cheaper, not free.</b> Adding a field is still a wire change and still bumps
    /// <see cref="ProtocolConstants.PROTOCOL_VERSION"/>: an old decoder reaching an unknown bit
    /// does not know the new field's width, so it cannot skip it, and every later field — and
    /// every later entry in the datagram — misaligns behind it. The spare bits buy a smaller
    /// diff and no re-layout of the mask; they do not buy backward compatibility. The only thing
    /// here an old decoder genuinely survives is an unknown <see cref="VehicleKind"/>, because
    /// the subtype tail is a fixed 2 bytes whatever the kind turns out to be.
    /// </para>
    /// <para>
    /// The bit order mirrors the field order on the wire, so a reader walking the bits
    /// low-to-high walks the bytes front-to-back.
    /// </para>
    /// </remarks>
    [Flags]
    public enum VehicleField : ushort
    {
        None = 0,

        /// <summary>i16 x 3, quantized position (<see cref="Quantize.PackPos"/>). 6 bytes.</summary>
        Position        = 1 << 0,

        /// <summary>
        /// u32 smallest-three quaternion (<see cref="Quantize.PackQuat"/>). 4 bytes.
        /// </summary>
        /// <remarks>
        /// A full rotation rather than the actor entry's yaw + pitch, because vehicles roll
        /// and yaw + pitch cannot express roll at all.
        /// </remarks>
        Rotation        = 1 << 1,

        /// <summary>
        /// i16 x 3, quantized linear velocity (<see cref="Quantize.PackVel16"/>). 6 bytes.
        /// </summary>
        /// <remarks>
        /// i16 rather than the actor entry's i8: <see cref="Quantize.VEL_MAX"/> saturates at
        /// 64 m/s, which a helicopter passes in level flight.
        /// </remarks>
        LinearVelocity  = 1 << 2,

        /// <summary>i8 x 3, angular velocity. 3 bytes.</summary>
        AngularVelocity = 1 << 3,

        /// <summary>u8, health normalized against the vehicle's own maxHealth. 1 byte.</summary>
        Health          = 1 << 4,

        /// <summary>u8 <see cref="VehicleStateFlags"/>. 1 byte.</summary>
        Flags           = 1 << 5,

        /// <summary>u16 turret yaw + i8 turret pitch. 3 bytes.</summary>
        Turret          = 1 << 6,

        /// <summary>
        /// The fixed 2-byte subtype tail, read according to the entry's
        /// <see cref="VehicleKind"/>. 2 bytes.
        /// </summary>
        Subtype         = 1 << 7,

        // Bits 8..15 reserved. A new vehicle field takes bit 8 and is NOT a mask widening.

        /// <summary>
        /// Every field a vehicle has. Unlike <see cref="SnapshotField.FullNoSeat"/> there is no
        /// opt-out variant: every bit here is a field every vehicle genuinely carries, so a
        /// vehicle the client has never seen gets all of them.
        /// </summary>
        Full = Position | Rotation | LinearVelocity | AngularVelocity
             | Health | Flags | Turret | Subtype,
    }

    /// <summary>
    /// Per-vehicle flags byte inside a vehicle snapshot entry. protocol-spec.md section 4.10.
    /// </summary>
    [Flags]
    public enum VehicleStateFlags : byte
    {
        None      = 0,
        /// <summary>Destroyed. The wreck, if any, is the client's to render.</summary>
        Dead      = 1 << 0,
        Burning   = 1 << 1,
        InWater   = 1 << 2,
        /// <summary>No wheel or hull in contact with the ground.</summary>
        Airborne  = 1 << 3,
        // Bits 4..7 reserved.
    }

    /// <summary>
    /// The physics family a vehicle belongs to. Decides how the 2-byte subtype tail of a
    /// snapshot entry is read (protocol-spec.md section 4.10).
    /// </summary>
    /// <remarks>
    /// Four values because the game has exactly four <c>Vehicle</c> subclasses. This is NOT
    /// the prefab identity — that is <see cref="VehicleIds"/>, carried alongside it in
    /// <see cref="VehicleSpawnMessage"/>. Collapsing the two would make adding a second tank
    /// model a wire change.
    /// </remarks>
    public enum VehicleKind : byte
    {
        Car        = 0,
        Tank       = 1,
        Helicopter = 2,
        Boat       = 3,
        // A kind this build does not know costs 2 skipped tail bytes and nothing else.
    }

    /// <summary>What a <c>C_SEAT_REQUEST</c> is asking for.</summary>
    public enum SeatAction : byte
    {
        Enter = 0,
        Leave = 1,
    }

    /// <summary>
    /// The server's answer to a seat request, carried by <c>S_SEAT_CHANGE</c>.
    /// </summary>
    /// <remarks>
    /// Rejections are values rather than silence. A dropped request and a refused one look
    /// identical to a client that only ever hears about success, and the client's own
    /// prediction has already seated the player by then.
    /// </remarks>
    public enum SeatChangeResult : byte
    {
        /// <summary>Granted; the actor is now in that seat.</summary>
        Entered            = 0,
        /// <summary>Granted; the actor is now on foot.</summary>
        Left               = 1,
        /// <summary>Somebody else is already in it (<c>Seat.IsOccupied</c>).</summary>
        RejectedOccupied   = 2,
        /// <summary>The vehicle is destroyed (<c>Vehicle.dead</c>).</summary>
        RejectedVehicleDead = 3,
        /// <summary>The actor is already seated (<c>Actor.CanEnterSeat</c>).</summary>
        RejectedAlreadySeated = 4,
        /// <summary>The actor is not close enough to reach it.</summary>
        RejectedTooFar     = 5,
        /// <summary>No such vehicle, or no such seat index on it.</summary>
        RejectedNoSuchSeat = 6,
    }

    /// <summary>What is being launched, carried by <c>S_PROJECTILE_SPAWN</c>.</summary>
    /// <remarks>
    /// One value per projectile class the game ships, because the client instantiates a
    /// different prefab and simulates a different flight model for each.
    /// </remarks>
    public enum ProjectileKind : byte
    {
        /// <summary>Tank main gun (<c>ShellLoadedWeapon</c>). Ballistic, unguided.</summary>
        Shell         = 0,
        /// <summary>Unguided rocket (<c>Rocket</c>).</summary>
        Rocket        = 1,
        /// <summary>Top-attack guided missile (<c>JavelinMissile</c>).</summary>
        GuidedMissile = 2,
        /// <summary>Thrown grenade (<c>GrenadeProjectile</c>).</summary>
        Grenade       = 3,
        /// <summary>Ammo box or medipack, thrown and landing inert.</summary>
        Supply        = 4,
    }

    /// <summary>Why a vehicle left the world. Carried by <c>S_VEHICLE_DESPAWN</c>.</summary>
    /// <remarks>
    /// <b>One enum, two readers.</b> This used to be declared a second time inside
    /// <c>Ironfront.Net.Replication.World</c> for phase-V8's spawner sink, which predated the
    /// wire. Two enums with the same name, the same values and different assemblies is the
    /// duplicate source of truth <c>development-principles.md</c> forbids, and it would drift
    /// the first time either side gained a reason. The values are V8's, unchanged, so the
    /// consolidation is a namespace move rather than a renumbering.
    /// </remarks>
    public enum VehicleDespawnReason : byte
    {
        /// <summary>Destroyed by damage. The wreck, if any, is the engine's business.</summary>
        Destroyed  = 0,

        /// <summary>Torn down between rounds, with the rest of the world.</summary>
        WorldReset = 1,
    }
}
