using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// Encodes vehicle snapshots as deltas against a baseline the client has confirmed
    /// receiving. One instance per connected client. The vehicle counterpart of
    /// <see cref="DeltaEncoder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same baseline rule as the actor stream and for the same reason (C-AD-1): deltaing
    /// against tick N-1 is smaller and completely brittle, because one lost packet leaves every
    /// later snapshot undecodable. Deltaing against a tick the client explicitly acked means a
    /// lost snapshot costs exactly that snapshot.
    /// </para>
    /// <para>
    /// Cost is a fraction of the actor encoder's: 32 baselines of at most
    /// <see cref="ProtocolConstants.MAX_VEHICLES"/> entries each.
    /// </para>
    /// <para>
    /// Allocation-free after construction. The history is allocated once and recycled through
    /// <see cref="VehicleWorldSnapshot.CopyFrom"/>.
    /// </para>
    /// </remarks>
    public sealed class VehicleDeltaEncoder
    {
        /// <summary>
        /// Snapshots retained per client. Shares <see cref="DeltaEncoder.BaselineHistory"/>
        /// rather than declaring a second number: both streams are acked by the same
        /// <c>C_ACK_BASELINE</c> tick, so two different history depths would mean a tick that is
        /// a usable baseline for one stream and not the other.
        /// </summary>
        public const int BaselineHistory = DeltaEncoder.BaselineHistory;

        private readonly VehicleWorldSnapshot[] _history;
        private uint _ackedBaselineTick;

        public VehicleDeltaEncoder()
        {
            _history = new VehicleWorldSnapshot[BaselineHistory];
            for (int i = 0; i < BaselineHistory; i++) _history[i] = new VehicleWorldSnapshot();
        }

        /// <summary>The newest tick the client has confirmed. 0 means "nothing yet".</summary>
        public uint AckedBaselineTick => _ackedBaselineTick;

        /// <summary>Snapshots sent as full because no usable baseline existed.</summary>
        public long FullSnapshotCount { get; private set; }

        /// <summary>Snapshots sent as deltas.</summary>
        public long DeltaSnapshotCount { get; private set; }

        /// <summary>Total bytes written, for bandwidth measurement.</summary>
        public long BytesWritten { get; private set; }

        /// <summary>Records a client ack.</summary>
        /// <remarks>
        /// Routed through <see cref="SequenceMath.IsNewer32"/> rather than <c>&gt;</c>, exactly
        /// as the actor encoder does: moving the baseline backwards would delta against a state
        /// newer than the one the client holds, which decodes into a plausible-looking, wrong
        /// world.
        /// </remarks>
        public void OnClientAck(uint tick)
        {
            if (tick == 0) return;
            if (_ackedBaselineTick == 0 || SequenceMath.IsNewer32(tick, _ackedBaselineTick))
                _ackedBaselineTick = tick;
        }

        /// <summary>Forgets the baseline, forcing the next snapshot to be full.</summary>
        public void Reset()
        {
            _ackedBaselineTick = 0;
            for (int i = 0; i < _history.Length; i++) _history[i].Clear();
        }

        /// <summary>
        /// Writes <paramref name="current"/> as a delta when a usable baseline exists, and as a
        /// full snapshot otherwise. Files it into the history either way.
        /// </summary>
        /// <returns>Bytes written, or -1 if the buffer was too small.</returns>
        public int Write(Span<byte> dst, VehicleWorldSnapshot current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));

            bool hasBaseline = TryFindBaseline(current.ServerTick, out VehicleWorldSnapshot? baseline);

            int written = hasBaseline
                ? WriteDelta(dst, current, baseline!)
                : WriteFull(dst, current);

            if (written < 0) return -1;

            // Only recorded AFTER a successful write. Filing a snapshot that was never emitted
            // would let a later ack select a baseline the two sides do not share.
            Record(current);

            if (hasBaseline) DeltaSnapshotCount++;
            else             FullSnapshotCount++;

            BytesWritten += written;
            return written;
        }

        /// <summary>
        /// Writes a full vehicle snapshot: <c>baselineTick = 0</c> and every field of every
        /// vehicle present, so a client can rebuild with no prior state.
        /// </summary>
        public static int WriteFull(Span<byte> dst, VehicleWorldSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            Span<VehicleSnapshotEntry> entries =
                snapshot.Vehicles.AsSpan(0, snapshot.VehicleCount);

            // Forced on regardless of what the caller left in the mask. A "full" snapshot that
            // inherited a delta's sparse mask would be undecodable by a client with no
            // baseline, and would do it silently.
            for (int i = 0; i < entries.Length; i++)
                entries[i].ChangeMask = VehicleField.Full;

            var header = new VehicleSnapshotHeader(
                snapshot.ServerTick,
                baselineTick: 0,
                vehicleCount: (byte)snapshot.VehicleCount);

            return VehicleSnapshotMessage.Write(dst, in header, entries);
        }

        private bool TryFindBaseline(uint currentTick, out VehicleWorldSnapshot? baseline)
        {
            baseline = null;

            if (_ackedBaselineTick == 0) return false;

            int age = SequenceMath.Distance32(currentTick, _ackedBaselineTick);
            if (age <= 0 || age >= BaselineHistory) return false;

            VehicleWorldSnapshot candidate = _history[_ackedBaselineTick % BaselineHistory];

            // The ring index alone is not proof: ticks 100 and 132 share a slot, so the tick
            // stored there must be verified before it is trusted.
            if (candidate.ServerTick != _ackedBaselineTick) return false;

            baseline = candidate;
            return true;
        }

        private void Record(VehicleWorldSnapshot snapshot)
            => _history[snapshot.ServerTick % BaselineHistory].CopyFrom(snapshot);

        private static int WriteDelta(
            Span<byte> dst, VehicleWorldSnapshot current, VehicleWorldSnapshot baseline)
        {
            Span<VehicleSnapshotEntry> entries =
                current.Vehicles.AsSpan(0, current.VehicleCount);

            for (int i = 0; i < entries.Length; i++)
            {
                if (baseline.TryFind(entries[i].VehicleId, out VehicleSnapshotEntry previous))
                {
                    entries[i].ChangeMask = ComputeChangeMask(in previous, in entries[i]);
                }
                else
                {
                    // Not in the baseline: the client has never seen this vehicle, so a mask of
                    // changed-fields-only would leave garbage in everything else.
                    //
                    // VehicleField.Full, all 8 bits — NOT the actor stream's FullNoSeat dodge.
                    // That one exists because SnapshotField.SeatInfo describes a relationship an
                    // unseated actor does not have. Every bit here is a field every vehicle
                    // genuinely carries, so there is nothing to opt out of.
                    entries[i].ChangeMask = VehicleField.Full;
                }
            }

            var header = new VehicleSnapshotHeader(
                current.ServerTick,
                baseline.ServerTick,
                (byte)current.VehicleCount);

            return VehicleSnapshotMessage.Write(dst, in header, entries);
        }

        /// <summary>
        /// Which fields differ between two quantized entries.
        /// </summary>
        /// <remarks>
        /// Both arguments are already quantized (see <see cref="VehicleWorldSnapshot"/>), so
        /// this is an integer comparison and physics jitter below the wire's own resolution does
        /// not register. Comparing raw floats here instead sets the Position bit on every
        /// vehicle every tick, makes the delta carry every field, matches a full snapshot's
        /// bandwidth — and passes every test.
        /// </remarks>
        public static VehicleField ComputeChangeMask(
            in VehicleSnapshotEntry baseline, in VehicleSnapshotEntry current)
        {
            VehicleField mask = VehicleField.None;

            if (baseline.PosX != current.PosX
                || baseline.PosY != current.PosY
                || baseline.PosZ != current.PosZ)
                mask |= VehicleField.Position;

            if (baseline.Rotation != current.Rotation) mask |= VehicleField.Rotation;

            if (baseline.VelX != current.VelX
                || baseline.VelY != current.VelY
                || baseline.VelZ != current.VelZ)
                mask |= VehicleField.LinearVelocity;

            if (baseline.AngVelX != current.AngVelX
                || baseline.AngVelY != current.AngVelY
                || baseline.AngVelZ != current.AngVelZ)
                mask |= VehicleField.AngularVelocity;

            if (baseline.Health != current.Health) mask |= VehicleField.Health;
            if (baseline.Flags  != current.Flags)  mask |= VehicleField.Flags;

            if (baseline.TurretYaw != current.TurretYaw
                || baseline.TurretPitch != current.TurretPitch)
                mask |= VehicleField.Turret;

            if (baseline.SubtypeA != current.SubtypeA || baseline.SubtypeB != current.SubtypeB)
                mask |= VehicleField.Subtype;

            return mask;
        }
    }
}
