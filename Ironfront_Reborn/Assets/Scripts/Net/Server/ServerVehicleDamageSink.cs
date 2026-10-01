using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The one place vehicle health is written on the server. V4 task 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase-05 D9's rule, one entity type over: one number, not a mirror that can drift. Every
    /// vehicle damage source in the shipped game already funnels through <c>Vehicle.Damage</c>
    /// — the ram check, <c>AutoDamage</c>, explosions, bullets — so the role guard there is the
    /// choke point and this is what it routes to.
    /// </para>
    /// <para>
    /// <b>Zero health starts a burn; it does not kill</b> (V4-D11). <c>Vehicle.ApplyHealth</c>
    /// sets <c>burning</c> at zero and <c>Die()</c> arrives from the <c>burnTime</c> countdown.
    /// Killing here instead would ship a game in which no vehicle ever burns — a visible
    /// difference from single-player that a test asserting "damage kills" would pass.
    /// </para>
    /// <para>
    /// <b>The authoritative number lives in two places on purpose, and one of them is a
    /// projection.</b> <see cref="VehicleState.Health"/> is what the snapshot is built from and
    /// what the arbiter reads; <c>Vehicle.Health</c> is what the scene's own AI, ramming and
    /// repair logic read. This sink writes both in one call, in that order, so there is exactly
    /// one writer — which is the property phase-05 bought for actors and the reason
    /// <c>Vehicle.SetHealthAuthoritative</c> exists at all.
    /// </para>
    /// </remarks>
    internal sealed class ServerVehicleDamageSink : IVehicleDamageSink
    {
        private readonly ServerVehicleRegistry _vehicles;
        private readonly VehicleBurnClock _burnClock;
        private readonly Func<uint> _currentTick;

        /// <summary>
        /// Who last took health off each vehicle while it was still alive, and on which tick. The
        /// crew that dies with it is credited to that actor (feature 2, 2026-09-29).
        /// </summary>
        private readonly Dictionary<ushort, LastAttack> _lastAttacks = new Dictionary<ushort, LastAttack>();

        internal ServerVehicleDamageSink(
            ServerVehicleRegistry vehicles, VehicleBurnClock burnClock, Func<uint> currentTick)
        {
            _vehicles    = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            _burnClock   = burnClock ?? throw new ArgumentNullException(nameof(burnClock));
            _currentTick = currentTick ?? throw new ArgumentNullException(nameof(currentTick));
        }

        /// <summary>Damage applications that named no registered vehicle.</summary>
        public long UnknownVehicles { get; private set; }

        /// <summary>Damage applied to vehicles that were already dead. Free; nothing happens.</summary>
        public long DamageToWrecks { get; private set; }

        /// <inheritdoc />
        public VehicleDamageOutcome ApplyDamage(ushort vehicleId, float amount, ushort attackerId)
        {
            VehicleRegistry registry = _vehicles.Registry;

            if (!registry.TryGetState(vehicleId, out VehicleState state))
            {
                UnknownVehicles++;
                return VehicleDamageOutcome.NoOp;
            }

            if (state.Dead)
            {
                DamageToWrecks++;
                return VehicleDamageOutcome.NoOp;
            }

            // Before the burn starts, not during it: the hit that empties a vehicle decides its
            // fate, and a shot into the fire four seconds later must not steal the credit for
            // the crew. An attacker of 0 is NetVehicleAuthority's "unknown" -- a crash, AutoDamage.
            if (!state.Burning && attackerId != 0 && amount > 0f)
                _lastAttacks[vehicleId] = new LastAttack(attackerId, _currentTick());

            float remaining = state.Health - amount;
            if (remaining < 0f) remaining = 0f;

            state.Health = remaining;
            registry.TrySetState(vehicleId, in state);

            // The scene's copy, through the entry point V0 opened for this caller. Written even
            // when the vehicle is already burning, because the AI reads Vehicle.Health to decide
            // whether a vehicle is worth taking.
            if (_vehicles.TryFind(vehicleId, out IGameplayVehicleSource source))
                source.SetHealthAuthoritative(remaining);

            if (remaining > 0f || state.Burning)
                return new VehicleDamageOutcome(remaining, startedBurning: false, died: false);

            // A crashSkipsBurn vehicle has no burn stage at all, and it dies HERE rather than on
            // the next snapshot tick. Expressing it as a zero-tick burn would compile and behave
            // almost right: the vehicle would carry the Burning flag for one tick and die up to
            // 50 ms later, which is a flicker no designer asked for on the one vehicle class
            // whose whole point is that it does not burn.
            //
            // Both paths still go through the burn clock, so the despawn is announced from one
            // place — two death paths is how a wreck ends up announced twice or not at all.
            // A hull the water drowned (Vehicle.IsFlooded) does not catch fire either: it dies here,
            // and every client draws it settling where it sank, without a blast.
            if (source != null && (source.CrashSkipsBurn || source.IsFlooded))
            {
                _burnClock.KillImmediately(vehicleId);
                return new VehicleDamageOutcome(0f, startedBurning: false, died: true);
            }

            int burnTicks = (int)(BurnSeconds(source) * ProtocolConstants.SIM_TICK_RATE);
            _burnClock.StartBurning(vehicleId, burnTicks, _currentTick());

            return new VehicleDamageOutcome(0f, startedBurning: true, died: false);
        }

        /// <summary>
        /// Who last damaged <paramref name="vehicleId"/> before it began to burn, if that was at
        /// most <paramref name="withinTicks"/> ago.
        /// </summary>
        /// <remarks>
        /// Bounded by age rather than cleared on despawn, which this sink never sees. A reused id
        /// cannot inherit a record: the wreck holds its id for 15 s after <c>Die</c>
        /// (<c>Vehicle.Die</c> invokes <c>Cleanup</c> then), so any record it left is already
        /// past <see cref="Ironfront.Net.Replication.Server.DeathAttribution.DestroyerCreditSeconds"/>
        /// before the next vehicle can take that id.
        /// </remarks>
        internal bool TryGetRecentAttacker(ushort vehicleId, uint withinTicks, out ushort attackerId)
        {
            if (_lastAttacks.TryGetValue(vehicleId, out LastAttack last)
                && _currentTick() - last.Tick <= withinTicks)
            {
                attackerId = last.ActorId;
                return true;
            }

            attackerId = 0;
            return false;
        }

        private readonly struct LastAttack
        {
            public LastAttack(ushort actorId, uint tick)
            {
                ActorId = actorId;
                Tick = tick;
            }

            public ushort ActorId { get; }
            public uint Tick { get; }
        }

        /// <inheritdoc />
        public float ApplyRepair(ushort vehicleId, float amount)
        {
            VehicleRegistry registry = _vehicles.Registry;

            if (!registry.TryGetState(vehicleId, out VehicleState state))
            {
                UnknownVehicles++;
                return 0f;
            }

            // A wreck is not repairable, matching Vehicle.Repair's own `if (dead) return false`.
            if (state.Dead) return 0f;

            float repaired = state.Health + amount;
            if (repaired > state.MaxHealth) repaired = state.MaxHealth;

            state.Health = repaired;
            registry.TrySetState(vehicleId, in state);

            // Both copies, in one call, exactly as ApplyDamage does. The scene's health has to
            // move too or the AI, the ram check and the particle ladder keep reading a wreck.
            if (_vehicles.TryFind(vehicleId, out IGameplayVehicleSource source))
                source.SetHealthAuthoritative(repaired);

            RepairsApplied++;
            return repaired;
        }

        /// <summary>
        /// Puts the burn out. Separate from <see cref="ApplyRepair"/> because the scene decides
        /// WHEN a burn stops — <c>Vehicle.Repair</c> requires three repairs
        /// (<c>stopBurningRepairs</c>) — and that rule is gameplay, not netcode.
        /// </summary>
        internal void ExtinguishBurn(ushort vehicleId) => _burnClock.CancelBurn(vehicleId);

        /// <summary>Repairs routed through the authoritative health record.</summary>
        public long RepairsApplied { get; private set; }

        /// <summary>
        /// The prefab's authored burn time, or a floor when it authored none.
        /// </summary>
        /// <remarks>
        /// A vehicle with <c>burnTime = 0</c> would otherwise die on the same tick it reached
        /// zero health, which is the no-burn behaviour V4-D11 exists to prevent — and it would
        /// do it silently, on whichever prefabs happened to leave the field at its default.
        /// One second is short enough to read as "it blew up" and long enough that the
        /// <c>Burning</c> flag reaches a client before the despawn does.
        /// </remarks>
        private static float BurnSeconds(IGameplayVehicleSource source)
        {
            if (source == null) return 1f;

            float authored = source.BurnTimeSeconds;
            return authored > 0f ? authored : 1f;
        }
    }
}
