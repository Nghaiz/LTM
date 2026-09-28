using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// The weapon half of one actor's snapshot entry, as
    /// <see cref="SnapshotBuilder.ResolveWeaponFields"/> resolved it.
    /// </summary>
    /// <remarks>
    /// A struct of the three so a caller cannot carry the clip forward and forget the reserve.
    /// <c>DeltaEncoder.ComputeChangeMask</c> masks the four weapon parts together and
    /// <c>DeltaDecoder.ApplyEntry</c> replaces or carries all four - a partial assignment on
    /// this side would put a reload flag from one tick beside a clip from another.
    /// </remarks>
    public readonly struct WeaponSnapshotFields
    {
        public WeaponSnapshotFields(
            byte ammoInClip, ushort spareAmmoEncoded, WeaponStateFlags stateFlags)
        {
            AmmoInClip = ammoInClip;
            SpareAmmoEncoded = spareAmmoEncoded;
            StateFlags = stateFlags;
        }

        public byte AmmoInClip { get; }

        public ushort SpareAmmoEncoded { get; }

        public WeaponStateFlags StateFlags { get; }

        /// <summary>The reserve, decoded back. For a HUD, a log, or a test.</summary>
        public SpareAmmo Reserve => SpareAmmo.Decode(SpareAmmoEncoded);
    }

    /// <summary>
    /// Turns gameplay state into quantized snapshot entries, and writes a full snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split against <see cref="SnapshotMessage"/> is deliberate and is the seam between
    /// two owners: <see cref="SnapshotMessage"/> (Ironfront.Net.Protocol, shared) decides how
    /// bytes are laid out; this class (the replication track) decides which actors and which fields go in at
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
        /// Quantizes one actor's gameplay state into a snapshot entry with every field the
        /// actor actually has present.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="health"/> is clamped to <see cref="Quantize.HEALTH_MAX"/> rather
        /// than trusted: it arrives as a float from gameplay, and a medkit overshoot or a
        /// negative from an overkill hit would otherwise wrap the byte and hand the client a
        /// player at 250 HP.
        /// </para>
        /// <para>
        /// <paramref name="vehicleId"/> and <paramref name="seatIndex"/> are optional so that
        /// the fourteen existing call sites that capture actors on foot keep compiling
        /// unchanged. A zero <paramref name="vehicleId"/> means on foot and produces
        /// <see cref="SnapshotField.FullNoSeat"/>; anything else produces
        /// <see cref="SnapshotField.Full"/>, which is 3 bytes wider.
        /// </para>
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
            byte team,
            ushort vehicleId = 0,
            byte seatIndex = 0,
            ushort spareAmmoEncoded = SpareAmmo.NoResupplyEncoded,
            WeaponStateFlags weaponStateFlags = WeaponStateFlags.None)
        {
            // Saturation is otherwise invisible: PackPos clamps, and the clamped value decodes
            // to a plausible position on the boundary rather than to anything that looks wrong.
            // X-39.
            World.PositionSaturationLog.Observe(isVehicle: false, actorId, in position);

            return new ActorSnapshotEntry
            {
                ActorId    = actorId,
                ChangeMask = vehicleId != 0
                    ? SnapshotField.Full
                    : SnapshotField.FullNoSeat,

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

                // Default no-resupply rather than zero, because on the wire those are different
                // facts and only one of them is safe to guess: a HUD reading "no reserve" for a
                // weapon that has one is a display bug, and a HUD reading "0 rounds" for a
                // weapon that never had a reserve is a player waiting for an ammo bag that can
                // never help them. Every caller that knows better passes it.
                SpareAmmoEncoded = spareAmmoEncoded,
                WeaponStateFlags = weaponStateFlags,

                VehicleId  = vehicleId,
                SeatIndex  = seatIndex,
            };
        }

        /// <summary>
        /// The three weapon numbers an actor's snapshot entry carries, resolved from the
        /// server's own state. Handoff section 4.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One function, because the reserve has three sources and they disagree.</b> The
        /// clip is the session's, the reserve is <c>ActorSpareAmmoPool</c>'s keyed by
        /// <c>(actorId, loadoutSlot)</c>, and whether the weapon has a reserve at all is the
        /// weapon config's. Resolving them at the call site would put the "which -1 is this?"
        /// question - the one <see cref="SpareAmmo"/> exists to answer once - back into the
        /// snapshot builder, and the wrong answer shows up as a HUD reading 65535.
        /// </para>
        /// <para>
        /// <b>Mounted weapons do not come through here.</b> A turret's reserve lives on the
        /// weapon and would be a different number under the same field; if a turret HUD needs
        /// one later it gets its own field in the vehicle stream.
        /// </para>
        /// </remarks>
        public static WeaponSnapshotFields ResolveWeaponFields(
            in WeaponRuntimeState weapon, in WeaponConfig config, in ActorAmmoSource ammo)
        {
            WeaponStateFlags flags = WeaponStateFlags.None;
            if (weapon.Reloading) flags |= WeaponStateFlags.Reloading;
            if (weapon.PendingRelease) flags |= WeaponStateFlags.PendingRelease;

            return new WeaponSnapshotFields(
                weapon.AmmoInClip,
                ammo.Reserve(in weapon, in config).Encode(),
                flags);
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
            //
            // The seat bit is preserved rather than cleared. Forcing FullNoSeat here — which is
            // what this loop used to do unconditionally — means a full snapshot never carries
            // seat state at all, so a joining client sees every passenger standing in the road
            // until each of them happens to change seats.
            for (int i = 0; i < entries.Length; i++)
                entries[i].ChangeMask = entries[i].VehicleId != 0
                    ? SnapshotField.Full
                    : SnapshotField.FullNoSeat;

            var header = new SnapshotHeader(
                snapshot.ServerTick,
                lastProcessedInputTick,
                baselineTick: 0,
                actorCount: (byte)snapshot.ActorCount);

            return SnapshotMessage.Write(dst, in header, entries);
        }

        /// <summary>
        /// Encoded size of a full snapshot with this many actors on foot: the 13-byte header
        /// plus 20 bytes per actor. 64 actors is 1293 bytes, past the 1184-byte payload limit,
        /// so a full server's join snapshot fragments. That is expected, not an error.
        /// </summary>
        /// <remarks>
        /// Seated actors are 3 bytes wider each. This is a floor for planning, not the width of
        /// any particular snapshot — <see cref="SnapshotMessage.SizeFor"/> is the exact answer.
        /// </remarks>
        public static int FullSizeFor(int actorCount)
            => SnapshotHeader.Size + actorCount * SnapshotMessage.EntrySize(SnapshotField.FullNoSeat);
    }
}
