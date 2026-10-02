using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Grass at every preset by default (owner report 2026-10-03: no trees or grass on an old PC
    /// with integrated graphics, from Low to Ultra, on v3.2.0).
    /// </summary>
    public sealed class VegetationRulesTests
    {
        [Theory]
        [InlineData(GraphicsPresetRules.Low)]
        [InlineData(GraphicsPresetRules.Medium)]
        [InlineData(GraphicsPresetRules.High)]
        [InlineData(GraphicsPresetRules.Ultra)]
        public void EveryPresetStartsWithSomeGrass(int level)
        {
            // 0.01 is where the legacy options slider and DetailObjectQuality read "no vegetation".
            Assert.True(VegetationRules.DefaultDensityFor(level) >= 0.01f,
                $"preset {level} defaults to no grass; v3.2.0 shipped exactly that on Low");
        }

        [Fact]
        public void LowStartsThinnerThanTheRest()
        {
            Assert.True(VegetationRules.DefaultDensityFor(GraphicsPresetRules.Low)
                        < VegetationRules.DefaultDensityFor(GraphicsPresetRules.Medium));
            Assert.Equal(VegetationRules.DefaultDensity, VegetationRules.DefaultDensityFor(GraphicsPresetRules.Ultra));
        }
    }
}
