using System;
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

            // A crash on a crashSkipsBurn vehicle has no burn stage at all. Routed through the
            // burn clock either way so the despawn is announced from one place — two death paths
            // is how a wreck ends up either announced twice or not at all.
            bool skipsBurn = source != null && source.CrashSkipsBurn;
            int burnTicks = skipsBurn
                ? 0
                : (int)(BurnSeconds(source) * ProtocolConstants.SIM_TICK_RATE);

            _burnClock.StartBurning(vehicleId, burnTicks, _currentTick());

            return new VehicleDamageOutcome(0f, startedBurning: true, died: false);
        }

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
