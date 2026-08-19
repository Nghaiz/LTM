using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The server's authoritative turret aim, one <see cref="TurretAimState"/> per
    /// <c>(vehicleId, seatIndex)</c>. V6 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the answer to "where is the turret pointing", and the joint is not.</b> Before
    /// V6 the only copy of a turret's aim lived inside a <c>ConfigurableJoint</c> on a prefab, so
    /// the server had nothing to capture and nothing to grade — <c>VehicleGameplaySource.TurretYaw</c>
    /// shipped a literal <c>0f</c> for two phases while the wire field it fed was already
    /// allocated. Holding the pose here instead makes the whole of V6-D2 testable without Unity:
    /// what decides where a shell goes is this state at the tick <c>Shoot</c> runs.
    /// </para>
    /// <para>
    /// <b>The client's request is a target, never the pose.</b> <see cref="SetTarget"/> records
    /// what the occupant asked for and <see cref="Step"/> walks toward it through
    /// <see cref="TurretAimCore.StepToward"/>, so an axis of 10<sup>6</sup> or a 180° snap buys
    /// exactly one step's arc. A client that stops sending holds its last target rather than
    /// centring: unlike a throttle, a turret left alone is a turret that stays where it was
    /// pointed, and centring one on packet loss would swing the gun off target for a driver whose
    /// connection hiccuped. The hold window that matters is
    /// <see cref="Server.VehicleInputAuthority"/>'s, and it governs the axes, not this.
    /// </para>
    /// <para>
    /// <b>Fixed-capacity, indexed, and allocation-free.</b> Sized
    /// <c>(MAX_VEHICLES + 1) x <see cref="VehicleState.MaxSeats"/></c> and allocated once — the
    /// same shape <see cref="BotSeatClaims"/> uses, and for the same reason: this is stepped for
    /// every occupied turret on every tick.
    /// </para>
    /// </remarks>
    public sealed class ServerTurretAuthority
    {
        private struct Slot
        {
            public bool Live;
            public bool HasTarget;
            public float TargetYaw;
            public float TargetPitch;
            public TurretAimState Aim;
            public TurretAimLimits Limits;
        }

        private readonly Slot[] _slots;
        private readonly int _capacity;

        /// <summary>
        /// Limits used for a turret whose prefab has not declared its own.
        /// </summary>
        /// <remarks>
        /// The shipped <c>MountedTurret</c> numbers — 600 °/s traverse (its per-frame 10° at the
        /// design 60 Hz) and the <c>[-40, +15]</c> stops that were inline literals. A default that
        /// refused to move at all would be worse than a wrong one: a turret that never traverses
        /// reads as "replication is broken" rather than as "this prefab was not authored".
        /// </remarks>
        public static readonly TurretAimLimits DefaultLimits = new TurretAimLimits
        {
            YawRateDegPerSec   = 600f,
            PitchRateDegPerSec = 600f,
            PitchMin           = -40f,
            PitchMax           = 15f,
        };

        public ServerTurretAuthority(int maxVehicles = ProtocolConstants.MAX_VEHICLES)
        {
            _capacity = maxVehicles + 1;
            _slots    = new Slot[_capacity * VehicleState.MaxSeats];
        }

        /// <summary>Turrets currently tracked. Zero is what a clean teardown looks like.</summary>
        public int TrackedCount { get; private set; }

        /// <summary>Targets refused because the seat index or vehicle id was out of range.</summary>
        public long RefusedOutOfRange { get; private set; }

        /// <summary>
        /// Starts tracking a turret, at the pose the prefab was authored with.
        /// </summary>
        /// <remarks>
        /// Idempotent on the aim: re-registering an already-tracked turret refreshes its limits
        /// and leaves the pose alone. A gunner leaving and re-entering must not snap the gun back
        /// to the prefab's rest pose, which is what a plain overwrite would do.
        /// </remarks>
        public bool Register(
            ushort vehicleId, byte seatIndex, in TurretAimLimits limits,
            float seedYaw = 0f, float seedPitch = 0f)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;

            ref Slot slot = ref _slots[index];
            slot.Limits = limits;

            if (slot.Live) return true;

            slot.Live      = true;
            slot.HasTarget = false;
            slot.Aim.Yaw   = TurretAimCore.WrapDegrees(seedYaw);
            slot.Aim.Pitch = TurretAimCore.ClampPitch(seedPitch, in limits);

            TrackedCount++;
            return true;
        }

        /// <summary>Stops tracking a turret. Vehicle despawn and round teardown.</summary>
        public bool Unregister(ushort vehicleId, byte seatIndex)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;
            if (!_slots[index].Live) return false;

            _slots[index] = default;
            TrackedCount--;
            return true;
        }

        /// <summary>Drops every turret on a vehicle. Called from the despawn path.</summary>
        public void UnregisterVehicle(ushort vehicleId)
        {
            for (byte seat = 0; seat < VehicleState.MaxSeats; seat++)
                Unregister(vehicleId, seat);
        }

        /// <summary>True when this seat's turret is being tracked.</summary>
        public bool IsTracked(ushort vehicleId, byte seatIndex)
            => TryIndex(vehicleId, seatIndex, out int index) && _slots[index].Live;

        /// <summary>
        /// Records what the seat's occupant is asking the turret to point at. Degrees, from
        /// <c>C_VEHICLE_INPUT</c>.
        /// </summary>
        /// <remarks>
        /// Recorded rather than applied. Applying here would make the traverse a function of how
        /// often the client sends, which is the framerate bug this phase closes wearing a
        /// different hat.
        /// </remarks>
        public bool SetTarget(ushort vehicleId, byte seatIndex, float yawDegrees, float pitchDegrees)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) { RefusedOutOfRange++; return false; }

            ref Slot slot = ref _slots[index];
            if (!slot.Live) return false;

            slot.HasTarget   = true;
            slot.TargetYaw   = yawDegrees;
            slot.TargetPitch = pitchDegrees;
            return true;
        }

        /// <summary>
        /// Clears a turret's target so it holds its current pose. Seat exit and disconnect.
        /// </summary>
        public void ClearTarget(ushort vehicleId, byte seatIndex)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return;

            ref Slot slot = ref _slots[index];
            slot.HasTarget   = false;
            slot.TargetYaw   = 0f;
            slot.TargetPitch = 0f;
        }

        /// <summary>
        /// Advances every tracked turret one fixed step toward its target.
        /// </summary>
        /// <remarks>
        /// Must run BEFORE fire resolution in the server tick — <c>Weapon.SpawnProjectile</c>
        /// reads <c>configuration.muzzle.position</c>, which is the transform this settles. The
        /// other order produces shots that leave from where the turret pointed LAST tick: 33 ms
        /// of lag, invisible on a static target and systematically wrong on a traversing one.
        /// </remarks>
        public void Step(float dt)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ref Slot slot = ref _slots[i];
                if (!slot.Live || !slot.HasTarget) continue;

                TurretAimCore.StepToward(
                    ref slot.Aim, slot.TargetYaw, slot.TargetPitch, in slot.Limits, dt);
            }
        }

        /// <summary>The authoritative aim for one turret.</summary>
        public bool TryGetAim(ushort vehicleId, byte seatIndex, out TurretAimState aim)
        {
            aim = default;
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;
            if (!_slots[index].Live) return false;

            aim = _slots[index].Aim;
            return true;
        }

        /// <summary>
        /// The aim of the vehicle's <b>first</b> tracked turret, which is the one the snapshot
        /// entry's single turret slot carries (V6-D3).
        /// </summary>
        /// <remarks>
        /// A second turret on the same vehicle is drawn from its occupant's already-replicated
        /// actor rotation and holds its last pose when vacant. The residual error is cosmetic by
        /// construction: no shot ever reads a remote client's transform, because V7's
        /// <c>S_PROJECTILE_SPAWN</c> carries a server-computed origin.
        /// </remarks>
        public bool TryGetPrimaryAim(ushort vehicleId, out TurretAimState aim)
        {
            for (byte seat = 0; seat < VehicleState.MaxSeats; seat++)
                if (TryGetAim(vehicleId, seat, out aim)) return true;

            aim = default;
            return false;
        }

        /// <summary>Overwrites a turret's aim outright. Seeding, and tests.</summary>
        public bool SetAim(ushort vehicleId, byte seatIndex, float yawDegrees, float pitchDegrees)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;

            ref Slot slot = ref _slots[index];
            if (!slot.Live) return false;

            slot.Aim.Yaw   = TurretAimCore.WrapDegrees(yawDegrees);
            slot.Aim.Pitch = TurretAimCore.ClampPitch(pitchDegrees, in slot.Limits);
            return true;
        }

        /// <summary>Forgets every turret and every counter. Round teardown.</summary>
        public void Reset()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = default;

            TrackedCount      = 0;
            RefusedOutOfRange = 0;
        }

        private bool TryIndex(ushort vehicleId, byte seatIndex, out int index)
        {
            index = 0;
            if (vehicleId == 0 || vehicleId >= _capacity) return false;
            if (seatIndex >= VehicleState.MaxSeats) return false;

            index = vehicleId * VehicleState.MaxSeats + seatIndex;
            return true;
        }
    }
}
