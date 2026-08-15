using Ironfront.Net.Replication.Combat;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The one place health is written on the server. phase-05 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>Every damage source funnels through here.</b> Hitscan from
    /// <see cref="ServerCombatAuthority"/> arrives directly; bot bullets, melee, explosions and
    /// vehicle collisions arrive via the <c>Actor.Damage</c> guard (task 6), which is the one
    /// choke point every damage source in the original game already passes through. A second
    /// writer would be exactly the silent divergence this phase exists to remove.
    /// </para>
    /// <para>
    /// <b><see cref="DamageOutcome.Died"/> is edge-triggered.</b> It is true only on the hit
    /// that crossed zero, so a shotgun blast whose second pellet lands on an already-dead
    /// target reports one death — which is what keeps <c>MatchController.ReportDeath</c> and
    /// the killfeed honest. Getting this wrong ends the round early and looks like a scoring
    /// bug rather than a damage bug.
    /// </para>
    /// </remarks>
    internal sealed class ServerActorDamageSink : IActorDamageSink
    {
        private readonly ServerActorRegistry _registry;

        public ServerActorDamageSink(ServerActorRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Damage applications that found no actor. Non-zero means a stale target id.</summary>
        public long UnknownVictims { get; private set; }

        /// <summary>Damage applied to actors that were already dead. Free; nothing happens.</summary>
        public long DamageToCorpses { get; private set; }

        public DamageOutcome ApplyDamage(ushort victimId, float amount, ushort attackerId)
        {
            if (!_registry.TryFind(victimId, out NetServerActor victim) || victim == null)
            {
                UnknownVictims++;
                return DamageOutcome.NoOp;
            }

            if (!victim.IsAlive)
            {
                DamageToCorpses++;
                return new DamageOutcome(0f, died: false);
            }

            float remaining = victim.Health - amount;
            if (remaining < 0f) remaining = 0f;

            victim.Health = remaining;

            if (remaining > 0f) return new DamageOutcome(remaining, died: false);

            // Flipped here rather than left to the caller, so the next call for the same actor
            // takes the IsAlive branch above and reports died:false. That is what makes the
            // edge single-fire without anyone having to remember to make it so.
            //
            // IsAlive writes through to Actor.dead, which is what stops the AI, stops the
            // corpse being a hitscan target, and makes Actor.Damage's own `if (dead) return`
            // guard refuse anything that arrives afterwards. Actor.Die() is deliberately NOT
            // called: it is private, and it reaches for IngameUi and ScoreUi, neither of which
            // exists on a headless server. The death choreography is per-client anyway —
            // corpses are never replicated (AD-4), so each client runs its own ragdoll off
            // S_DEATH.
            victim.IsAlive = false;
            return new DamageOutcome(0f, died: true);
        }
    }
}
