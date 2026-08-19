using Ironfront.Net.Replication.Combat;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The one place health is written on the server. phase-05 task 3.
    /// </summary>
    /// <remarks>
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

        /// <summary>
        /// Hits that carried stagger. Zero across a whole match means either every weapon in play
        /// is inert or the phase-V2 balance number is not reaching the actor -- which is the exact
        /// failure this counter exists to make visible rather than inferable.
        /// </summary>
        public long BalanceDamageApplied { get; private set; }

        /// <summary>Health handed back by medipacks. Phase-V7 task 7.</summary>
        public float HealthRestored { get; private set; }

        public DamageOutcome ApplyDamage(
            ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)
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

            // Stagger before health, so a hit that kills has already knocked the victim over by
            // the time IsAlive flips -- the original's Actor.Damage subtracts balance before it
            // decides whether to Die() for the same reason. Non-zero here for the first time in
            // this build: the server has always passed zero (phase-V2 D6).
            if (balanceDamage > 0f)
            {
                victim.ApplyBalanceDamage(balanceDamage);
                BalanceDamageApplied++;
            }

            float remaining = victim.Health - healthDamage;
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

        /// <summary>
        /// The medipack path. Phase-V7 task 7.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Mirrors <c>Actor.ResupplyHealth()</c> (<c>Actor.cs:1232-1247</c>): 30 health, clamped
        /// at 100, refused outright on a corpse. It lives here rather than on the medipack for
        /// phase-05 D9's reason -- there is exactly one place health is written on the server,
        /// and a heal is a negative hit, not a different kind of event.
        /// </para>
        /// <para>
        /// <b>Returns the amount applied, so a full-health actor cannot shorten a medipack.</b>
        /// The pack subtracts five seconds per SUCCESSFUL heal (Medipack.cs:26-29); a squad
        /// standing on it at full health must cost it nothing.
        /// </para>
        /// </remarks>
        public float ApplyHeal(ushort actorId, float amount)
        {
            if (amount <= 0f) return 0f;

            if (!_registry.TryFind(actorId, out NetServerActor actor) || actor == null)
            {
                UnknownVictims++;
                return 0f;
            }

            if (!actor.IsAlive) return 0f;

            float before = actor.Health;
            float after  = before + amount;
            if (after > MaxHealth) after = MaxHealth;
            if (after <= before) return 0f;

            actor.Health = after;
            HealthRestored += after - before;
            return after - before;
        }

        /// <summary>
        /// The ceiling <c>Actor.ResupplyHealth</c> clamps to (<c>Actor.cs:1239</c>). Named here
        /// rather than inlined because it is the same 100 the snapshot's health byte is scaled
        /// against, and a second literal is how the two drift.
        /// </summary>
        private const float MaxHealth = 100f;
    }
}
