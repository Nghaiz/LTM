using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The one call the original game's damage path makes into the netcode. phase-05 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C. <c>Actor.cs</c> is Dev A's file, so the guard that lives there is kept to
    /// a shape a reviewer can read in one sitting: a role check and a call to this class.
    /// Everything the netcode actually does on a death is here, on this side of the line, where
    /// changing it does not need another review round.
    /// </para>
    /// <para>
    /// <b>Static, and deliberately so.</b> <c>Actor</c> has no reference to the tick loop and
    /// acquiring one would mean a serialized field on every actor prefab in the game — a scene
    /// edit per actor, for a call that happens a few times a minute. <see cref="ServerTickLoop"/>
    /// already publishes itself for exactly this reason (<c>BotLodGate</c> was the first
    /// caller), including the domain-reload reset that keeps a stale loop from a previous Play
    /// session out of this one.
    /// </para>
    /// </remarks>
    public static class ServerCombatEvents
    {
        /// <summary>
        /// Reports that a non-hitscan damage source killed an actor: a bot's bullet, a
        /// grenade, a fall, a vehicle.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A no-op off the server, and a no-op for a GameObject that is not replicated. Both
        /// matter: this is called from a method that runs in single-player, and the single-player
        /// game must behave exactly as it does today.
        /// </para>
        /// <para>
        /// The victim is resolved through its <see cref="NetServerActor"/> because that is what
        /// knows the wire id. A <c>GetComponent</c> per death is affordable; a serialized
        /// back-reference on every actor prefab is not.
        /// </para>
        /// </remarks>
        /// <param name="victim">The actor that just died.</param>
        /// <param name="impactForce">The blow that killed it, for each client's own ragdoll.</param>
        /// <param name="cause">What killed it, for the killfeed.</param>
        public static void ReportDeath(
            Component victim, Vector3 impactForce, CauseOfDeath cause = CauseOfDeath.Bullet)
        {
            if (!NetContext.IsServer) return;
            if (victim == null) return;

            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return;

            var replicated = victim.GetComponent<NetServerActor>();
            if (replicated == null) return;

            loop.EmitDeath(
                replicated.ActorId,
                DeathMessage.EnvironmentKiller,
                MovementSimulation.ToCore(impactForce),
                (byte)HitboxType.Body,
                cause);
        }
    }
}
