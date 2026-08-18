using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The fixed part of an <c>S_VEHICLE_SNAPSHOT</c> body. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// <b>There is no <c>lastProcessedInputTick</c> here, and that is a decision rather than an
    /// omission.</b> <see cref="SnapshotHeader"/> carries one because the actor path replays
    /// unacked inputs through <c>PredictionReconciler</c>. Driver prediction is
    /// error-corrected simulation, not input replay — the client never re-runs a vehicle tick,
    /// so it has nothing to reconcile against and the field would be 4 bytes at 20 Hz that
    /// nobody reads.
    /// </remarks>
    public readonly struct VehicleSnapshotHeader
    {
        /// <summary>u32 + u32 + u8 = 9 bytes.</summary>
        public const int Size = 9;

        /// <summary>Tick at which the server built this snapshot.</summary>
        public readonly uint ServerTick;
        /// <summary>0 = full snapshot; non-zero = delta against that snapshot tick.</summary>
        public readonly uint BaselineTick;
        public readonly byte VehicleCount;

        public VehicleSnapshotHeader(uint serverTick, uint baselineTick, byte vehicleCount)
        {
            ServerTick   = serverTick;
            BaselineTick = baselineTick;
            VehicleCount = vehicleCount;
        }

        public bool IsFullSnapshot => BaselineTick == 0;
    }

    /// <summary>
    /// One vehicle's slice of a vehicle snapshot. Only the fields flagged in
    /// <see cref="ChangeMask"/> are on the wire; the rest must be read from the baseline.
    /// </summary>
    /// <remarks>
    /// Mutable by design, exactly as <see cref="ActorSnapshotEntry"/> is: the parser fills
    /// these in place in a caller-owned array, so a 20 Hz stream produces no garbage.
    /// </remarks>
    public struct VehicleSnapshotEntry
    {
        /// <summary>
        /// Allocated from 1 out of a u16 space separate from <c>actorId</c>.
        /// <b>0 means "no vehicle"</b>, which is what <see cref="SnapshotField.SeatInfo"/>
        /// sends when an actor leaves a seat.
        /// </summary>
        public ushort VehicleId;
        public VehicleField ChangeMask;

        // VehicleField.Position
        public short PosX, PosY, PosZ;

        // VehicleField.Rotation — Quantize.PackQuat
        public uint Rotation;

        // VehicleField.LinearVelocity — Quantize.PackVel16
        public short VelX, VelY, VelZ;

        // VehicleField.AngularVelocity
        public sbyte AngVelX, AngVelY, AngVelZ;

        // VehicleField.Health — normalized against the vehicle's own maxHealth
        public byte Health;

        // VehicleField.Flags
        public VehicleStateFlags Flags;

        // VehicleField.Turret
        public ushort TurretYaw;
        public sbyte TurretPitch;

        /// <summary>
        /// First subtype-tail byte. Its meaning depends on the <see cref="VehicleKind"/> the
        /// client learned from <c>S_VEHICLE_SPAWN</c> — see protocol-spec.md section 4.10.
        /// </summary>
        public byte SubtypeA;
        /// <summary>Second subtype-tail byte. See <see cref="SubtypeA"/>.</summary>
        public byte SubtypeB;

        public bool Has(VehicleField field) => (ChangeMask & field) != 0;
    }

    /// <summary>
    /// <c>S_VEHICLE_SNAPSHOT</c> (0x4C) body codec. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vehicle counterpart of <see cref="SnapshotMessage"/>, and separate from it on
    /// purpose: a vehicle is not expressible as an actor entry. <see cref="SnapshotField"/> has
    /// all 8 bits spent, quantized actor velocity saturates at
    /// <see cref="Quantize.VEL_MAX"/>, and the actor rotation field is yaw + pitch with no
    /// roll.
    /// </para>
    /// <para>
    /// <b>Co-residency with the actor snapshot.</b> Both messages go in the same channel-1
    /// payload batch, this one written FIRST, and the actor snapshot takes the remainder of the
    /// datagram budget. The vehicle body is bounded at <see cref="MaxBodySize"/>; the actor
    /// body is elastic and already sheds. Sizing the elastic one against what the bounded one
    /// actually consumed is exact, where reserving a fixed slice would need
    /// unused-reserve-return logic for no gain. <b>This class declares the rule and the
    /// number; implementing the split belongs to whoever owns the payload writer.</b>
    /// </para>
    /// </remarks>
    public static class VehicleSnapshotMessage
    {
        /// <summary>vehicleId + changeMask, before any optional field.</summary>
        public const int EntryHeaderSize = 4;

        /// <summary>
        /// Encoded size of one entry carrying every field, arrived at by addition rather than
        /// asserted: 4 + 6 + 4 + 6 + 3 + 1 + 1 + 3 + 2 = 30.
        /// </summary>
        public const int FullEntrySize = EntryHeaderSize
            + 6   // Position         i16 x 3
            + 4   // Rotation         u32 smallest-three
            + 6   // LinearVelocity   i16 x 3
            + 3   // AngularVelocity  i8 x 3
            + 1   // Health           u8
            + 1   // Flags            u8
            + 3   // Turret           u16 + i8
            + 2;  // Subtype          fixed tail

        /// <summary>
        /// Worst case for the whole message: <see cref="ProtocolConstants.MAX_VEHICLES"/> full
        /// entries plus the header. 16 x 30 + 9 = 489.
        /// </summary>
        /// <remarks>
        /// This number is the entire reason <see cref="ProtocolConstants.MAX_VEHICLES"/> is
        /// capped. It leaves 689 bytes of the 1178-byte snapshot body for actors even when
        /// every vehicle in the world is visible and every field of every one has changed.
        /// </remarks>
        public const int MaxBodySize =
            ProtocolConstants.MAX_VEHICLES * FullEntrySize + VehicleSnapshotHeader.Size;

        /// <summary>Encoded size of one entry with the given mask.</summary>
        public static int EntrySize(VehicleField mask)
        {
            int size = EntryHeaderSize;
            if ((mask & VehicleField.Position)        != 0) size += 6;
            if ((mask & VehicleField.Rotation)        != 0) size += 4;
            if ((mask & VehicleField.LinearVelocity)  != 0) size += 6;
            if ((mask & VehicleField.AngularVelocity) != 0) size += 3;
            if ((mask & VehicleField.Health)          != 0) size += 1;
            if ((mask & VehicleField.Flags)           != 0) size += 1;
            if ((mask & VehicleField.Turret)          != 0) size += 3;
            if ((mask & VehicleField.Subtype)         != 0) size += 2;
            return size;
        }

        /// <summary>Total encoded size of a vehicle snapshot with these entries.</summary>
        public static int SizeFor(ReadOnlySpan<VehicleSnapshotEntry> entries)
        {
            int size = VehicleSnapshotHeader.Size;
            for (int i = 0; i < entries.Length; i++) size += EntrySize(entries[i].ChangeMask);
            return size;
        }

        /// <summary>
        /// Writes the message body. <paramref name="header"/>'s VehicleCount must match
        /// <paramref name="entries"/>.Length. Returns bytes written, or -1.
        /// </summary>
        public static int Write(
            Span<byte> dst,
            in VehicleSnapshotHeader header,
            ReadOnlySpan<VehicleSnapshotEntry> entries)
        {
            if (entries.Length > byte.MaxValue) return -1;
            if (header.VehicleCount != entries.Length) return -1;

            var w = new SpanWriter(dst);
            w.WriteU32(header.ServerTick);
            w.WriteU32(header.BaselineTick);
            w.WriteU8(header.VehicleCount);

            for (int i = 0; i < entries.Length; i++)
            {
                VehicleSnapshotEntry e = entries[i];
                VehicleField mask = e.ChangeMask;

                w.WriteU16(e.VehicleId);
                w.WriteU16((ushort)mask);

                if ((mask & VehicleField.Position) != 0)
                {
                    w.WriteI16(e.PosX); w.WriteI16(e.PosY); w.WriteI16(e.PosZ);
                }
                if ((mask & VehicleField.Rotation) != 0) w.WriteU32(e.Rotation);
                if ((mask & VehicleField.LinearVelocity) != 0)
                {
                    w.WriteI16(e.VelX); w.WriteI16(e.VelY); w.WriteI16(e.VelZ);
                }
                if ((mask & VehicleField.AngularVelocity) != 0)
                {
                    w.WriteI8(e.AngVelX); w.WriteI8(e.AngVelY); w.WriteI8(e.AngVelZ);
                }
                if ((mask & VehicleField.Health) != 0) w.WriteU8(e.Health);
                if ((mask & VehicleField.Flags)  != 0) w.WriteU8((byte)e.Flags);
                if ((mask & VehicleField.Turret) != 0)
                {
                    w.WriteU16(e.TurretYaw); w.WriteI8(e.TurretPitch);
                }
                if ((mask & VehicleField.Subtype) != 0)
                {
                    w.WriteU8(e.SubtypeA); w.WriteU8(e.SubtypeB);
                }
            }

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a vehicle snapshot body. <paramref name="entries"/> must have room for the
        /// encoded vehicle count — size it to
        /// <see cref="ProtocolConstants.MAX_VEHICLES"/> and reuse it across ticks.
        /// </summary>
        public static bool TryParse(
            ReadOnlySpan<byte> src,
            Span<VehicleSnapshotEntry> entries,
            out VehicleSnapshotHeader header,
            out int entryCount)
        {
            header     = default;
            entryCount = 0;

            var r = new SpanReader(src);
            uint serverTick   = r.ReadU32();
            uint baselineTick = r.ReadU32();
            byte vehicleCount = r.ReadU8();
            if (!r.Ok) return false;

            // Checked before any field is read, so a hostile count cannot walk off the end of
            // the caller's array one entry at a time.
            if (entries.Length < vehicleCount) return false;

            for (int i = 0; i < vehicleCount; i++)
            {
                ushort vehicleId = r.ReadU16();
                var mask = (VehicleField)r.ReadU16();
                if (!r.Ok) return false;

                var e = new VehicleSnapshotEntry { VehicleId = vehicleId, ChangeMask = mask };

                if ((mask & VehicleField.Position) != 0)
                {
                    e.PosX = r.ReadI16(); e.PosY = r.ReadI16(); e.PosZ = r.ReadI16();
                }
                if ((mask & VehicleField.Rotation) != 0) e.Rotation = r.ReadU32();
                if ((mask & VehicleField.LinearVelocity) != 0)
                {
                    e.VelX = r.ReadI16(); e.VelY = r.ReadI16(); e.VelZ = r.ReadI16();
                }
                if ((mask & VehicleField.AngularVelocity) != 0)
                {
                    e.AngVelX = r.ReadI8(); e.AngVelY = r.ReadI8(); e.AngVelZ = r.ReadI8();
                }
                if ((mask & VehicleField.Health) != 0) e.Health = r.ReadU8();
                if ((mask & VehicleField.Flags)  != 0) e.Flags  = (VehicleStateFlags)r.ReadU8();
                if ((mask & VehicleField.Turret) != 0)
                {
                    e.TurretYaw = r.ReadU16(); e.TurretPitch = r.ReadI8();
                }
                if ((mask & VehicleField.Subtype) != 0)
                {
                    e.SubtypeA = r.ReadU8(); e.SubtypeB = r.ReadU8();
                }

                if (!r.Ok) return false;
                entries[i] = e;
            }

            header     = new VehicleSnapshotHeader(serverTick, baselineTick, vehicleCount);
            entryCount = vehicleCount;
            return true;
        }
    }
}
