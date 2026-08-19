using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// <c>C_VEHICLE_INPUT</c> (0x21), channel 3. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No frame redundancy and no batching, unlike <see cref="ClientInputMessage"/>.</b>
    /// <c>C_INPUT</c> repeats three frames because a lost frame costs a tick of movement AND a
    /// button edge. Vehicle axes are continuous and level-triggered — a lost throttle frame is
    /// corrected by the next one 33 ms later. The one genuinely edge-triggered vehicle action,
    /// leaving a seat, travels on <see cref="SeatRequestMessage"/>, which is reliable.
    /// </para>
    /// <para>
    /// <b><see cref="VehicleId"/> is not redundant</b>, even though the server knows which seat
    /// the sender is in. It lets the server discard input addressed at a vehicle the client has
    /// already left — precisely the window a same-frame leave-then-enter opens.
    /// </para>
    /// <para>
    /// <b><see cref="TurretPitch"/> is an i16 here and an i8 in the snapshot entry.</b> Input is
    /// what the player asked for and deserves full <see cref="Quantize.PackPitch"/> precision;
    /// the snapshot is what the world looks like. The same asymmetry already exists between
    /// <c>C_INPUT</c> and <see cref="SnapshotField.Rotation"/>.
    /// </para>
    /// </remarks>
    public readonly struct VehicleInputMessage
    {
        /// <summary>u32 + u16 + i8 x 4 + u16 + i16 + u16 = 16 bytes.</summary>
        public const int Size = 16;

        /// <summary>Client tick this input was sampled at.</summary>
        public readonly uint Tick;
        /// <summary>The vehicle the sender believes it is controlling. Never 0.</summary>
        public readonly ushort VehicleId;

        /// <summary>Forward/back. <see cref="Quantize.PackMoveAxis"/>.</summary>
        public readonly sbyte Throttle;
        /// <summary>Left/right.</summary>
        public readonly sbyte Steer;
        /// <summary>Collective / nose pitch. Unused by ground vehicles.</summary>
        public readonly sbyte PitchAxis;
        /// <summary>Fourth axis: rudder, or handbrake analogue, per <see cref="VehicleKind"/>.</summary>
        public readonly sbyte AuxAxis;

        /// <summary>Turret aim yaw. <see cref="Quantize.PackYaw"/>.</summary>
        public readonly ushort TurretYaw;
        /// <summary>Turret aim pitch. <see cref="Quantize.PackPitch"/>.</summary>
        public readonly short TurretPitch;

        /// <summary>
        /// Held states only — fire, handbrake, horn, lights. There is no edge-triggered bit.
        /// </summary>
        public readonly ushort Buttons;

        public VehicleInputMessage(
            uint tick, ushort vehicleId,
            sbyte throttle, sbyte steer, sbyte pitchAxis, sbyte auxAxis,
            ushort turretYaw, short turretPitch, ushort buttons)
        {
            Tick        = tick;
            VehicleId   = vehicleId;
            Throttle    = throttle;
            Steer       = steer;
            PitchAxis   = pitchAxis;
            AuxAxis     = auxAxis;
            TurretYaw   = turretYaw;
            TurretPitch = turretPitch;
            Buttons     = buttons;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU32(Tick);
            w.WriteU16(VehicleId);
            w.WriteI8(Throttle);
            w.WriteI8(Steer);
            w.WriteI8(PitchAxis);
            w.WriteI8(AuxAxis);
            w.WriteU16(TurretYaw);
            w.WriteI16(TurretPitch);
            w.WriteU16(Buttons);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out VehicleInputMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            uint tick        = r.ReadU32();
            ushort vehicleId = r.ReadU16();
            sbyte throttle   = r.ReadI8();
            sbyte steer      = r.ReadI8();
            sbyte pitchAxis  = r.ReadI8();
            sbyte auxAxis    = r.ReadI8();
            ushort yaw       = r.ReadU16();
            short pitch      = r.ReadI16();
            ushort buttons   = r.ReadU16();
            if (!r.Ok) return false;

            message = new VehicleInputMessage(
                tick, vehicleId, throttle, steer, pitchAxis, auxAxis, yaw, pitch, buttons);
            return true;
        }
    }

    /// <summary>
    /// <c>C_SEAT_REQUEST</c> (0x26), channel 2. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// Reserved at the freeze and unimplemented until now — the router counted it as an unknown
    /// message type. It is reliable because leaving a seat is edge-triggered: a dropped request
    /// leaves the player welded into a vehicle with no second chance to ask.
    /// </remarks>
    public readonly struct SeatRequestMessage
    {
        /// <summary>u16 + u8 + u8 = 4 bytes.</summary>
        public const int Size = 4;

        public readonly ushort VehicleId;
        public readonly byte SeatIndex;
        public readonly SeatAction Action;

        public SeatRequestMessage(ushort vehicleId, byte seatIndex, SeatAction action)
        {
            VehicleId = vehicleId;
            SeatIndex = seatIndex;
            Action    = action;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(VehicleId);
            w.WriteU8(SeatIndex);
            w.WriteU8((byte)Action);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out SeatRequestMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort vehicleId = r.ReadU16();
            byte seatIndex   = r.ReadU8();
            byte action      = r.ReadU8();
            if (!r.Ok) return false;

            // Range-checked rather than cast. An unchecked cast makes EVERY byte except 1 an
            // Enter — so a corrupt or hostile action byte becomes a seat-entry request and is
            // counted as a well-formed message, blinding MalformedMessages, which exists to
            // surface exactly this. No authority is bypassed (the arbiter still runs every
            // check); what is lost is the ability to see that it happened.
            if (action > (byte)SeatAction.Leave) return false;

            message = new SeatRequestMessage(vehicleId, seatIndex, (SeatAction)action);
            return true;
        }
    }

    /// <summary>
    /// <c>S_VEHICLE_SPAWN</c> (0x4D), channel 2. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// <b>Two type fields, deliberately.</b> <see cref="Kind"/> is the four-way physics family
    /// that tells a decoder how to read the snapshot entry's 2-byte subtype tail;
    /// <see cref="NetworkTypeId"/> is the <see cref="VehicleIds"/> entry that decides which
    /// prefab to instantiate. Collapsing them would make adding a second tank model a wire
    /// change — and a decoder that has never heard of the new model would then be unable to
    /// read the tail of every vehicle behind it in the datagram.
    /// </remarks>
    public readonly struct VehicleSpawnMessage
    {
        /// <summary>u16 + u8 + u8 + i16 x 3 + u32 + u8 + u8 = 16 bytes.</summary>
        public const int Size = 16;

        public readonly ushort VehicleId;
        public readonly VehicleKind Kind;
        /// <summary>A <see cref="VehicleIds"/> value. 0 is unknown and never assigned.</summary>
        public readonly byte NetworkTypeId;
        /// <summary>Quantized spawn position (<see cref="Quantize.PackPos"/>).</summary>
        public readonly short PosX, PosY, PosZ;
        /// <summary>Packed spawn rotation (<see cref="Quantize.PackQuat"/>).</summary>
        public readonly uint Rotation;
        public readonly byte SeatCount;
        /// <summary>Reserved for per-spawn flags. Zero in v3.0.0.</summary>
        public readonly byte Flags;

        public VehicleSpawnMessage(
            ushort vehicleId, VehicleKind kind, byte networkTypeId,
            short posX, short posY, short posZ, uint rotation, byte seatCount, byte flags)
        {
            VehicleId     = vehicleId;
            Kind          = kind;
            NetworkTypeId = networkTypeId;
            PosX          = posX;
            PosY          = posY;
            PosZ          = posZ;
            Rotation      = rotation;
            SeatCount     = seatCount;
            Flags         = flags;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(VehicleId);
            w.WriteU8((byte)Kind);
            w.WriteU8(NetworkTypeId);
            w.WriteI16(PosX);
            w.WriteI16(PosY);
            w.WriteI16(PosZ);
            w.WriteU32(Rotation);
            w.WriteU8(SeatCount);
            w.WriteU8(Flags);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out VehicleSpawnMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort vehicleId = r.ReadU16();
            byte kind        = r.ReadU8();
            byte typeId      = r.ReadU8();
            short x = r.ReadI16(), y = r.ReadI16(), z = r.ReadI16();
            uint rotation    = r.ReadU32();
            byte seatCount   = r.ReadU8();
            byte flags       = r.ReadU8();
            if (!r.Ok) return false;

            message = new VehicleSpawnMessage(
                vehicleId, (VehicleKind)kind, typeId, x, y, z, rotation, seatCount, flags);
            return true;
        }
    }

    /// <summary><c>S_VEHICLE_DESPAWN</c> (0x4E), channel 2. protocol-spec.md section 4.10.</summary>
    public readonly struct VehicleDespawnMessage
    {
        /// <summary>u16 + u8 = 3 bytes.</summary>
        public const int Size = 3;

        public readonly ushort VehicleId;
        public readonly VehicleDespawnReason Reason;

        public VehicleDespawnMessage(ushort vehicleId, VehicleDespawnReason reason)
        {
            VehicleId = vehicleId;
            Reason    = reason;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(VehicleId);
            w.WriteU8((byte)Reason);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out VehicleDespawnMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort vehicleId = r.ReadU16();
            byte reason      = r.ReadU8();
            if (!r.Ok) return false;

            message = new VehicleDespawnMessage(vehicleId, (VehicleDespawnReason)reason);
            return true;
        }
    }

    /// <summary>
    /// <c>S_PROJECTILE_SPAWN</c> (0x4F), channel 2. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V7 gave this message a projectile id, a remaining-lifetime byte and a narrower
    /// tick.</b> V3 declared it 19 bytes with no id, reasoning that clients simulate flight
    /// from the parameters and detonation arrives separately in <c>S_EXPLOSION</c>, so nothing
    /// needed to correlate the two. That was true for the only projectile V3 had in view — a
    /// tank shell, launched once and never spoken of again. It is false for the two V7 adds:
    /// a guided missile is <b>re-parameterized</b> at 5 Hz (V7-D6) and a deployable
    /// re-announces while it tumbles (V7-D8), and without an id every re-announce is a second
    /// projectile. The id is what makes a repeat a correction instead of a duplicate.
    /// </para>
    /// <para>
    /// <b><see cref="RemainingLifetimeDeciseconds"/> exists for exactly one projectile.</b>
    /// Every other lifetime is derivable from <see cref="SpawnTick"/> plus an authored
    /// constant. A <c>Medipack</c> subtracts five seconds from its own life per successful
    /// heal (<c>Medipack.cs:26-29</c>), which no client can predict, so the server has to say
    /// so. 0.1 s resolution, 25.5 s ceiling — comfortably above the longest authored lifetime.
    /// </para>
    /// <para>
    /// <b><see cref="SpawnTick"/> is the low 16 bits of the server tick, not the full 32.</b>
    /// The only thing a receiver computes from it is the projectile's age, and at
    /// <see cref="ProtocolConstants.SIM_TICK_RATE"/> a <c>u16</c> spans 36 minutes — three
    /// orders of magnitude past the longest flight. Reconstruct with
    /// <c>SequenceMath.Distance(nowTick16, SpawnTick)</c>, which is wrap-correct for any age
    /// under 18 minutes. Two bytes on a message that fires per shot, per re-announce and per
    /// missile update is worth more than range nothing reads.
    /// </para>
    /// <para>
    /// 20 bytes. The design of record estimated "~16 B"; the estimate is not the pinned table,
    /// and the difference is graded by the bandwidth criterion rather than argued here.
    /// </para>
    /// </remarks>
    public readonly struct ProjectileSpawnMessage
    {
        /// <summary>u16 + u16 + u8 + i16 x 3 + i16 x 3 + u16 + u8 = 20 bytes.</summary>
        public const int Size = 20;

        /// <summary>
        /// Correlates a re-announce with the projectile it corrects (V7-D6, V7-D8). A receiver
        /// that already holds this id re-seats that projectile instead of spawning a second.
        /// </summary>
        public readonly ushort ProjectileId;

        /// <summary>Who fired it, for attribution in the killfeed and for self-hit exclusion.</summary>
        public readonly ushort OwnerActorId;
        public readonly ProjectileKind Kind;
        /// <summary>Launch point (<see cref="Quantize.PackPos"/>).</summary>
        public readonly short OriginX, OriginY, OriginZ;
        /// <summary>Launch velocity (<see cref="Quantize.PackVel16"/>).</summary>
        public readonly short VelX, VelY, VelZ;
        /// <summary>Low 16 bits of the server tick of the launch, so a late receiver can advance the flight.</summary>
        public readonly ushort SpawnTick;

        /// <summary>
        /// Life left at send time, in tenths of a second, saturating at 25.5 s. Zero means the
        /// projectile is expiring on this tick.
        /// </summary>
        public readonly byte RemainingLifetimeDeciseconds;

        public ProjectileSpawnMessage(
            ushort projectileId, ushort ownerActorId, ProjectileKind kind,
            short originX, short originY, short originZ,
            short velX, short velY, short velZ,
            ushort spawnTick, byte remainingLifetimeDeciseconds)
        {
            ProjectileId = projectileId;
            OwnerActorId = ownerActorId;
            Kind         = kind;
            OriginX      = originX;
            OriginY      = originY;
            OriginZ      = originZ;
            VelX         = velX;
            VelY         = velY;
            VelZ         = velZ;
            SpawnTick    = spawnTick;
            RemainingLifetimeDeciseconds = remainingLifetimeDeciseconds;
        }

        /// <summary>
        /// Deciseconds for a remaining lifetime in seconds, clamped into the byte. Shared so
        /// the writer and every test round-trip through one conversion rather than each
        /// rediscovering the saturation rule.
        /// </summary>
        public static byte PackRemainingLifetime(float seconds)
        {
            if (seconds <= 0f) return 0;

            float tenths = seconds * 10f;
            return tenths >= 255f ? (byte)255 : (byte)tenths;
        }

        /// <summary>Inverse of <see cref="PackRemainingLifetime"/>.</summary>
        public static float UnpackRemainingLifetime(byte deciseconds) => deciseconds * 0.1f;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(ProjectileId);
            w.WriteU16(OwnerActorId);
            w.WriteU8((byte)Kind);
            w.WriteI16(OriginX);
            w.WriteI16(OriginY);
            w.WriteI16(OriginZ);
            w.WriteI16(VelX);
            w.WriteI16(VelY);
            w.WriteI16(VelZ);
            w.WriteU16(SpawnTick);
            w.WriteU8(RemainingLifetimeDeciseconds);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ProjectileSpawnMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort projectileId = r.ReadU16();
            ushort owner = r.ReadU16();
            byte kind    = r.ReadU8();
            short ox = r.ReadI16(), oy = r.ReadI16(), oz = r.ReadI16();
            short vx = r.ReadI16(), vy = r.ReadI16(), vz = r.ReadI16();
            ushort spawnTick = r.ReadU16();
            byte remainingLifetime = r.ReadU8();
            if (!r.Ok) return false;

            message = new ProjectileSpawnMessage(
                projectileId, owner, (ProjectileKind)kind, ox, oy, oz, vx, vy, vz,
                spawnTick, remainingLifetime);
            return true;
        }
    }

    /// <summary>
    /// <c>S_SEAT_CHANGE</c> (0x50), channel 2. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// The authoritative answer to every <see cref="SeatRequestMessage"/>, including the ones
    /// that were refused. Without it a rejection has no path home and the client's predicted
    /// seat entry never gets corrected.
    /// </remarks>
    public readonly struct SeatChangeMessage
    {
        /// <summary>u16 + u16 + u8 + u8 = 6 bytes.</summary>
        public const int Size = 6;

        public readonly ushort ActorId;
        /// <summary>0 when the actor is on foot — the same "no vehicle" sentinel the snapshot uses.</summary>
        public readonly ushort VehicleId;
        public readonly byte SeatIndex;
        public readonly SeatChangeResult Result;

        public SeatChangeMessage(
            ushort actorId, ushort vehicleId, byte seatIndex, SeatChangeResult result)
        {
            ActorId   = actorId;
            VehicleId = vehicleId;
            SeatIndex = seatIndex;
            Result    = result;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(ActorId);
            w.WriteU16(VehicleId);
            w.WriteU8(SeatIndex);
            w.WriteU8((byte)Result);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out SeatChangeMessage message)
        {
            message = default;
            var r = new SpanReader(src);
            ushort actorId   = r.ReadU16();
            ushort vehicleId = r.ReadU16();
            byte seatIndex   = r.ReadU8();
            byte result      = r.ReadU8();
            if (!r.Ok) return false;

            message = new SeatChangeMessage(
                actorId, vehicleId, seatIndex, (SeatChangeResult)result);
            return true;
        }
    }
}
