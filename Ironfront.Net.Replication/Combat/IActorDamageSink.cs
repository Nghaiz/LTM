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
        /// Applies <paramref name="amount"/> damage to <paramref name="victimId"/>.
        /// </summary>
        /// <param name="attackerId">
        /// For the killfeed and for friendly-fire rules. Never used to decide whether the
        /// damage lands — that was settled before this call.
        /// </param>
        DamageOutcome ApplyDamage(ushort victimId, float amount, ushort attackerId);
    }
}
