using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    public sealed class ExplosionEffectPlaybackTests
    {
        [Fact]
        public void AConfiguredButInvisibleEffectIsMadeIntoADrawableOneShotBeforePlaying()
        {
            var effect = new RecordingEffect
            {
                Looping = true,
                EmissionEnabled = false,
                EmissionRatePerSecond = 10f,
                BurstCount = 0,
                HasDrawableMaterial = false,
                CanAssignMaterial = true,
            };

            Assert.True(ExplosionEffectPlayback.TryPlay(effect));
            Assert.False(effect.Looping);
            Assert.True(effect.EmissionEnabled);
            Assert.Equal(0f, effect.EmissionRatePerSecond);
            Assert.Equal(ExplosionEffectPlayback.DefaultBurstParticles, effect.BurstCount);
            Assert.True(effect.HasDrawableMaterial);
            Assert.Equal(1, effect.Restarts);
        }

        [Fact]
        public void AnEffectThatCannotObtainAMaterialIsNotReportedAsPlayed()
        {
            var effect = new RecordingEffect
            {
                HasDrawableMaterial = false,
                CanAssignMaterial = false,
            };

            Assert.False(ExplosionEffectPlayback.TryPlay(effect));
            Assert.Equal(0, effect.Restarts);
        }

        private sealed class RecordingEffect : IExplosionEffectSurface
        {
            public bool Looping { get; set; }
            public bool EmissionEnabled { get; set; }
            public float EmissionRatePerSecond { get; set; }
            public int BurstCount { get; set; }
            public bool HasDrawableMaterial { get; set; }
            public bool CanAssignMaterial { get; set; }
            public int Restarts { get; private set; }

            public void SetBurstCount(short count) => BurstCount = count;

            public bool TryAssignFallbackMaterial()
            {
                if (CanAssignMaterial) HasDrawableMaterial = true;
                return CanAssignMaterial;
            }

            public void Restart() => Restarts++;
        }
    }
}
