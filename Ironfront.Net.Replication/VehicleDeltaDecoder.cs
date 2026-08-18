using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication
{
    /// <summary>
    /// The client half of vehicle delta encoding: rebuilds full vehicle state from a full
    /// snapshot, or from a delta plus the baseline it names. The counterpart of
    /// <see cref="DeltaDecoder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each entry starts as a copy of its baseline.</b> A delta with the Position bit clear
    /// means "unchanged", so the position must be carried over. Building a fresh entry and
    /// filling in only the advertised fields — the way the loop naturally wants to be written —
    /// leaves everything else at its struct default, and a vehicle that stopped moving
    /// teleports to the world origin at zero rotation. That is why
    /// <see cref="ApplyEntry"/> takes the baseline by reference rather than constructing.
    /// </para>
    /// <para>
    /// Keeps its own history so it can decode a delta naming any baseline it recently acked,
    /// not just the newest.
    /// </para>
    /// </remarks>
    public sealed class VehicleDeltaDecoder
    {
        private readonly VehicleWorldSnapshot[] _history;
        private readonly VehicleSnapshotEntry[] _scratch;
        private bool _hasApplied;

        public VehicleDeltaDecoder()
        {
            _history = new VehicleWorldSnapshot[VehicleDeltaEncoder.BaselineHistory];
            for (int i = 0; i < _history.Length; i++) _history[i] = new VehicleWorldSnapshot();

            _scratch = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            Current  = new VehicleWorldSnapshot();
        }

        /// <summary>The reconstructed vehicle world. Valid once <see cref="Read"/> returned Applied.</summary>
        public VehicleWorldSnapshot Current { get; }

        /// <summary>The newest vehicle-snapshot tick applied. 0 until the first one lands.</summary>
        /// <remarks>
        /// <para>
        /// <b>Nothing consumes this yet, and that is a precondition rather than an oversight.</b>
        /// <c>C_ACK_BASELINE</c> carries ONE tick, taken from <see cref="DeltaDecoder.AckTick"/>,
        /// and both encoders key their baseline history off it. That is only sound while the two
        /// snapshots travel in the same datagram (protocol-spec.md § 4.10, co-residency): they are
        /// then applied together or lost together, so one acked tick describes both.
        /// </para>
        /// <para>
        /// <b>If they are ever split, this becomes a correctness bug, not a tuning question.</b> A
        /// lost vehicle snapshot whose actor twin arrived would have the client ack a tick it never
        /// applied on this stream, and the server would delta every later vehicle snapshot against
        /// a baseline the client does not hold — <see cref="SnapshotReadResult.UnknownBaseline"/>
        /// forever, with no full snapshot to recover from, because the server believes it has one.
        /// Splitting the streams therefore requires a second ack tick on the wire first. This
        /// property is exposed so that a split has something to wire up rather than something to
        /// discover.
        /// </para>
        /// </remarks>
        public uint AckTick => _hasApplied ? Current.ServerTick : 0u;

        public long AppliedCount { get; private set; }
        public long UnknownBaselineCount { get; private set; }
        public long StaleCount { get; private set; }

        /// <summary>Forgets everything. Used when the connection restarts.</summary>
        public void Reset()
        {
            _hasApplied = false;
            Current.Clear();
            for (int i = 0; i < _history.Length; i++) _history[i].Clear();
        }

        /// <summary>
        /// Decodes an <c>S_VEHICLE_SNAPSHOT</c> body and, on success, updates
        /// <see cref="Current"/>.
        /// </summary>
        public SnapshotReadResult Read(ReadOnlySpan<byte> body)
        {
            if (!VehicleSnapshotMessage.TryParse(
                    body, _scratch, out VehicleSnapshotHeader header, out int count))
                return SnapshotReadResult.Malformed;

            // Channel 1 already drops packets older than one delivered. Checking again costs
            // nothing and means a decoder fed from a replay or a test cannot be walked backwards.
            if (_hasApplied && !SequenceMath.IsNewer32(header.ServerTick, Current.ServerTick))
            {
                StaleCount++;
                return SnapshotReadResult.Stale;
            }

            if (header.IsFullSnapshot)
            {
                ApplyFull(in header, count);
                return Finish();
            }

            VehicleWorldSnapshot baseline =
                _history[header.BaselineTick % VehicleDeltaEncoder.BaselineHistory];

            if (!_hasApplied || baseline.ServerTick != header.BaselineTick)
            {
                UnknownBaselineCount++;
                return SnapshotReadResult.UnknownBaseline;
            }

            ApplyDelta(in header, count, baseline);
            return Finish();
        }

        private SnapshotReadResult Finish()
        {
            _hasApplied = true;
            AppliedCount++;

            _history[Current.ServerTick % VehicleDeltaEncoder.BaselineHistory].CopyFrom(Current);

            return SnapshotReadResult.Applied;
        }

        private void ApplyFull(in VehicleSnapshotHeader header, int count)
        {
            Current.Clear();
            Current.ServerTick = header.ServerTick;

            for (int i = 0; i < count; i++) Current.Add(in _scratch[i]);
        }

        private void ApplyDelta(
            in VehicleSnapshotHeader header, int count, VehicleWorldSnapshot baseline)
        {
            // Rebuilt from scratch. A vehicle in the baseline but absent from this snapshot is
            // simply not re-added, which is how a despawn arrives: the snapshot's vehicle list
            // is authoritative about who exists, and the changeMask only about which of their
            // fields moved.
            Current.Clear();
            Current.ServerTick = header.ServerTick;

            for (int i = 0; i < count; i++)
            {
                ref VehicleSnapshotEntry incoming = ref _scratch[i];

                VehicleSnapshotEntry resolved =
                    baseline.TryFind(incoming.VehicleId, out VehicleSnapshotEntry previous)
                        ? ApplyEntry(in previous, in incoming)
                        : incoming;

                // The stored entry carries every field, not the sparse wire mask, so it can
                // serve as a baseline itself next tick.
                resolved.ChangeMask = VehicleField.Full;
                Current.Add(in resolved);
            }
        }

        /// <summary>
        /// Overlays the fields <paramref name="incoming"/> actually carries onto
        /// <paramref name="baseline"/>. Everything else is inherited.
        /// </summary>
        public static VehicleSnapshotEntry ApplyEntry(
            in VehicleSnapshotEntry baseline, in VehicleSnapshotEntry incoming)
        {
            VehicleSnapshotEntry result = baseline;
            result.VehicleId = incoming.VehicleId;

            VehicleField mask = incoming.ChangeMask;

            if ((mask & VehicleField.Position) != 0)
            {
                result.PosX = incoming.PosX;
                result.PosY = incoming.PosY;
                result.PosZ = incoming.PosZ;
            }

            if ((mask & VehicleField.Rotation) != 0) result.Rotation = incoming.Rotation;

            if ((mask & VehicleField.LinearVelocity) != 0)
            {
                result.VelX = incoming.VelX;
                result.VelY = incoming.VelY;
                result.VelZ = incoming.VelZ;
            }

            if ((mask & VehicleField.AngularVelocity) != 0)
            {
                result.AngVelX = incoming.AngVelX;
                result.AngVelY = incoming.AngVelY;
                result.AngVelZ = incoming.AngVelZ;
            }

            if ((mask & VehicleField.Health) != 0) result.Health = incoming.Health;
            if ((mask & VehicleField.Flags)  != 0) result.Flags  = incoming.Flags;

            if ((mask & VehicleField.Turret) != 0)
            {
                result.TurretYaw   = incoming.TurretYaw;
                result.TurretPitch = incoming.TurretPitch;
            }

            if ((mask & VehicleField.Subtype) != 0)
            {
                result.SubtypeA = incoming.SubtypeA;
                result.SubtypeB = incoming.SubtypeB;
            }

            return result;
        }
    }
}
