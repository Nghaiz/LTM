using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The two-stage vehicle death machine: zero health starts a burn, and the burn ending is
    /// what kills. V4-D11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Vehicles do not die at zero health, and the wire reflects that.</b>
    /// <c>Vehicle.ApplyHealth</c> sets <c>burning</c> when health reaches zero;
    /// <c>Vehicle.FixedUpdate</c> counts <c>burnTime</c> down and calls <c>Die()</c> when it
    /// passes zero. A server that killed at zero health would ship a game in which no vehicle
    /// ever burns — a visible, dramatic difference from single-player that no test asserting
    /// "damage kills" would catch.
    /// </para>
    /// <para>
    /// <b>Tick-counted, not <c>Time.deltaTime</c>.</b> The shipped countdown reads
    /// <c>Time.deltaTime</c> inside <c>FixedUpdate</c>, where it happens to return
    /// <c>fixedDeltaTime</c> — correct BY ACCIDENT, and silently wrong the moment the burn is
    /// driven from the netcode's own 30 Hz accumulator instead, which is exactly what this class
    /// does (design § 3.3). V0 already changed that line to <c>Time.fixedDeltaTime</c> for zero
    /// behaviour change today; this is the reason it mattered.
    /// </para>
    /// <para>
    /// <b><c>burnTime</c> is not replicated</b>, because the field is simultaneously the
    /// serialized designer default and the live countdown — a client receiving it could not tell
    /// which of the two it held. Clients run a cosmetic burn off the <c>Burning</c> flag bit; the
    /// server owns when it ends and announces the end as <c>S_VEHICLE_DESPAWN</c>.
    /// </para>
    /// <para>
    /// <b>Deaths are drained, not raised as events.</b> An event on the tick path is an
    /// invocation list this class would own and a re-entrancy question at every subscriber;
    /// draining into a caller-supplied buffer keeps the ordering the caller's and allocates
    /// nothing.
    /// </para>
    /// </remarks>
    public sealed class VehicleBurnClock
    {
        private readonly VehicleRegistry _registry;
        private readonly ushort[] _diedThisTick;
        private int _diedCount;

        public VehicleBurnClock(VehicleRegistry registry)
        {
            _registry     = registry ?? throw new ArgumentNullException(nameof(registry));
            _diedThisTick = new ushort[registry.Capacity];
        }

        /// <summary>Vehicles that started burning since construction.</summary>
        public long BurnsStarted { get; private set; }

        /// <summary>Vehicles that died since construction.</summary>
        public long DeathsAnnounced { get; private set; }

        /// <summary>
        /// Ids that died on the most recent <see cref="Tick"/>. Valid for
        /// <see cref="DiedThisTickCount"/> entries, and overwritten by the next call.
        /// </summary>
        public ushort[] DiedThisTick => _diedThisTick;

        /// <summary>How many of <see cref="DiedThisTick"/> are live.</summary>
        public int DiedThisTickCount => _diedCount;

        /// <summary>
        /// Lights the fire on a vehicle whose health has just reached zero.
        /// </summary>
        /// <param name="burnTicks">
        /// How long it burns. <c>Vehicle.burnTime</c> in seconds times
        /// <see cref="ProtocolConstants.SIM_TICK_RATE"/>, converted by the caller because the
        /// seconds value is a per-prefab designer field this class never sees.
        /// </param>
        /// <returns>
        /// False when the vehicle is unknown, already burning, or already dead — so a second
        /// call cannot restart a countdown that is halfway through.
        /// </returns>
        public bool StartBurning(ushort vehicleId, int burnTicks, uint nowTick)
        {
            if (!_registry.TryGetState(vehicleId, out VehicleState state)) return false;
            if (state.Burning || state.Dead) return false;

            state.Burning = true;

            // A non-positive burn time means "no burn stage" — a crash on a crashSkipsBurn
            // vehicle, or a prefab that authored 0. Ending it on THIS tick rather than
            // special-casing the caller keeps one death path, so the despawn reason and the
            // snapshot-suppression below cannot diverge between the two.
            state.BurnEndsAtTick = nowTick + (uint)(burnTicks > 0 ? burnTicks : 0);

            _registry.TrySetState(vehicleId, in state);
            BurnsStarted++;
            return true;
        }

        /// <summary>
        /// Kills a vehicle immediately, skipping the burn.
        /// </summary>
        /// <remarks>
        /// For <c>Vehicle.crashSkipsBurn</c>, which the shipped code honours by calling
        /// <c>Die()</c> straight out of a hard collision. Routed through the same
        /// <see cref="MarkDead"/> as the burn's own expiry so both produce one despawn, once.
        /// </remarks>
        public bool KillImmediately(ushort vehicleId)
        {
            _diedCount = 0;

            if (!MarkDead(vehicleId)) return false;

            _diedThisTick[_diedCount++] = vehicleId;
            return true;
        }

        /// <summary>
        /// Advances every burning vehicle one tick and collects the ones that died.
        /// </summary>
        /// <remarks>
        /// Walks the registry's live-id list rather than keeping a separate burning set. At
        /// <see cref="ProtocolConstants.MAX_VEHICLES"/> = 16 the scan is free, and a second
        /// collection tracking who is burning is a second thing that can disagree with
        /// <c>VehicleState.Burning</c> — which is the divergence class this whole phase spends
        /// its budget removing.
        /// </remarks>
        public void Tick(uint nowTick)
        {
            _diedCount = 0;

            ushort[] liveIds = _registry.LiveIds;

            // A copy of the count taken up front: MarkDead does not remove from the registry
            // (the despawn does), but reading the count once is what makes the loop bound
            // obvious rather than something a reader has to prove.
            int count = _registry.LiveCount;

            for (int i = 0; i < count; i++)
            {
                ushort id = liveIds[i];

                if (!_registry.TryGetState(id, out VehicleState state)) continue;
                if (!state.Burning || state.Dead) continue;

                // Signed distance, so a tick counter that has wrapped past 2^32 — 4.5 years at
                // 30 Hz, and a dedicated server runs for months — does not read as a
                // two-billion-tick overdue burn on every vehicle at once.
                if (SequenceMath.Distance32(nowTick, state.BurnEndsAtTick) < 0) continue;

                if (MarkDead(id)) _diedThisTick[_diedCount++] = id;
            }
        }

        /// <summary>Resets the counters. The registry owns the vehicles.</summary>
        public void Reset()
        {
            _diedCount      = 0;
            BurnsStarted    = 0;
            DeathsAnnounced = 0;
        }

        private bool MarkDead(ushort vehicleId)
        {
            if (!_registry.TryGetState(vehicleId, out VehicleState state)) return false;
            if (state.Dead) return false;

            state.Dead    = true;
            state.Burning = false;
            state.Health  = 0f;
            _registry.TrySetState(vehicleId, in state);

            // Everyone aboard is out. The engine-side Vehicle.Die() ejects and hurts them; this
            // is the arbiter's record catching up, so the next seat request for that vehicle is
            // refused on RejectedVehicleDead rather than on a seat that looks free.
            _registry.ClearSeats(vehicleId);

            DeathsAnnounced++;
            return true;
        }
    }
}
