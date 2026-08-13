using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Serialization;

namespace Ironfront.Net.Replication.Tests.Experiments
{
    /// <summary>
    /// A snapshot codec whose format is controlled by a <see cref="ReplicationConfig"/>, so
    /// phase-04 task 1 can measure each compression technique in isolation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This deliberately lives in the test project and is deliberately never shipped.</b>
    /// Two of the techniques the experiment table asks for — bit-packing and a 12-bit height —
    /// are changes to the byte layout that protocol-spec.md section 4.3 froze at v1. Putting
    /// them behind a runtime flag in the real encoder would mean a server that can emit a
    /// format no client is required to understand, which is the unannounced wire-format change
    /// phase 01 already declined to ship. Here both ends of the wire are this file, so each
    /// technique gets a real measured number and the shipped path stays frozen.
    /// </para>
    /// <para>
    /// It is a measuring instrument, not a second encoder: the shipped
    /// <see cref="SnapshotMessage"/> path is what the "byte-aligned" configuration is compared
    /// against, and <c>ByteAlignedMatchesTheShippedEncoder</c> pins that this codec agrees with
    /// it byte for byte in that configuration. Without that check the whole table would be
    /// measuring a codec nobody uses.
    /// </para>
    /// </remarks>
    internal static class ExperimentalSnapshotCodec
    {
        /// <summary>Bits used for the compact height field. 12 bits over the +/-2048 m range.</summary>
        public const int CompactHeightBits = 12;

        /// <summary>Maximum height representable by the compact field, in metres.</summary>
        /// <remarks>
        /// 12 bits at the spec's 6.25 cm position resolution covers 256 m, which is the reason
        /// the technique is proposed at all: no shipping map is 512 m tall. It is also the
        /// reason it is a format change and not a tuning knob — an actor above 256 m would be
        /// clamped, silently.
        /// </remarks>
        public const float CompactHeightRange = 256f;

        /// <summary>
        /// Encodes a snapshot. Returns bytes written, or -1.
        /// </summary>
        /// <param name="baseline">
        /// The client's acked baseline, or null for a full snapshot. Delta encoding is skipped
        /// entirely when <see cref="ReplicationConfig.UseDeltaEncoding"/> is off, which is what
        /// the table's first row measures.
        /// </param>
        /// <param name="viewerPosition">
        /// Where the receiving client is, for the distant-pitch test. Ignored when that flag is
        /// off.
        /// </param>
        public static int Write(
            Span<byte> destination,
            WorldSnapshot current,
            WorldSnapshot? baseline,
            in Vec3 viewerPosition,
            ReplicationConfig config)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (config == null) throw new ArgumentNullException(nameof(config));

            var writer = new BitWriter(destination);

            writer.WriteUInt32(current.ServerTick);
            writer.WriteUInt32(baseline?.ServerTick ?? 0u);
            writer.WriteByte((byte)current.ActorCount);

            for (int i = 0; i < current.ActorCount; i++)
            {
                ActorSnapshotEntry entry = current.Actors[i];

                SnapshotField mask;
                if (config.UseDeltaEncoding && baseline != null
                    && baseline.TryFind(entry.ActorId, out ActorSnapshotEntry previous))
                {
                    mask = DeltaEncoder.ComputeChangeMask(in previous, in entry);
                }
                else
                {
                    mask = SnapshotField.FullNoSeat;
                }

                if (config.UseVelocityCulling && !IsNear(in entry, in viewerPosition))
                    mask &= ~SnapshotField.Velocity;

                writer.WriteUInt16(entry.ActorId);
                writer.WriteByte((byte)mask);

                if ((mask & SnapshotField.Position) != 0)
                {
                    writer.WriteInt16(entry.PosX);
                    WriteHeight(ref writer, entry.PosY, config);
                    writer.WriteInt16(entry.PosZ);
                }

                if ((mask & SnapshotField.Rotation) != 0)
                {
                    writer.WriteUInt16(entry.Yaw);

                    // Pitch is a separate field here, which it is NOT on the frozen wire — yaw
                    // and pitch share one change-mask bit there, so suppressing pitch alone is
                    // inexpressible. That is exactly why this technique is measured here and
                    // reported as a format change rather than shipped as a flag.
                    bool sendPitch = !config.UseDistantPitchCulling
                                     || IsWithin(in entry, in viewerPosition, config.DistantPitchMetres);
                    writer.WriteBool(sendPitch);
                    if (sendPitch) writer.WriteSByte(entry.Pitch);
                }

                if ((mask & SnapshotField.Velocity) != 0)
                {
                    writer.WriteSByte(entry.VelX);
                    writer.WriteSByte(entry.VelY);
                    writer.WriteSByte(entry.VelZ);
                }

                if ((mask & SnapshotField.StateFlags) != 0) writer.WriteByte((byte)entry.StateFlags);
                if ((mask & SnapshotField.Health) != 0) writer.WriteByte(entry.Health);

                if ((mask & SnapshotField.Weapon) != 0)
                {
                    writer.WriteByte(entry.WeaponId);
                    writer.WriteByte(entry.AmmoInClip);
                }

                if ((mask & SnapshotField.Team) != 0) writer.WriteByte(entry.Team);

                // Byte alignment is the DEFAULT, not the optimization: the frozen format is
                // byte-aligned, so the baseline row has to pay for the padding that the
                // bit-packed rows save.
                if (!config.UseBitPacking) writer.AlignToByte();
            }

            if (!writer.Ok) return -1;
            writer.AlignToByte();
            return writer.BytesWritten;
        }

        /// <summary>
        /// Decodes into <paramref name="destination"/>, seeding each entry from the baseline so
        /// an omitted field means "unchanged" rather than "zero" (phase-01 trap 5).
        /// </summary>
        public static bool TryRead(
            ReadOnlySpan<byte> source,
            WorldSnapshot destination,
            WorldSnapshot? baseline,
            ReplicationConfig config)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (config == null) throw new ArgumentNullException(nameof(config));

            var reader = new BitReader(source);

            uint serverTick = reader.ReadUInt32();
            reader.ReadUInt32();                       // baselineTick, echoed for diagnostics
            int actorCount = reader.ReadByte();
            if (!reader.Ok) return false;

            destination.Clear();
            destination.ServerTick = serverTick;

            for (int i = 0; i < actorCount; i++)
            {
                ushort actorId = reader.ReadUInt16();
                var mask = (SnapshotField)reader.ReadByte();
                if (!reader.Ok) return false;

                ActorSnapshotEntry entry = default;
                if (baseline != null && baseline.TryFind(actorId, out ActorSnapshotEntry previous))
                    entry = previous;

                entry.ActorId = actorId;
                entry.ChangeMask = mask;

                if ((mask & SnapshotField.Position) != 0)
                {
                    entry.PosX = reader.ReadInt16();
                    entry.PosY = ReadHeight(ref reader, config);
                    entry.PosZ = reader.ReadInt16();
                }

                if ((mask & SnapshotField.Rotation) != 0)
                {
                    entry.Yaw = reader.ReadUInt16();
                    if (reader.ReadBool()) entry.Pitch = reader.ReadSByte();
                }

                if ((mask & SnapshotField.Velocity) != 0)
                {
                    entry.VelX = reader.ReadSByte();
                    entry.VelY = reader.ReadSByte();
                    entry.VelZ = reader.ReadSByte();
                }

                if ((mask & SnapshotField.StateFlags) != 0)
                    entry.StateFlags = (ActorStateFlags)reader.ReadByte();
                if ((mask & SnapshotField.Health) != 0) entry.Health = reader.ReadByte();

                if ((mask & SnapshotField.Weapon) != 0)
                {
                    entry.WeaponId = reader.ReadByte();
                    entry.AmmoInClip = reader.ReadByte();
                }

                if ((mask & SnapshotField.Team) != 0) entry.Team = reader.ReadByte();

                if (!config.UseBitPacking) reader.AlignToByte();
                if (!reader.Ok) return false;

                destination.Add(in entry);
            }

            return true;
        }

        private static void WriteHeight(ref BitWriter writer, short packedY, ReplicationConfig config)
        {
            if (!config.UseCompactHeight)
            {
                writer.WriteInt16(packedY);
                return;
            }

            // Biased to unsigned so the top bits can simply be dropped. Clamping rather than
            // wrapping: an actor above the range should sit on the ceiling of the representable
            // world, not teleport to its floor.
            float metres = Quantize.UnpackPos(packedY);
            float clamped = metres < 0f ? 0f
                : metres > CompactHeightRange ? CompactHeightRange
                : metres;

            var q = (uint)(clamped / CompactHeightRange * ((1 << CompactHeightBits) - 1));
            writer.WriteBits(q, CompactHeightBits);
        }

        private static short ReadHeight(ref BitReader reader, ReplicationConfig config)
        {
            if (!config.UseCompactHeight) return reader.ReadInt16();

            uint q = reader.ReadBits(CompactHeightBits);
            float metres = (float)q / ((1 << CompactHeightBits) - 1) * CompactHeightRange;
            return Quantize.PackPos(metres);
        }

        private static bool IsNear(in ActorSnapshotEntry entry, in Vec3 viewerPosition)
            => IsWithin(in entry, in viewerPosition, Interest.InterestManager.NearRadius);

        private static bool IsWithin(in ActorSnapshotEntry entry, in Vec3 viewerPosition, float metres)
        {
            Vec3 position = SnapshotBuilder.UnpackPosition(in entry);
            return (position - viewerPosition).SqrMagnitude <= metres * metres;
        }
    }
}
