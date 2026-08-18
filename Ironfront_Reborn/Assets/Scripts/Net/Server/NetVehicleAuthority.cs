using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The face <c>Assembly-CSharp</c>'s <c>Vehicle</c> calls into for its role guards. V4
    /// tasks 5 and 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Static, for <see cref="NetVehicleLifecycle"/>'s reason.</b> A <c>Vehicle</c> is an
    /// authored prefab instantiated by a spawner; a serialized reference per vehicle would be a
    /// per-prefab manual step that gets forgotten on the one prefab nobody re-opened, and the
    /// symptom would be a vehicle whose damage is applied twice with nothing in the log.
    /// </para>
    /// <para>
    /// <b>Uninstalled is the normal state.</b> A client and an offline build never install a
    /// sink, so every method here reports "not handled" and <c>Vehicle</c> runs exactly the code
    /// it shipped with. That is what acceptance criterion 12 asks for, and it is why the guards
    /// are written as <c>if (handled) return;</c> rather than as a role switch: there is one
    /// branch and it is false unless a server put something behind it.
    /// </para>
    /// <para>
    /// <b>Cleared at subsystem registration</b>, because with domain reload disabled a static
    /// field survives leaving play mode and the second run would route damage into the first
    /// run's registry.
    /// </para>
    /// </remarks>
    public static class NetVehicleAuthority
    {
        private static IVehicleDamageSink _damageSink;
        private static ServerVehicleRegistry _vehicles;
        private static ServerActorRegistry _actors;

        /// <summary>True when a server is authoritative over vehicle health and seat claims.</summary>
        public static bool IsInstalled => _damageSink != null && _vehicles != null;

        /// <summary>Installs the server's sinks. Called from <c>ServerTickLoop.Bind</c>.</summary>
        public static void Install(
            IVehicleDamageSink damageSink,
            ServerVehicleRegistry vehicles,
            ServerActorRegistry actors)
        {
            _damageSink = damageSink;
            _vehicles   = vehicles;
            _actors     = actors;
        }

        /// <summary>Restores the uninstalled state. Called from <c>ServerTickLoop.Unbind</c>.</summary>
        public static void Uninstall()
        {
            _damageSink = null;
            _vehicles   = null;
            _actors     = null;
        }

        /// <summary>
        /// Routes a vehicle's damage through the authoritative sink.
        /// </summary>
        /// <returns>
        /// True when the server handled it, in which case the caller must NOT subtract health
        /// itself. False offline, on a client, and for a vehicle that was never replicated —
        /// every one of which is a case where the shipped path is the correct one.
        /// </returns>
        public static bool TryApplyDamage(GameObject vehicle, float amount, int attackerActorId)
        {
            if (!IsInstalled) return false;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return false;

            ushort attacker = attackerActorId > 0 && attackerActorId <= ushort.MaxValue
                ? (ushort)attackerActorId
                : (ushort)0;

            _damageSink.ApplyDamage(vehicleId, amount, attacker);
            return true;
        }

        /// <summary>
        /// True when this build must suppress a vehicle's local health and death logic because
        /// the server owns them.
        /// </summary>
        /// <remarks>
        /// A client, and only a client. It is asked separately from
        /// <see cref="TryApplyDamage"/> because the two answers differ on the one build that
        /// matters: a client has no sink to route to and must still not run the health ladder,
        /// where an offline build has no sink and must.
        /// </remarks>
        public static bool IsClientSuppressed => NetContext.IsClient;

        // ------------------------------------------------------------------- seat claims

        /// <summary>
        /// Reserves a seat for a bot by identity. V4-D10.
        /// </summary>
        /// <returns>False when not replicating, so the caller falls back to its counter.</returns>
        public static bool TryClaimSeat(GameObject vehicle, GameObject botActor)
        {
            if (!TryResolveClaim(vehicle, botActor, out ushort vehicleId, out ushort botId))
                return false;

            // Seat index 0 is a placeholder: the shipped ClaimSeat() reserves "a seat", not a
            // particular one, and the AI picks which on arrival. What matters for the count is
            // that the claim names the bot — the first free index is claimed so two bots take
            // two slots rather than overwriting one.
            VehicleRegistry registry = _vehicles.Registry;
            if (!registry.TryGetState(vehicleId, out VehicleState state)) return false;

            for (byte seat = 0; seat < state.SeatCount; seat++)
            {
                ushort held = _vehicles.Claims.ClaimantOf(vehicleId, seat);
                if (held != 0 && held != botId) continue;

                return _vehicles.Claims.TryClaim(vehicleId, seat, botId, Time.time);
            }

            return false;
        }

        /// <summary>Releases every claim this bot holds on this vehicle.</summary>
        /// <returns>False when not replicating.</returns>
        public static bool TryDropSeatClaim(GameObject vehicle, GameObject botActor)
        {
            if (!TryResolveClaim(vehicle, botActor, out ushort vehicleId, out ushort botId))
                return false;

            VehicleRegistry registry = _vehicles.Registry;
            if (!registry.TryGetState(vehicleId, out VehicleState state)) return false;

            for (byte seat = 0; seat < state.SeatCount; seat++)
                if (_vehicles.Claims.Release(vehicleId, seat, botId)) return true;

            return true;
        }

        /// <summary>
        /// Live claims on a vehicle, or -1 when this build is not replicating.
        /// </summary>
        /// <remarks>
        /// <b>-1 rather than 0 for "no answer".</b> Zero is a legitimate count and the caller
        /// falls back to its own field on -1; conflating the two would report every vehicle on a
        /// client as having no claims, which is the AI reading a number it was never given.
        /// </remarks>
        public static int ClaimCount(GameObject vehicle)
        {
            if (!IsInstalled) return -1;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            return vehicleId == 0 ? -1 : _vehicles.Claims.ClaimCount(vehicleId);
        }

        /// <summary>Drops claims whose per-claim deadline has passed.</summary>
        /// <remarks>
        /// Per-claim, not per-vehicle. The shipped <c>drainClaimAction</c> takes one claim off an
        /// anonymous pile every ten seconds, which is why two bots claiming and one dying leaves
        /// the count permanently wrong (V4-D10).
        /// </remarks>
        public static void ReleaseExpiredClaims()
        {
            if (!IsInstalled) return;

            _vehicles.Claims.ReleaseExpired(Time.time);
        }

        private static bool TryResolveClaim(
            GameObject vehicle, GameObject botActor, out ushort vehicleId, out ushort botId)
        {
            vehicleId = 0;
            botId     = 0;

            if (!IsInstalled || _actors == null || botActor == null) return false;

            vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return false;

            NetServerActor actor = botActor.GetComponent<NetServerActor>();
            if (actor == null || actor.ActorId == 0) return false;

            botId = actor.ActorId;
            return true;
        }
    }
}
