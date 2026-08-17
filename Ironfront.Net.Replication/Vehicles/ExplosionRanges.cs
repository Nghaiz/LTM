namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The two radii an explosion reaches, and the falloff parameter each produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this isolates.</b> <c>ActorManager.Explode</c> gathers actors within
    /// <c>balanceRange</c> (9 m on the shipped configuration) and then normalizes the damage
    /// falloff against <c>damageRange</c> (6 m) with <c>Mathf.Clamp01</c>. Clamping
    /// <b>saturates</b> rather than excludes: an actor at 8 m gets <c>t = 8/6 = 1.33 → 1.0</c>
    /// and takes exactly what an actor at 6.001 m takes. The 6–9 m band is therefore a flat
    /// plateau at the curve's endpoint, not a falloff, and the real damage cut-off is the
    /// wider radius. The vehicle loop ten lines below gets this right by testing the distance
    /// first; the actor loop does not.
    /// </para>
    /// <para>
    /// Splitting the two questions — <i>does this reach at all</i> and <i>how hard</i> — makes
    /// the cut-off impossible to skip, which is the shape the vehicle loop already had.
    /// <c>AnimationCurve</c> stays on the Unity side; only the range policy moves.
    /// </para>
    /// </remarks>
    public struct ExplosionRanges
    {
        /// <summary>Beyond this, an explosion deals no damage at all.</summary>
        public float DamageRange;

        /// <summary>Balance disruption and knockback reach this far. Normally the wider of the two.</summary>
        public float BalanceRange;

        public ExplosionRanges(float damageRange, float balanceRange)
        {
            DamageRange = damageRange;
            BalanceRange = balanceRange;
        }

        /// <summary>
        /// The damage falloff parameter at <paramref name="distance"/>.
        /// </summary>
        /// <returns>
        /// <c>false</c> when the target is at or beyond <see cref="DamageRange"/> and takes no
        /// damage; <c>true</c> with <paramref name="t"/> in <c>[0, 1)</c> otherwise.
        /// </returns>
        public bool TryGetDamageT(float distance, out float t)
        {
            t = 0f;

            // Negated rather than `distance >= DamageRange` so a NaN distance reports "out of
            // range" instead of falling through to a NaN damage multiplier.
            if (DamageRange <= 0f || !(distance < DamageRange))
                return false;

            t = distance / DamageRange;
            if (t < 0f) t = 0f;
            return true;
        }

        /// <summary>
        /// The balance falloff parameter at <paramref name="distance"/>, clamped to
        /// <c>[0, 1]</c>. Saturating is correct here: the caller has already restricted the
        /// query to <see cref="BalanceRange"/>, so the endpoint is the boundary itself rather
        /// than a plateau reaching past one.
        /// </summary>
        public float GetBalanceT(float distance)
        {
            if (BalanceRange <= 0f || float.IsNaN(distance))
                return 1f;

            float t = distance / BalanceRange;
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return t;
        }
    }
}
