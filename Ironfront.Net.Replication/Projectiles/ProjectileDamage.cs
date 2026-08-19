namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The server's damage number for a projectile hit. Phase-V7 task 1, governed by V7-D3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Clients never call this.</b> <c>Projectile.Damage()</c> scales by a curve over
    /// locally-accumulated distance, and two peers with different frame times accumulate
    /// different distances — so a client-computed number is a different number, every time.
    /// V7-D3 puts damage entirely on the server and this is where it is computed.
    /// </para>
    /// <para>
    /// The sampled-table equivalent of <c>Projectile.DamageDropOff()</c>
    /// (<c>Projectile.cs:175-178</c>), with linear interpolation between points.
    /// </para>
    /// </remarks>
    public static class ProjectileDamage
    {
        /// <summary>
        /// The drop-off multiplier at <paramref name="travelDistance"/>, in <c>[0, 1]</c> of
        /// whatever the authored curve holds.
        /// </summary>
        /// <remarks>
        /// An empty table means "no drop-off authored" and returns 1.0 — the neutral element,
        /// not a silent zero. A zero would make every un-tabulated weapon do no damage, which
        /// is the kind of fallback that looks like a balance decision.
        /// </remarks>
        public static float DropoffAt(in ProjectileConfig config, float travelDistance)
        {
            System.ReadOnlySpan<float> table = config.DropoffTable.Span;
            if (table.Length == 0) return 1f;
            if (table.Length == 1) return table[0];
            if (config.DropoffEnd <= 0f) return table[0];

            float t = travelDistance / config.DropoffEnd;
            if (t <= 0f) return table[0];
            if (t >= 1f) return table[table.Length - 1];

            float scaled = t * (table.Length - 1);
            var lower = (int)scaled;
            float frac = scaled - lower;

            // lower + 1 is in range because scaled < table.Length - 1 whenever t < 1.
            return table[lower] + (table[lower + 1] - table[lower]) * frac;
        }

        /// <summary>Health damage for a hit at <paramref name="travelDistance"/>.</summary>
        public static float DamageFor(in ProjectileConfig config, float travelDistance)
            => DropoffAt(config, travelDistance) * config.Damage;

        /// <summary>Stagger damage for a hit at <paramref name="travelDistance"/>.</summary>
        public static float BalanceDamageFor(in ProjectileConfig config, float travelDistance)
            => DropoffAt(config, travelDistance) * config.BalanceDamage;
    }
}
