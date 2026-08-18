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
    /// <b>Deaths accumulate into a pending queue that the caller drains; they are not raised as
    /// events and they are NOT scoped to one call.</b> An event on the tick path is an invocation
    /// list this class would own and a re-entrancy question at every subscriber. But the reason
    /// the queue outlives a single call is sharper: deaths arrive from <b>two stages</b>.
    /// <see cref="KillImmediately"/> fires from the input stage, when a crash resolves inside
    /// <c>Vehicle.Damage</c>; <see cref="Tick"/> fires from the snapshot stage. A buffer that each
    /// of them reset on entry would have the snapshot stage silently discard every crash death
    /// that happened earlier in the same frame — the vehicle would be marked dead in the registry,
    /// stop appearing in snapshots, and never be despawned, so every client would hold a wreck
    /// forever with nothing anywhere to say why.
    /// </para>
    /// </remarks>
    public sealed class VehicleBurnClock
    {
        private readonly VehicleRegistry _registry;
        private readonly ushort[] _pendingDeaths;
        private int _pendingCount;

        public VehicleBurnClock(VehicleRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            // Capacity is the true bound: at most that many vehicles exist, and MarkDead refuses
            // a second death for an id, so no id can occupy two slots.
            _pendingDeaths = new ushort[registry.Capacity];
        }

        /// <summary>Vehicles that started burning since construction.</summary>
        public long BurnsStarted { get; private set; }

        /// <summary>Vehicles that died since construction.</summary>
        public long DeathsAnnounced { get; private set; }

        /// <summary>
        /// Ids that have died and not yet been announced. Valid for
        /// <see cref="PendingDeathCount"/> entries; cleared by
        /// <see cref="ClearPendingDeaths"/> and by nothing else.
        /// </summary>
        public ushort[] PendingDeaths => _pendingDeaths;

        /// <summary>How many of <see cref="PendingDeaths"/> are live.</summary>
        public int PendingDeathCount => _pendingCount;

        /// <summary>
        /// Drops the pending list. <b>Call this only after every id in it has been announced</b> —
        /// a death dropped here is a wreck no client will ever be told to remove.
        /// </summary>
        public void ClearPendingDeaths() => _pendingCount = 0;

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
        /// Puts a burn out, for a vehicle that has been repaired back above zero.
        /// </summary>
        /// <remarks>
        /// <b>Without this the burn clock kills a repaired vehicle.</b> <c>Vehicle.Repair</c>
        /// reaches <c>StopBurning()</c> after three repairs, which clears the scene's
        /// <c>burning</c> flag and knows nothing about <see cref="VehicleState"/> — so
        /// <see cref="Tick"/> still found <c>Burning</c> true with <c>BurnEndsAtTick</c> armed,
        /// and despawned a drivable, occupied vehicle on schedule while the GameObject stayed
        /// solid in the world.
        /// </remarks>
        /// <returns>False when the vehicle is unknown, dead, or was not burning.</returns>
        public bool CancelBurn(ushort vehicleId)
        {
            if (!_registry.TryGetState(vehicleId, out VehicleState state)) return false;
            if (state.Dead || !state.Burning) return false;

            state.Burning        = false;
            state.BurnEndsAtTick = 0;
            _registry.TrySetState(vehicleId, in state);

            BurnsExtinguished++;
            return true;
        }

        /// <summary>Burns put out by repair rather than by reaching their end.</summary>
        public long BurnsExtinguished { get; private set; }

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
            if (!MarkDead(vehicleId)) return false;

            Enqueue(vehicleId);
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

                if (MarkDead(id)) Enqueue(id);
            }
        }

        /// <summary>Resets the counters and drops any pending death. The registry owns the vehicles.</summary>
        public void Reset()
        {
            _pendingCount   = 0;
            BurnsStarted    = 0;
            DeathsAnnounced = 0;
        }

        /// <summary>
        /// Appends a death, refusing to overflow rather than throwing.
        /// </summary>
        /// <remarks>
        /// The bound cannot be reached — <see cref="MarkDead"/> admits each id once and there are
        /// at most <c>Capacity</c> of them — so this guard exists to make that reasoning
        /// falsifiable rather than to handle a case. An <c>IndexOutOfRangeException</c> here would
        /// come out of the snapshot stage and take the tick loop down for every client.
        /// </remarks>
        private void Enqueue(ushort vehicleId)
        {
            if (_pendingCount >= _pendingDeaths.Length) return;

            _pendingDeaths[_pendingCount++] = vehicleId;
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
