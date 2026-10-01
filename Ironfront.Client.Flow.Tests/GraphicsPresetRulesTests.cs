using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Which preset a machine starts on, and how a preset saved under the six Unity levels carries
    /// over to the game's four (owner report 2026-10-01: "the client is badly optimised").
    /// </summary>
    public sealed class GraphicsPresetRulesTests
    {
        [Theory]
        [InlineData(1024, false, 8, GraphicsPresetRules.Low)]
        [InlineData(2048, false, 8, GraphicsPresetRules.Low)]
        [InlineData(4096, false, 8, GraphicsPresetRules.Medium)]   // a 4 GB GTX 1650
        [InlineData(6144, false, 12, GraphicsPresetRules.Medium)]  // a 6 GB GTX 1660 / RTX 2060
        [InlineData(8192, false, 32, GraphicsPresetRules.High)]    // a laptop RTX 4060
        [InlineData(12288, false, 16, GraphicsPresetRules.High)]   // never Ultra: that is the player's to pick
        [InlineData(8192, false, 4, GraphicsPresetRules.Medium)]   // four threads hold a big card back
        [InlineData(8192, true, 16, GraphicsPresetRules.Low)]      // shared memory reports large and is slow
        public void AMachineStartsOnThePresetItsCardSuits(int videoMemoryMb, bool integrated, int cores, int expected)
        {
            Assert.Equal(expected, GraphicsPresetRules.Recommended(videoMemoryMb, integrated, cores));
        }

        [Theory]
        [InlineData("Intel(R) UHD Graphics 620", true)]
        [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
        [InlineData("AMD Radeon(TM) Graphics", true)]
        [InlineData("AMD Radeon Vega 8 Graphics", true)]
        [InlineData("AMD Radeon 780M", true)]
        [InlineData("AMD Radeon 760M Graphics", true)]
        [InlineData("AMD Radeon RX 7600M", false)]
        [InlineData("AMD Radeon RX 580 Series", false)]
        [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", false)]
        [InlineData("NVIDIA GeForce GTX 1650", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IntegratedGraphicsAreToldApartByName(string? device, bool integrated)
        {
            Assert.Equal(integrated, GraphicsPresetRules.IsIntegrated(device));
        }

        [Theory]
        [InlineData(0, GraphicsPresetRules.Low)]      // Fastest
        [InlineData(1, GraphicsPresetRules.Low)]      // Fast
        [InlineData(2, GraphicsPresetRules.Medium)]   // Simple
        [InlineData(3, GraphicsPresetRules.Medium)]   // Good
        [InlineData(4, GraphicsPresetRules.High)]     // Beautiful
        [InlineData(5, GraphicsPresetRules.High)]     // Fantastic: the old default comes back as High
        [InlineData(-1, GraphicsPresetRules.Low)]
        public void ASixLevelPresetCarriesOverToTheFour(int legacy, int expected)
        {
            Assert.Equal(expected, GraphicsPresetRules.FromLegacyLevel(legacy));
        }

        [Fact]
        public void TheOriginalsHighQualityObjectsStillShowOnTheNewDefault()
        {
            // QualitySwitcher.hqLevel and CapturePoint's flag are authored at 5 (Fantastic) in every
            // scene and prefab; read through the mapping, High -- the default -- still draws them.
            Assert.True(GraphicsPresetRules.High >= GraphicsPresetRules.FromLegacyLevel(5));
            Assert.True(GraphicsPresetRules.Medium < GraphicsPresetRules.FromLegacyLevel(5));
        }

        [Theory]
        [InlineData(-3, GraphicsPresetRules.Low)]
        [InlineData(2, GraphicsPresetRules.High)]
        [InlineData(9, GraphicsPresetRules.Ultra)]
        public void ASavedLevelIsKeptInsideTheShippedRange(int saved, int expected)
        {
            Assert.Equal(expected, GraphicsPresetRules.Clamp(saved));
        }

        [Fact]
        public void TheShippedPresetsAreFourAndTheLayoutIsVersioned()
        {
            Assert.Equal(4, GraphicsPresetRules.LevelCount);
            Assert.Equal(GraphicsPresetRules.LevelCount - 1, GraphicsPresetRules.Ultra);
            Assert.True(GraphicsPresetRules.SettingsVersion >= 2);
        }
    }
}
