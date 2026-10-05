using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The grass sliders scale the preset's own distance and density (P33: they wrote the
    /// terrain's own values, which every preset overrides, and so did nothing).
    /// </summary>
    public sealed class PresetGrassScaleTests
    {
        private const int Ultra = 3;
        private const int Low = 0;

        [Fact]
        public void FullSlidersGiveThePresetsOwnGrass()
        {
            var scale = new PresetGrassScale();
            Assert.Equal((120f, 1f), scale.Scale(Ultra, 120f, 1f, 1f, 1f));
        }

        [Fact]
        public void TheSlidersAreFractionsOfThePreset()
        {
            var scale = new PresetGrassScale();
            Assert.Equal((60f, 0.25f), scale.Scale(Ultra, 120f, 1f, 0.5f, 0.25f));
        }

        [Fact]
        public void ApplyingAgainScalesThePresetNotTheLastScale()
        {
            var scale = new PresetGrassScale();
            (float distance, float density) = scale.Scale(Ultra, 120f, 1f, 0.5f, 0.5f);
            // What the level reads after the first apply is the scaled value; a second apply must
            // not halve it again.
            Assert.Equal((60f, 0.5f), scale.Scale(Ultra, distance, density, 0.5f, 0.5f));
            Assert.Equal((120f, 1f), scale.Scale(Ultra, distance, density, 1f, 1f));
        }

        [Fact]
        public void EachLevelKeepsItsOwnPreset()
        {
            var scale = new PresetGrassScale();
            scale.Scale(Ultra, 120f, 1f, 0.5f, 0.5f);
            Assert.Equal((20f, 0.175f), scale.Scale(Low, 40f, 0.35f, 0.5f, 0.5f));
            Assert.True(scale.TryGetPreset(Ultra, out float distance, out float density));
            Assert.Equal((120f, 1f), (distance, density));
            Assert.False(scale.TryGetPreset(1, out _, out _));
        }

        [Theory]
        [InlineData(-1f, 0f)]
        [InlineData(2f, 120f)]
        [InlineData(float.NaN, 0f)]
        public void FractionsOutsideZeroToOneAreClamped(float fraction, float expectedDistance)
        {
            var scale = new PresetGrassScale();
            Assert.Equal(expectedDistance, scale.Scale(Ultra, 120f, 1f, fraction, 1f).Distance);
        }
    }
}
