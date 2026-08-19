namespace Ironfront.Net.Replication.Combat
{
    /// <summary>What applying damage to an actor did to it.</summary>
    public readonly struct DamageOutcome
    {
        /// <summary>Health after the hit, floored at zero.</summary>
        public readonly float RemainingHealth;

        /// <summary>True only on the hit that took the actor from alive to dead.</summary>
        /// <remarks>
        /// Edge-triggered, not level-triggered. Two projectiles from one shotgun blast landing
        /// on a target with 10 health must report <c>Died</c> once, or the killfeed shows two
        /// kills, <c>MatchController.ReportDeath</c> is called twice and the round ends early —
        /// the phase-05 criterion-4 "exactly once" clause.
        /// </remarks>
        public readonly bool Died;

        public DamageOutcome(float remainingHealth, bool died)
        {
            RemainingHealth = remainingHealth;
            Died = died;
        }

        /// <summary>The answer for a victim that could not be found or was already dead.</summary>
        public static DamageOutcome NoOp => new DamageOutcome(0f, false);
    }

    /// <summary>
    /// Where the authoritative health of an actor lives. phase-05 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one seam between the engine-free combat core and the engine. Everything about a
    /// shot — the cooldown, the ammo, the rewind, the hitbox, the multiplier — is decided in
    /// this library and testable in CI; the single question this library cannot answer is
    /// "where is that actor's health stored", because in a Unity build the answer is a
    /// <c>MonoBehaviour</c> field and in a test it is a dictionary.
    /// </para>
    /// <para>
    /// <b>Implementations must be the only writer of health on their side.</b> Two writers is
    /// exactly the divergence phase-05 exists to remove (D9): the snapshot reads one number,
    /// <c>Die()</c> reads another, and nothing reports the disagreement.
    /// </para>
    /// </remarks>
    public interface IActorDamageSink
    {
        /// <summary>
        /// Applies <paramref name="healthDamage"/> and <paramref name="balanceDamage"/> to
        /// <paramref name="victimId"/>.
        /// </summary>
        /// <param name="balanceDamage">
        /// Stagger, subtracted from the victim's balance. phase-V2 D6.
        /// <c>Actor.Damage(healthDamage, balanceDamage, ...)</c> has taken this number since
        /// before the netcode existed and the server has always passed zero, so no weapon has
        /// ever staggered anyone on a dedicated server. Widening the interface rather than
        /// adding an overload is deliberate: two signatures for one concept is the SSOT
        /// violation <c>development-principles.md</c> forbids, and the compiler enumerates every
        /// call site for free.
        /// <para>
        /// <b>Applied server-side and not replicated</b> (D7). The authoritative view and the
        /// bots stagger correctly; a remote client sees none of it, because there is no wire
        /// field for stagger and <c>ActorStateFlags</c> is 8/8 full. That is a V3 decision, not
        /// something this seam can smuggle in.
        /// </para>
        /// </param>
        /// <param name="attackerId">
        /// For the killfeed and for friendly-fire rules. Never used to decide whether the
        /// damage lands — that was settled before this call.
        /// </param>
        DamageOutcome ApplyDamage(
            ushort victimId, float healthDamage, float balanceDamage, ushort attackerId);

        /// <summary>
        /// Adds health back, clamped at the actor's ceiling, and reports how much actually
        /// landed. Phase-V7 task 7.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A heal is damage inverted, and it goes through the same door.</b> phase-05 D9
        /// established that there is exactly one place health is written on the server; a
        /// medipack that wrote <c>Actor.health</c> directly (<c>Actor.cs:1173-1187</c> does
        /// exactly that today) would recreate the two-numbers-out-of-sync problem D9 removed —
        /// the snapshot reading one health and the death check reading another, with nothing
        /// reporting the disagreement.
        /// </para>
        /// <para>
        /// <b>The return value is the amount applied, not the resulting health</b>, because the
        /// caller's question is "did this heal do anything". A <c>Medipack</c> subtracts five
        /// seconds from its own life per <i>successful</i> heal (<c>Medipack.cs:26-29</c>), and
        /// a full-health actor standing on it must not shorten it. Returning the new health
        /// would make every caller compare against a ceiling it does not know.
        /// </para>
        /// <para>
        /// <b>A dead actor is never healed.</b> Reviving by walking a corpse over a medipack is
        /// not a mechanic this game has, and the resupply sweep does not filter for it — this
        /// does.
        /// </para>
        /// </remarks>
        /// <returns>Health actually restored, in <c>[0, amount]</c>. Zero means nothing happened.</returns>
        float ApplyHeal(ushort actorId, float amount);
    }
}
