using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The fixed part of an S_SNAPSHOT body. protocol-spec.md section 4.3.
    /// </summary>
    public readonly struct SnapshotHeader
    {
        /// <summary>u32 + u32 + u32 + u8 = 13 bytes.</summary>
        public const int Size = 13;

        /// <summary>Tick at which the server built this snapshot.</summary>
        public readonly uint ServerTick;
        /// <summary>Last input tick the server applied for THIS client. Drives reconciliation.</summary>
        public readonly uint LastProcessedInputTick;
        /// <summary>0 = full snapshot; non-zero = delta against that snapshot tick.</summary>
        public readonly uint BaselineTick;
        public readonly byte ActorCount;

        public SnapshotHeader(
            uint serverTick, uint lastProcessedInputTick, uint baselineTick, byte actorCount)
        {
            ServerTick             = serverTick;
            LastProcessedInputTick = lastProcessedInputTick;
            BaselineTick           = baselineTick;
            ActorCount             = actorCount;
        }

        public bool IsFullSnapshot => BaselineTick == 0;
    }

    /// <summary>
    /// One actor's slice of a snapshot. Only the fields flagged in
    /// <see cref="ChangeMask"/> are on the wire; the rest keep their default value and
    /// must be read from the baseline by the caller.
    /// </summary>
    /// <remarks>
    /// Mutable by design — the parser fills these in place, in a caller-owned array, so a
    /// 20 Hz snapshot stream produces no garbage.
    /// </remarks>
    public struct ActorSnapshotEntry
    {
        public ushort ActorId;
        public SnapshotField ChangeMask;

        // Position — SnapshotField.Position
        public short PosX, PosY, PosZ;

        // Rotation — SnapshotField.Rotation
        public ushort Yaw;
        public sbyte Pitch;

        // Velocity — SnapshotField.Velocity
        public sbyte VelX, VelY, VelZ;

        // SnapshotField.StateFlags
        public ActorStateFlags StateFlags;

        // SnapshotField.Health
        public byte Health;

        // SnapshotField.Weapon
        public byte WeaponId;
        public byte AmmoInClip;

        // SnapshotField.Team
        public byte Team;

        // SnapshotField.SeatInfo (stretch goal)
        public ushort VehicleId;
        public byte SeatIndex;

        public bool Has(SnapshotField field) => (ChangeMask & field) != 0;
    }

    /// <summary>
    /// S_SNAPSHOT (0x40) body codec. protocol-spec.md section 4.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-actor cost: 20 bytes when every v1 field is present
    /// (<see cref="SnapshotField.FullNoSeat"/>), ~12 for a typical position+rotation
    /// delta, 3 for an actor that has not moved. At 48 actors averaging 12 bytes that is
    /// ~600 B per snapshot, ~12 KB/s at 20 Hz — and ~5-7 KB/s once interest management
    /// trims the set to the ~20 actors a client can actually see.
    /// </para>
    /// <para>
    /// A full 64-actor snapshot is 1293 bytes, over the 1184-byte payload limit, so it
    /// fragments. That is the expected case on join, not an error — see
    /// <see cref="Fragmenter"/>.
    /// </para>
    /// </remarks>
    public static class SnapshotMessage
    {
        /// <summary>actorId + changeMask, before any optional field.</summary>
        public const int EntryHeaderSize = 3;

        /// <summary>Encoded size of one entry with the given mask.</summary>
        public static int EntrySize(SnapshotField mask)
        {
            int size = EntryHeaderSize;
            if ((mask & SnapshotField.Position)   != 0) size += 6;
            if ((mask & SnapshotField.Rotation)   != 0) size += 3;
            if ((mask & SnapshotField.Velocity)   != 0) size += 3;
            if ((mask & SnapshotField.StateFlags) != 0) size += 1;
            if ((mask & SnapshotField.Health)     != 0) size += 1;
            if ((mask & SnapshotField.Weapon)     != 0) size += 2;
            if ((mask & SnapshotField.Team)       != 0) size += 1;
            if ((mask & SnapshotField.SeatInfo)   != 0) size += 3;
            return size;
        }

        /// <summary>Total encoded size of a snapshot with these entries.</summary>
        public static int SizeFor(ReadOnlySpan<ActorSnapshotEntry> entries)
        {
            int size = SnapshotHeader.Size;
            for (int i = 0; i < entries.Length; i++) size += EntrySize(entries[i].ChangeMask);
            return size;
        }

        /// <summary>
        /// Writes the message body. <paramref name="header"/>'s ActorCount must match
        /// <paramref name="entries"/>.Length. Returns bytes written, or -1.
        /// </summary>
        public static int Write(
            Span<byte> dst, in SnapshotHeader header, ReadOnlySpan<ActorSnapshotEntry> entries)
        {
            if (entries.Length > byte.MaxValue) return -1;
            if (header.ActorCount != entries.Length) return -1;

            var w = new SpanWriter(dst);
            w.WriteU32(header.ServerTick);
            w.WriteU32(header.LastProcessedInputTick);
            w.WriteU32(header.BaselineTick);
            w.WriteU8(header.ActorCount);

            for (int i = 0; i < entries.Length; i++)
            {
                ActorSnapshotEntry e = entries[i];
                SnapshotField mask = e.ChangeMask;

                w.WriteU16(e.ActorId);
                w.WriteU8((byte)mask);

                if ((mask & SnapshotField.Position) != 0)
                {
                    w.WriteI16(e.PosX); w.WriteI16(e.PosY); w.WriteI16(e.PosZ);
                }
                if ((mask & SnapshotField.Rotation) != 0)
                {
                    w.WriteU16(e.Yaw); w.WriteI8(e.Pitch);
                }
                if ((mask & SnapshotField.Velocity) != 0)
                {
                    w.WriteI8(e.VelX); w.WriteI8(e.VelY); w.WriteI8(e.VelZ);
                }
                if ((mask & SnapshotField.StateFlags) != 0) w.WriteU8((byte)e.StateFlags);
                if ((mask & SnapshotField.Health)     != 0) w.WriteU8(e.Health);
                if ((mask & SnapshotField.Weapon)     != 0)
                {
                    w.WriteU8(e.WeaponId); w.WriteU8(e.AmmoInClip);
                }
                if ((mask & SnapshotField.Team)     != 0) w.WriteU8(e.Team);
                if ((mask & SnapshotField.SeatInfo) != 0)
                {
                    w.WriteU16(e.VehicleId); w.WriteU8(e.SeatIndex);
                }
            }

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a snapshot body. <paramref name="entries"/> must have room for the
        /// encoded actor count — size it to <see cref="ProtocolConstants.MAX_ACTORS"/> and
        /// reuse it across ticks.
        /// </summary>
        public static bool TryParse(
            ReadOnlySpan<byte> src,
            Span<ActorSnapshotEntry> entries,
            out SnapshotHeader header,
            out int entryCount)
        {
            header     = default;
            entryCount = 0;

            var r = new SpanReader(src);
            uint serverTick   = r.ReadU32();
            uint lastInput    = r.ReadU32();
            uint baselineTick = r.ReadU32();
            byte actorCount   = r.ReadU8();
            if (!r.Ok) return false;

            if (entries.Length < actorCount) return false;

            for (int i = 0; i < actorCount; i++)
            {
                ushort actorId = r.ReadU16();
                var mask = (SnapshotField)r.ReadU8();
                if (!r.Ok) return false;

                var e = new ActorSnapshotEntry { ActorId = actorId, ChangeMask = mask };

                if ((mask & SnapshotField.Position) != 0)
                {
                    e.PosX = r.ReadI16(); e.PosY = r.ReadI16(); e.PosZ = r.ReadI16();
                }
                if ((mask & SnapshotField.Rotation) != 0)
                {
                    e.Yaw = r.ReadU16(); e.Pitch = r.ReadI8();
                }
                if ((mask & SnapshotField.Velocity) != 0)
                {
                    e.VelX = r.ReadI8(); e.VelY = r.ReadI8(); e.VelZ = r.ReadI8();
                }
                if ((mask & SnapshotField.StateFlags) != 0) e.StateFlags = (ActorStateFlags)r.ReadU8();
                if ((mask & SnapshotField.Health)     != 0) e.Health = r.ReadU8();
                if ((mask & SnapshotField.Weapon)     != 0)
                {
                    e.WeaponId = r.ReadU8(); e.AmmoInClip = r.ReadU8();
                }
                if ((mask & SnapshotField.Team)     != 0) e.Team = r.ReadU8();
                if ((mask & SnapshotField.SeatInfo) != 0)
                {
                    e.VehicleId = r.ReadU16(); e.SeatIndex = r.ReadU8();
                }

                if (!r.Ok) return false;
                entries[i] = e;
            }

            header     = new SnapshotHeader(serverTick, lastInput, baselineTick, actorCount);
            entryCount = actorCount;
            return true;
        }
    }
}
