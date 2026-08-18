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
    /// <c>Actor.cs</c> is the client track's file, so the guard that lives there is kept to
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

        /// <summary>
        /// Reports that a blast went off, so every client in earshot can draw it. phase-V1
        /// task 2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called once per blast, from the end of <c>ActorManager.Explode</c> — not once per
        /// victim. One grenade landing among four people is one explosion and four separate
        /// deaths, and conflating the two is the same edge-triggered mistake phase-05's
        /// <c>DamageOutcome.Died</c> exists to avoid.
        /// </para>
        /// <para>
        /// <b>This reports the cosmetic; it does not apply the damage.</b> Blast damage reaches
        /// the authoritative health through <c>Actor.Damage</c> and the sink phase-05 task 6
        /// installed, exactly as a bullet does. A second explosion-specific damage path would be
        /// the two-writers divergence phase-05 D9 forbids (V1 D2).
        /// </para>
        /// <para>
        /// A no-op off the server and a no-op with no tick loop, for the same reason
        /// <see cref="ReportDeath"/> is: the method it is called from also runs in single-player,
        /// and single-player must behave exactly as it does today.
        /// </para>
        /// </remarks>
        /// <param name="source">
        /// Whoever set it off. A source with no <c>NetServerActor</c> — a world explosive, or an
        /// unreplicated prop — is reported as
        /// <see cref="DeathMessage.EnvironmentKiller"/> rather than dropped: an unattributed
        /// explosion is still an explosion every client needs to see. This differs from
        /// <see cref="ReportDeath"/>, which returns early instead, because there the unresolved
        /// component IS the subject of the message.
        /// </param>
        /// <param name="radiusMetres">
        /// The radius the damage selection used, so the wire radius and the damaging radius
        /// cannot be read independently and drift (V1 D4).
        /// </param>
        public static void ReportExplosion(
            Component source, Vector3 centre, float radiusMetres, ExplosionKind kind)
        {
            if (!NetContext.IsServer) return;

            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return;
            if (loop.Transport == null) return;

            ushort sourceActorId = DeathMessage.EnvironmentKiller;
            if (source != null)
            {
                var replicated = source.GetComponent<NetServerActor>();
                if (replicated != null) sourceActorId = replicated.ActorId;
            }

            loop.EmitExplosion(
                sourceActorId, MovementSimulation.ToCore(centre), radiusMetres, kind);
        }
    }
}
