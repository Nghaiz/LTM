using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// Turns gameplay state into quantized snapshot entries, and writes a full snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split against <see cref="SnapshotMessage"/> is deliberate and is the seam between
    /// two owners: <see cref="SnapshotMessage"/> (Ironfront.Net.Protocol, shared) decides how
    /// bytes are laid out; this class (Dev C) decides which actors and which fields go in at
    /// all. Nothing here may invent a wire layout.
    /// </para>
    /// <para>
    /// Quantization happens exactly once, here, on the way in. Everything downstream —
    /// change detection, history, the wire — sees the same quantized values, so the client
    /// and the server's own baseline never disagree about what a position "is".
    /// </para>
    /// </remarks>
    public static class SnapshotBuilder
    {
        /// <summary>
        /// Quantizes one actor's gameplay state into a snapshot entry with every v1 field
        /// present (<see cref="SnapshotField.FullNoSeat"/>).
        /// </summary>
        /// <remarks>
        /// <paramref name="health"/> is clamped to <see cref="Quantize.HEALTH_MAX"/> rather
        /// than trusted: it arrives as a float from gameplay, and a medkit overshoot or a
        /// negative from an overkill hit would otherwise wrap the byte and hand the client a
        /// player at 250 HP.
        /// </remarks>
        public static ActorSnapshotEntry Capture(
            ushort actorId,
            in Vec3 position,
            float yawDegrees,
            float pitchDegrees,
            in Vec3 velocity,
            ActorStateFlags stateFlags,
            float health,
            byte weaponId,
            byte ammoInClip,
            byte team)
        {
            return new ActorSnapshotEntry
            {
                ActorId    = actorId,
                ChangeMask = SnapshotField.FullNoSeat,

                PosX = Quantize.PackPos(position.X),
                PosY = Quantize.PackPos(position.Y),
                PosZ = Quantize.PackPos(position.Z),

                Yaw   = Quantize.PackYaw(yawDegrees),
                Pitch = Quantize.PackPitchByte(pitchDegrees),

                VelX = Quantize.PackVel(velocity.X),
                VelY = Quantize.PackVel(velocity.Y),
                VelZ = Quantize.PackVel(velocity.Z),

                StateFlags = stateFlags,
                Health     = ClampHealth(health),
                WeaponId   = weaponId,
                AmmoInClip = ammoInClip,
                Team       = team,
            };
        }

        /// <summary>Rounds and clamps a float health into the 0..100 the wire allows.</summary>
        public static byte ClampHealth(float health)
        {
            if (health <= 0f) return 0;
            if (health >= Quantize.HEALTH_MAX) return Quantize.HEALTH_MAX;
            return (byte)(health + 0.5f);
        }

        /// <summary>Reverses <see cref="Capture"/>'s position quantization.</summary>
        public static Vec3 UnpackPosition(in ActorSnapshotEntry entry)
            => new Vec3(
                Quantize.UnpackPos(entry.PosX),
                Quantize.UnpackPos(entry.PosY),
                Quantize.UnpackPos(entry.PosZ));

        /// <summary>Reverses <see cref="Capture"/>'s velocity quantization.</summary>
        public static Vec3 UnpackVelocity(in ActorSnapshotEntry entry)
            => new Vec3(
                Quantize.UnpackVel(entry.VelX),
                Quantize.UnpackVel(entry.VelY),
                Quantize.UnpackVel(entry.VelZ));

        /// <summary>
        /// Writes a full snapshot: <c>baselineTick = 0</c> and every field of every actor
        /// present, so a client can rebuild the world with no prior state.
        /// </summary>
        /// <returns>Bytes written, or -1 if the buffer is too small.</returns>
        public static int WriteFull(
            Span<byte> dst, WorldSnapshot snapshot, uint lastProcessedInputTick)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            Span<ActorSnapshotEntry> entries =
                snapshot.Actors.AsSpan(0, snapshot.ActorCount);

            // A full snapshot forces every field on regardless of what the caller left in the
            // mask. A "full" snapshot that inherited a delta's sparse mask would be
            // undecodable by a client with no baseline, and would do it silently.
            for (int i = 0; i < entries.Length; i++)
                entries[i].ChangeMask = SnapshotField.FullNoSeat;

            var header = new SnapshotHeader(
                snapshot.ServerTick,
                lastProcessedInputTick,
                baselineTick: 0,
                actorCount: (byte)snapshot.ActorCount);

            return SnapshotMessage.Write(dst, in header, entries);
        }

        /// <summary>
        /// Encoded size of a full snapshot with this many actors: the 13-byte header plus 20
        /// bytes per actor. 64 actors is 1293 bytes, past the 1184-byte payload limit, so a
        /// full server's join snapshot fragments. That is expected, not an error.
        /// </summary>
        public static int FullSizeFor(int actorCount)
            => SnapshotHeader.Size + actorCount * SnapshotMessage.EntrySize(SnapshotField.FullNoSeat);
    }
}
