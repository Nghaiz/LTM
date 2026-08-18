namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The blast radius, on the wire and off it. phase-V1 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ExplosionMessage.RadiusMetres</c> is a single byte of whole metres, so somebody has to
    /// decide what happens to the fraction and what happens past 255 m. Left to each caller,
    /// that decision gets made three times and agreed on twice: the emitter rounds, the
    /// presenter truncates, and a 300 m radius wraps to 44 on a cast. Both halves live here so
    /// the pair cannot disagree.
    /// </para>
    /// <para>
    /// <b>Ceil, not round.</b> A client's effect must never render smaller than the blast that
    /// hurt the player — a 6.4 m blast drawn at 6 m puts a victim visibly outside a radius that
    /// killed them, and "the explosion did not reach me" is a bug report. Rounding up costs at
    /// most a metre of harmless extra flash.
    /// </para>
    /// <para>
    /// <b>Saturate, never wrap.</b> 255 m is roughly 28x the widest explosive in scope
    /// (<c>balanceRange</c> 9 m), so the clamp is unreachable in practice — but a future weapon
    /// that reached it would, on a bare cast, emit a <i>smaller</i> radius than a grenade, and
    /// nothing downstream could tell that had happened.
    /// </para>
    /// <para>
    /// Pure functions, no state, no allocation. <c>MathF.Ceiling</c> is avoided so the NaN and
    /// out-of-range answers are visible in the source rather than inherited.
    /// </para>
    /// </remarks>
    public static class ExplosionEncoding
    {
        /// <summary>The widest radius the wire can carry.</summary>
        public const byte MaxRadiusMetres = byte.MaxValue;

        /// <summary>
        /// Quantizes a blast radius to the message's whole-metre byte, rounding up and
        /// saturating at <see cref="MaxRadiusMetres"/>.
        /// </summary>
        /// <remarks>
        /// The negation on the first test is deliberate: written as
        /// <c>radiusMetres &lt;= 0f</c>, a NaN radius would fall through to the cast and produce
        /// an arbitrary byte. Phrased this way NaN reports zero, which is the same answer an
        /// absent explosion gives.
        /// </remarks>
        public static byte PackRadiusMetres(float radiusMetres)
        {
            if (!(radiusMetres > 0f)) return 0;
            if (radiusMetres >= MaxRadiusMetres) return MaxRadiusMetres;

            int whole = (int)radiusMetres;
            if (radiusMetres > whole) whole++;
            return (byte)whole;
        }

        /// <summary>
        /// The metres a packed radius stands for, so a presenter does not open-code the inverse.
        /// </summary>
        /// <remarks>
        /// Trivial today, and that is the point: the pair is named, so a later change to the
        /// packing has one obvious place to change with it. Every explosion is at least as wide
        /// as this reports, never wider, because <see cref="PackRadiusMetres"/> rounds up.
        /// </remarks>
        public static float UnpackRadiusMetres(byte packedRadiusMetres) => packedRadiusMetres;
    }
}
