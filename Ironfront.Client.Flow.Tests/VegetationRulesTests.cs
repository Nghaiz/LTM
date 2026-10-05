using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Grass at every preset by default (owner report 2026-10-03: no trees or grass on an old PC
    /// with integrated graphics, from Low to Ultra, on v3.2.0), and the preset's full grass for a
    /// player who never moved the sliders, which is all any player saw while they did nothing.
    /// </summary>
    public sealed class VegetationRulesTests
    {
        [Fact]
        public void APlayerWhoNeverSetTheSlidersKeepsThePresetsFullGrass()
        {
            Assert.Equal(1f, VegetationRules.DefaultDensity);
            Assert.Equal(1f, VegetationRules.DefaultDistance);
        }

        [Fact]
        public void TheSlidersAreNotReadFromTheKeysThatNeverApplied()
        {
            // 0.5 and 0.7 sit under these for any player who ever saved the options; read as the
            // fractions they now are, they would halve the grass nobody chose to halve.
            foreach (string old in new[] { "vegetation density", "fast vegetation density", "vegetation distance" })
            {
                Assert.NotEqual(VegetationRules.DensityKey, old);
                Assert.NotEqual(VegetationRules.DistanceKey, old);
            }
            Assert.NotEqual(VegetationRules.DensityKey, VegetationRules.DistanceKey);
        }
    }
}
