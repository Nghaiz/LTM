using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The server's mounted weapons, one entry per <c>(vehicleId, seatIndex)</c>. V6 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed by seat, not by actor, and that is the whole difference from
    /// <see cref="ServerCombatAuthority"/>.</b> An infantry weapon belongs to whoever is carrying
    /// it, so its state travels with the <c>actorId</c>. A mounted weapon belongs to the vehicle:
    /// its clip, its cooldown and its spare rounds survive the gunner getting out, and must,
    /// because two players swapping seats on a half-empty coaxial must not each find a full one.
    /// </para>
    /// <para>
    /// <b>Fixed-capacity arrays over a packed key, never a dictionary.</b> Sized
    /// <c>(MAX_VEHICLES + 1) x <see cref="VehicleState.MaxSeats"/></c> and allocated once, the
    /// same shape <see cref="ServerTurretAuthority"/> and <see cref="BotSeatClaims"/> use. A
    /// dictionary would allocate on the 30 Hz fire path and buy nothing over a dense index.
    /// </para>
    /// <para>
    /// <b>It participates in <c>AssertCleanState()</c></b> (brainstorm criterion 13). A mounted
    /// weapon that outlives its vehicle is a leak that shows up on the second or third round of a
    /// server nobody is watching, which is exactly the class of bug the audit exists for.
    /// </para>
    /// </remarks>
    public sealed class MountedWeaponRegistry
    {
        private struct Entry
        {
            public bool Live;
            public byte WeaponId;
            public WeaponConfig Config;
            public WeaponRuntimeState State;
        }

        private readonly Entry[] _entries;
        private readonly int _capacity;

        public MountedWeaponRegistry(int maxVehicles = ProtocolConstants.MAX_VEHICLES)
        {
            _capacity = maxVehicles + 1;
            _entries  = new Entry[_capacity * VehicleState.MaxSeats];
        }

        /// <summary>Mounted weapons currently tracked. Zero is a clean teardown.</summary>
        public int TrackedCount { get; private set; }

        /// <summary>
        /// Starts tracking a mounted weapon at a full clip.
        /// </summary>
        /// <remarks>
        /// Idempotent: re-registering an already-tracked weapon is a no-op that returns true, so a
        /// gunner re-entering a seat does not silently re-arm the gun. That re-arm is not
        /// hypothetical — <c>Seat.SetOccupant</c> runs on every entry.
        /// </remarks>
        public bool Register(ushort vehicleId, byte seatIndex, byte weaponId, in WeaponConfig config)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;

            ref Entry entry = ref _entries[index];
            if (entry.Live) return true;

            entry.Live     = true;
            entry.WeaponId = weaponId;
            entry.Config   = config;
            entry.State    = WeaponRuntimeState.Loaded(in config);

            TrackedCount++;
            return true;
        }

        /// <summary>Stops tracking one mounted weapon.</summary>
        public bool Unregister(ushort vehicleId, byte seatIndex)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;
            if (!_entries[index].Live) return false;

            _entries[index] = default;
            TrackedCount--;
            return true;
        }

        /// <summary>Drops every mounted weapon on a vehicle. The despawn path.</summary>
        public void UnregisterVehicle(ushort vehicleId)
        {
            for (byte seat = 0; seat < VehicleState.MaxSeats; seat++)
                Unregister(vehicleId, seat);
        }

        /// <summary>True when this seat carries a tracked mounted weapon.</summary>
        public bool IsTracked(ushort vehicleId, byte seatIndex)
            => TryIndex(vehicleId, seatIndex, out int index) && _entries[index].Live;

        /// <summary>The weapon id, or <see cref="WeaponIds.NONE"/>.</summary>
        public byte WeaponIdOf(ushort vehicleId, byte seatIndex)
            => TryIndex(vehicleId, seatIndex, out int index) && _entries[index].Live
                ? _entries[index].WeaponId
                : WeaponIds.NONE;

        /// <summary>The server's numbers for this weapon. <see cref="WeaponCatalog.Inert"/> when untracked.</summary>
        public WeaponConfig ConfigOf(ushort vehicleId, byte seatIndex)
            => TryIndex(vehicleId, seatIndex, out int index) && _entries[index].Live
                ? _entries[index].Config
                : WeaponCatalog.Inert;

        /// <summary>A copy of the runtime state, for a HUD, a snapshot or a test.</summary>
        public bool TryGetState(ushort vehicleId, byte seatIndex, out WeaponRuntimeState state)
        {
            state = default;
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;
            if (!_entries[index].Live) return false;

            state = _entries[index].State;
            return true;
        }

        /// <summary>
        /// The live runtime state, by reference, so a caller mutates the registry's copy rather
        /// than a snapshot of it.
        /// </summary>
        /// <remarks>
        /// <b>Returning a copy here would be the silent version of doing nothing.</b>
        /// <see cref="WeaponRuntimeState"/> is a struct: an authority handed a copy would spend
        /// ammo, run the cooldown and complete reloads against storage that is thrown away at the
        /// end of the tick, and every one of those operations would still return success.
        /// </remarks>
        public ref WeaponRuntimeState StateRef(ushort vehicleId, byte seatIndex, out bool found)
        {
            if (TryIndex(vehicleId, seatIndex, out int index) && _entries[index].Live)
            {
                found = true;
                return ref _entries[index].State;
            }

            found = false;
            return ref _scratch;
        }

        /// <summary>
        /// Sets a tracked weapon's spare rounds. Authoring and resupply.
        /// </summary>
        public bool SetSpareAmmo(ushort vehicleId, byte seatIndex, short spareAmmo)
        {
            if (!TryIndex(vehicleId, seatIndex, out int index)) return false;
            if (!_entries[index].Live) return false;

            _entries[index].State.SpareAmmo = spareAmmo;
            return true;
        }

        /// <summary>Forgets every mounted weapon. Round teardown.</summary>
        public void Reset()
        {
            for (int i = 0; i < _entries.Length; i++) _entries[i] = default;
            TrackedCount = 0;
        }

        /// <summary>
        /// The sink for a <see cref="StateRef"/> miss, so the ref return has somewhere to point.
        /// </summary>
        /// <remarks>
        /// Writes to it are discarded on purpose and the caller is told via <c>found</c>. A throw
        /// would be worse here: the miss happens when a vehicle is torn down between the seat
        /// lookup and the fire step, which is an ordinary race rather than a programming error.
        /// </remarks>
        private WeaponRuntimeState _scratch;

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
