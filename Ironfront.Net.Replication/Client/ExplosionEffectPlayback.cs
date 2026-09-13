namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// The engine-facing surface needed to turn an authored particle object into one visible
    /// explosion. Kept engine-free so the failure can be regression-tested without Unity.
    /// </summary>
    public interface IExplosionEffectSurface
    {
        bool Looping { get; set; }
        bool EmissionEnabled { get; set; }
        float EmissionRatePerSecond { get; set; }
        int BurstCount { get; }
        bool HasDrawableMaterial { get; }

        void SetBurstCount(short count);
        bool TryAssignFallbackMaterial();
        void Restart();
    }

    /// <summary>
    /// Repairs incomplete scene-authored explosion placeholders before playing them.
    /// </summary>
    public static class ExplosionEffectPlayback
    {
        public const short DefaultBurstParticles = 24;

        /// <summary>
        /// Makes <paramref name="effect"/> a finite visible burst and restarts it. Returns false
        /// when no drawable material can be supplied.
        /// </summary>
        public static bool TryPlay(IExplosionEffectSurface effect)
        {
            if (effect == null) return false;

            effect.Looping = false;
            effect.EmissionEnabled = true;
            effect.EmissionRatePerSecond = 0f;
            if (effect.BurstCount <= 0)
                effect.SetBurstCount(DefaultBurstParticles);

            if (!effect.HasDrawableMaterial && !effect.TryAssignFallbackMaterial())
                return false;

            effect.Restart();
            return true;
        }
    }
}
