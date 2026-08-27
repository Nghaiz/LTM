using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The server's SSOT for which vehicles exist, what is authoritative about them, and who is
    /// sitting in them — plus the once-per-tick capture that turns all of that into the wire
    /// buffer V3 defined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It does NOT own the id pool, and the phase plan said it should.</b> V8 task 6 shipped
    /// <c>ServerVehicleLifecycleSink</c>, which allocates from <c>VehicleIdPool</c> because it
    /// is the thing that puts <c>S_VEHICLE_SPAWN</c> on the wire and the quarantine only means
    /// anything relative to what was actually announced. A second owner here would be two
    /// allocators over one id space — the duplicate source of truth
    /// <c>development-principles.md</c> forbids, and the failure would be a live vehicle and a
    /// freshly spawned one holding the same id with nothing on either side able to tell.
    /// A vehicle is registered here <i>with</i> the id it was already given.
    /// </para>
    /// <para>
    /// <b>An array indexed by id, not a dictionary</b> — <c>ServerRespawnGate</c> set the
    /// precedent in phase-05. Ids are dense, 1-based and capped at
    /// <see cref="ProtocolConstants.MAX_VEHICLES"/>, so the array is 17 slots and a lookup is a
    /// bounds check. A dictionary would hash on the hot path to answer a question an index
    /// already answers.
    /// </para>
    /// <para>
    /// <b>A dense id list runs alongside the sparse array</b> so capture walks live vehicles
    /// rather than 17 slots looking for 3. Removal swaps with the last entry, so the list is
    /// unordered — which is fine here and would not be in the interest tracker, where the
    /// admission order is load-bearing.
    /// </para>
    /// <para>
    /// <b>Nothing here allocates after construction.</b> Capture is on the 20 Hz snapshot path
    /// (conventions § 3.2) and the seat tables are pre-sized at
    /// <see cref="VehicleState.MaxSeats"/> per vehicle.
    /// </para>
    /// </remarks>
    public sealed class VehicleRegistry
    {
        private readonly VehicleState[] _states;
        private readonly IVehiclePoseSource?[] _poses;
        private readonly ushort[] _occupants;
        private readonly bool[] _live;

        private readonly ushort[] _liveIds;
        private int _liveCount;

        private readonly int _capacity;

        public VehicleRegistry(int capacity = ProtocolConstants.MAX_VEHICLES)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity  = capacity;
            _states    = new VehicleState[capacity + 1];          // 1-based; slot 0 unused
            _poses     = new IVehiclePoseSource?[capacity + 1];
            _live      = new bool[capacity + 1];
            _occupants = new ushort[(capacity + 1) * VehicleState.MaxSeats];
            _liveIds   = new ushort[capacity];
        }

        /// <summary>Vehicles currently registered.</summary>
        public int LiveCount => _liveCount;

        /// <summary>The largest id this registry accepts.</summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Registers a vehicle under the id the lifecycle sink already assigned it.
        /// </summary>
        /// <returns>
        /// False for id 0 (never replicated), an id past <see cref="Capacity"/>, or an id
        /// already live. False rather than an exception: a spawner double-reporting is a
        /// duplicate report, not a second vehicle, and the vehicle path must not be able to
        /// take the tick loop down.
        /// </returns>
        public bool Add(in VehicleState state, IVehiclePoseSource pose)
        {
            if (pose == null) throw new ArgumentNullException(nameof(pose));

            ushort id = state.VehicleId;
            if (id == 0 || id > _capacity || _live[id]) return false;

            _states[id] = state;
            _poses[id]  = pose;
            _live[id]   = true;

            ClearSeats(id);

            _liveIds[_liveCount++] = id;
            return true;
        }

        /// <summary>Removes a vehicle. Returns false when it was not registered.</summary>
        /// <remarks>
        /// The pose reference is nulled rather than left behind. The source is a
        /// <c>MonoBehaviour</c>, and holding one past its <c>OnDisable</c> keeps a destroyed
        /// GameObject's managed wrapper alive and makes the next capture read a
        /// <c>Rigidbody</c> Unity has already torn down.
        /// </remarks>
        public bool Remove(ushort vehicleId)
        {
            if (vehicleId == 0 || vehicleId > _capacity || !_live[vehicleId]) return false;

            _live[vehicleId]  = false;
            _poses[vehicleId] = null;
            _states[vehicleId] = default;
            ClearSeats(vehicleId);

            for (int i = 0; i < _liveCount; i++)
            {
                if (_liveIds[i] != vehicleId) continue;
                _liveIds[i] = _liveIds[--_liveCount];
                break;
            }

            return true;
        }

        /// <summary>True when this id names a registered vehicle.</summary>
        public bool Contains(ushort vehicleId)
            => vehicleId != 0 && vehicleId <= _capacity && _live[vehicleId];

        /// <summary>Reads a vehicle's authoritative state.</summary>
        public bool TryGetState(ushort vehicleId, out VehicleState state)
        {
            if (!Contains(vehicleId))
            {
                state = default;
                return false;
            }

            state = _states[vehicleId];
            return true;
        }

        /// <summary>
        /// Writes a vehicle's authoritative state back.
        /// </summary>
        /// <remarks>
        /// A read-modify-write pair rather than a <c>ref</c> accessor. A <c>ref</c> would be one
        /// fewer copy of a 40-byte struct on a path that runs at most 16 times a tick, and it
        /// would hand every caller a permanent write handle into the registry's backing array —
        /// which is how a second writer of <c>Health</c> appears without anybody deciding to add
        /// one. A damage sink going through a named method is
        /// the property phase-05 D9 bought for actors.
        /// </remarks>
        public bool TrySetState(ushort vehicleId, in VehicleState state)
        {
            if (!Contains(vehicleId)) return false;

            _states[vehicleId] = state;
            _states[vehicleId].VehicleId = vehicleId;   // an id swap here would be unrecoverable
            return true;
        }

        /// <summary>The live ids, in no particular order. Valid for <see cref="LiveCount"/>.</summary>
        /// <remarks>
        /// Exposed as the backing array rather than copied out, because the two callers that
        /// want it (capture and the burn clock) run per tick. It is the registry's array: a
        /// caller that writes to it corrupts the registry, which is the price of not allocating.
        /// </remarks>
        public ushort[] LiveIds => _liveIds;

        // ------------------------------------------------------------------ occupancy

        /// <summary>Who is in a seat, or 0 for empty.</summary>
        public ushort OccupantOf(ushort vehicleId, byte seatIndex)
            => IsSeatInRange(vehicleId, seatIndex)
                ? _occupants[SeatSlot(vehicleId, seatIndex)]
                : (ushort)0;

        /// <summary>
        /// Seats an actor, or clears the seat when <paramref name="actorId"/> is 0.
        /// </summary>
        /// <returns>False for an unknown vehicle or a seat index the vehicle does not have.</returns>
        public bool TrySetOccupant(ushort vehicleId, byte seatIndex, ushort actorId)
        {
            if (!IsSeatInRange(vehicleId, seatIndex)) return false;

            _occupants[SeatSlot(vehicleId, seatIndex)] = actorId;
            return true;
        }

        /// <summary>Finds where an actor is sitting, if anywhere.</summary>
        /// <remarks>
        /// A scan over at most 16 x 8 slots, run once per seat request rather than per tick.
        /// A reverse index would be a second copy of the same fact, and the failure mode of a
        /// stale reverse index is an actor the arbiter believes is seated in a vehicle that no
        /// longer exists — which is precisely the class of bug V4-D10 removes elsewhere.
        /// </remarks>
        public bool TryFindSeatOf(ushort actorId, out ushort vehicleId, out byte seatIndex)
        {
            if (actorId != 0)
            {
                for (int i = 0; i < _liveCount; i++)
                {
                    ushort id = _liveIds[i];
                    int seats = _states[id].SeatCount;

                    for (int s = 0; s < seats; s++)
                    {
                        if (_occupants[SeatSlot(id, (byte)s)] != actorId) continue;

                        vehicleId = id;
                        seatIndex = (byte)s;
                        return true;
                    }
                }
            }

            vehicleId = 0;
            seatIndex = 0;
            return false;
        }

        /// <summary>Empties every seat of a vehicle. Used when it dies.</summary>
        public void ClearSeats(ushort vehicleId)
        {
            if (vehicleId == 0 || vehicleId > _capacity) return;

            int start = SeatSlot(vehicleId, 0);
            for (int s = 0; s < VehicleState.MaxSeats; s++) _occupants[start + s] = 0;
        }

        // -------------------------------------------------------------------- capture

        /// <summary>
        /// Fills <paramref name="destination"/> with one entry per live vehicle, quantized and
        /// ready for <see cref="VehicleDeltaEncoder"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every entry leaves with <see cref="VehicleField.Full"/>. The encoder recomputes the
        /// real mask against each client's own acked baseline and overwrites this — setting
        /// anything narrower here would be one viewer's opinion applied to all of them.
        /// </para>
        /// <para>
        /// <b>Quantization happens here, once, not per viewer.</b> That is what makes change
        /// detection meaningful: a vehicle idling on a slope whose float position jitters below
        /// the 6.25 cm quantum produces identical bytes and keeps its Position bit clear
        /// (see <see cref="VehicleWorldSnapshot"/>).
        /// </para>
        /// </remarks>
        public void CaptureInto(VehicleWorldSnapshot destination, uint serverTick)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            destination.Clear();
            destination.ServerTick = serverTick;

            for (int i = 0; i < _liveCount; i++)
            {
                ushort id = _liveIds[i];
                IVehiclePoseSource? pose = _poses[id];
                if (pose == null) continue;   // torn down between registration and capture

                pose.ReadPose(
                    out Vec3 position,
                    out float rx, out float ry, out float rz, out float rw,
                    out Vec3 linear,
                    out Vec3 angular);

                pose.ReadSubtypeTail(out byte subtypeA, out byte subtypeB);

                ref VehicleState state = ref _states[id];

                // X-39: see SnapshotBuilder.Capture. Eight of Dustbowl's fourteen vehicles
                // reported a saturated X in one 120 s run and nothing said so.
                World.PositionSaturationLog.Observe(isVehicle: true, id, in position);

                var entry = new VehicleSnapshotEntry
                {
                    VehicleId   = id,
                    ChangeMask  = VehicleField.Full,

                    PosX = Quantize.PackPos(position.X),
                    PosY = Quantize.PackPos(position.Y),
                    PosZ = Quantize.PackPos(position.Z),

                    Rotation = Quantize.PackQuat(rx, ry, rz, rw),

                    VelX = Quantize.PackVel16(linear.X),
                    VelY = Quantize.PackVel16(linear.Y),
                    VelZ = Quantize.PackVel16(linear.Z),

                    AngVelX = Quantize.PackAngVel(angular.X),
                    AngVelY = Quantize.PackAngVel(angular.Y),
                    AngVelZ = Quantize.PackAngVel(angular.Z),

                    Health = state.NormalizedHealth,
                    Flags  = BuildFlags(in state, pose),

                    TurretYaw   = Quantize.PackYaw(pose.TurretYaw),
                    TurretPitch = Quantize.PackPitchByte(pose.TurretPitch),

                    SubtypeA = subtypeA,
                    SubtypeB = subtypeB,
                };

                destination.Add(in entry);
            }
        }

        /// <summary>Forgets every vehicle. For a round boundary.</summary>
        public void Clear()
        {
            for (int i = 0; i < _liveCount; i++)
            {
                ushort id = _liveIds[i];
                _live[id]   = false;
                _poses[id]  = null;
                _states[id] = default;
                ClearSeats(id);
            }

            _liveCount = 0;
        }

        private static VehicleStateFlags BuildFlags(in VehicleState state, IVehiclePoseSource pose)
        {
            VehicleStateFlags flags = VehicleStateFlags.None;

            if (state.Dead)     flags |= VehicleStateFlags.Dead;
            if (state.Burning)  flags |= VehicleStateFlags.Burning;
            if (pose.IsInWater) flags |= VehicleStateFlags.InWater;

            // Read from the pose source rather than stored, because it is a physics fact that
            // changes several times a second and nothing on the server decides it.
            if (pose.IsAirborne) flags |= VehicleStateFlags.Airborne;

            return flags;
        }

        private bool IsSeatInRange(ushort vehicleId, byte seatIndex)
            => Contains(vehicleId)
               && seatIndex < _states[vehicleId].SeatCount
               && seatIndex < VehicleState.MaxSeats;

        private static int SeatSlot(ushort vehicleId, byte seatIndex)
            => vehicleId * VehicleState.MaxSeats + seatIndex;
    }
}
