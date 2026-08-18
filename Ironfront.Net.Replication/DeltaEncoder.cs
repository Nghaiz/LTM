using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// Encodes snapshots as deltas against a baseline the client has confirmed receiving.
    /// One instance per connected client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the baseline is an acked tick and not simply the previous snapshot.</b>
    /// Deltaing against tick N-1 is smaller and completely brittle: lose one packet and every
    /// snapshot after it is undecodable, because the client has no baseline to apply them to,
    /// and the stream only recovers with a full resend. Deltaing against a tick the client
    /// has explicitly acked (C_ACK_BASELINE, protocol-spec.md section 4.1) means a lost
    /// snapshot costs exactly that snapshot — the next one is still decodable, because it is
    /// measured from a state the client demonstrably has. This is decision C-AD-1.
    /// </para>
    /// <para>
    /// Cost: the server keeps <see cref="BaselineHistory"/> snapshots per client. At 64
    /// actors that is 32 x ~1.3 KB = ~42 KB per client, ~670 KB for a 16-player server. Cheap
    /// against the alternative.
    /// </para>
    /// <para>
    /// Allocation-free after construction. The history buffers are allocated once in the
    /// constructor and recycled through <see cref="WorldSnapshot.CopyFrom"/>, so a 20 Hz
    /// snapshot stream produces no garbage and no GC spike.
    /// </para>
    /// </remarks>
    public sealed class DeltaEncoder
    {
        /// <summary>
        /// Snapshots retained per client. At 20 Hz with server ticks advancing 1-2 per
        /// snapshot, 32 ticks is roughly 1.1 s of history — comfortably longer than any
        /// round trip we accept, and a power of two so the ring index is a mask.
        /// </summary>
        public const int BaselineHistory = 32;

        private readonly WorldSnapshot[] _history;
        private uint _ackedBaselineTick;

        public DeltaEncoder()
        {
            _history = new WorldSnapshot[BaselineHistory];
            for (int i = 0; i < BaselineHistory; i++) _history[i] = new WorldSnapshot();
        }

        /// <summary>The newest tick the client has confirmed. 0 means "nothing yet".</summary>
        public uint AckedBaselineTick => _ackedBaselineTick;

        /// <summary>Snapshots sent as full because no usable baseline existed.</summary>
        public long FullSnapshotCount { get; private set; }

        /// <summary>Snapshots sent as deltas.</summary>
        public long DeltaSnapshotCount { get; private set; }

        /// <summary>Total bytes written, for the bandwidth measurements phase-01 requires.</summary>
        public long BytesWritten { get; private set; }

        /// <summary>
        /// Records a client ack.
        /// </summary>
        /// <remarks>
        /// Routed through <see cref="SequenceMath.IsNewer32"/> rather than <c>&gt;</c>. Acks
        /// arrive on the reliable channel, but "reliable" is not "cannot be delivered out of
        /// order relative to what the encoder has already seen" — and moving the baseline
        /// backwards would have the server delta against a state newer than the one the
        /// client is holding, which decodes into a plausible-looking, wrong world.
        /// </remarks>
        public void OnClientAck(uint tick)
        {
            if (tick == 0) return;
            if (_ackedBaselineTick == 0 || SequenceMath.IsNewer32(tick, _ackedBaselineTick))
                _ackedBaselineTick = tick;
        }

        /// <summary>Forgets the baseline, forcing the next snapshot to be full. Used on respawn/rejoin.</summary>
        public void Reset()
        {
            _ackedBaselineTick = 0;
            for (int i = 0; i < _history.Length; i++) _history[i].Clear();
        }

        /// <summary>
        /// Writes <paramref name="current"/> as a delta when a usable baseline exists, and as
        /// a full snapshot otherwise. Files the snapshot into the history either way, so it
        /// can serve as a future baseline once acked.
        /// </summary>
        /// <returns>Bytes written, or -1 if the buffer was too small.</returns>
        public int Write(Span<byte> dst, WorldSnapshot current, uint lastProcessedInputTick)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));

            bool hasBaseline = TryFindBaseline(current.ServerTick, out WorldSnapshot? baseline);

            int written = hasBaseline
                ? WriteDelta(dst, current, baseline!, lastProcessedInputTick)
                : SnapshotBuilder.WriteFull(dst, current, lastProcessedInputTick);

            if (written < 0) return -1;

            // Only record AFTER a successful write. Filing a snapshot that was never actually
            // emitted would let a later ack select a baseline the two sides do not share.
            Record(current);

            if (hasBaseline) DeltaSnapshotCount++;
            else             FullSnapshotCount++;

            BytesWritten += written;
            return written;
        }

        private bool TryFindBaseline(uint currentTick, out WorldSnapshot? baseline)
        {
            baseline = null;

            if (_ackedBaselineTick == 0) return false;

            // Unsigned subtraction here would turn "the ack is somehow ahead of us" into a
            // 4-billion distance that passes no check; the signed form makes it a negative
            // that fails the range test, which is the correct outcome.
            int age = SequenceMath.Distance32(currentTick, _ackedBaselineTick);
            if (age <= 0 || age >= BaselineHistory) return false;

            WorldSnapshot candidate = _history[_ackedBaselineTick % BaselineHistory];

            // The ring index alone is not proof. Ticks 100 and 132 share a slot, so the tick
            // stored there must be verified before it is trusted as a baseline.
            if (candidate.ServerTick != _ackedBaselineTick) return false;

            baseline = candidate;
            return true;
        }

        private void Record(WorldSnapshot snapshot)
            => _history[snapshot.ServerTick % BaselineHistory].CopyFrom(snapshot);

        private static int WriteDelta(
            Span<byte> dst, WorldSnapshot current, WorldSnapshot baseline, uint lastProcessedInputTick)
        {
            Span<ActorSnapshotEntry> entries = current.Actors.AsSpan(0, current.ActorCount);

            for (int i = 0; i < entries.Length; i++)
            {
                if (baseline.TryFind(entries[i].ActorId, out ActorSnapshotEntry previous))
                {
                    entries[i].ChangeMask = ComputeChangeMask(in previous, in entries[i]);
                }
                else
                {
                    // Not in the baseline: the client has never seen this actor, so a mask of
                    // changed-fields-only would leave it with garbage for everything else.
                    //
                    // Full or FullNoSeat by whether the actor is actually in a vehicle. This
                    // used to be FullNoSeat unconditionally, because v1 never populated the
                    // seat field; v3 does, and sending a new actor without it would introduce
                    // the passenger-standing-in-the-road bug on the interest-set path instead
                    // of the join path. Still not a blanket 0xFF: for an actor on foot that
                    // would spend 3 bytes per new actor saying "no vehicle".
                    entries[i].ChangeMask = entries[i].VehicleId != 0
                        ? SnapshotField.Full
                        : SnapshotField.FullNoSeat;
                }
            }

            var header = new SnapshotHeader(
                current.ServerTick,
                lastProcessedInputTick,
                baseline.ServerTick,
                (byte)current.ActorCount);

            return SnapshotMessage.Write(dst, in header, entries);
        }

        /// <summary>
        /// Which fields differ between two quantized entries.
        /// </summary>
        /// <remarks>
        /// Both arguments are already quantized (see <see cref="WorldSnapshot"/>), so this is
        /// an integer comparison and physics jitter below the 6.25 cm position resolution
        /// simply does not register. Comparing raw floats here instead is phase-01's trap 4:
        /// the Position bit would be set on every actor every tick, the delta would carry
        /// every field, and bandwidth would match a full snapshot while every test still
        /// passed.
        /// </remarks>
        public static SnapshotField ComputeChangeMask(
            in ActorSnapshotEntry baseline, in ActorSnapshotEntry current)
        {
            SnapshotField mask = SnapshotField.None;

            if (baseline.PosX != current.PosX
                || baseline.PosY != current.PosY
                || baseline.PosZ != current.PosZ)
                mask |= SnapshotField.Position;

            if (baseline.Yaw != current.Yaw || baseline.Pitch != current.Pitch)
                mask |= SnapshotField.Rotation;

            if (baseline.VelX != current.VelX
                || baseline.VelY != current.VelY
                || baseline.VelZ != current.VelZ)
                mask |= SnapshotField.Velocity;

            if (baseline.StateFlags != current.StateFlags) mask |= SnapshotField.StateFlags;
            if (baseline.Health     != current.Health)     mask |= SnapshotField.Health;

            if (baseline.WeaponId != current.WeaponId || baseline.AmmoInClip != current.AmmoInClip)
                mask |= SnapshotField.Weapon;

            if (baseline.Team != current.Team) mask |= SnapshotField.Team;

            // Seat changes include LEAVING one, which is a change to vehicleId 0. That is the
            // whole reason 0 is a reserved sentinel rather than just an unused value: a field
            // sent only on change has no other way to say "no longer seated".
            if (baseline.VehicleId != current.VehicleId
                || baseline.SeatIndex != current.SeatIndex)
                mask |= SnapshotField.SeatInfo;

            return mask;
        }
    }
}
