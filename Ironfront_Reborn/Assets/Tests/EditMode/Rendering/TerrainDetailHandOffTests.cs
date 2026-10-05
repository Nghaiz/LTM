using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="TerrainDetailHandOff"/> stops the terrain drawing details whatever the preset says,
    /// keeps everything else the preset gives it, and puts the terrain's own settings back.
    /// </summary>
    public sealed class TerrainDetailHandOffTests
    {
        private TerrainData _data;
        private Terrain _terrain;

        [SetUp]
        public void SetUp()
        {
            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(100f, 50f, 100f) };
            _terrain = Terrain.CreateTerrainGameObject(_data).GetComponent<Terrain>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_terrain.gameObject);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void HeldTheTerrainDrawsNoDetailsAndKeepsThePresetsOtherSettings()
        {
            float presetDistance = _terrain.detailObjectDistance;
            float presetPixelError = _terrain.heightmapPixelError;
            float presetTrees = _terrain.treeDistance;
            var handOff = new TerrainDetailHandOff(_terrain);

            handOff.Hold();

            Assert.IsTrue(_terrain.ignoreQualitySettings, "the preset still overrides the detail distance");
            Assert.AreEqual(0f, _terrain.detailObjectDistance, "the terrain still draws its details");
            Assert.AreEqual(presetPixelError, _terrain.heightmapPixelError, "the heightmap is drawn at another pixel error");
            Assert.AreEqual(presetTrees, _terrain.treeDistance, "the trees are drawn to another distance");
            Assert.AreEqual(presetDistance, handOff.Distance, "the details' distance is no longer the one the terrain drew to");
        }

        [Test]
        public void ReleasedTheTerrainGetsItsOwnSettingsBack()
        {
            _terrain.ignoreQualitySettings = true;
            _terrain.detailObjectDistance = 37f;
            _terrain.detailObjectDensity = 0.4f;
            _terrain.heightmapPixelError = 9f;
            _terrain.ignoreQualitySettings = false;
            var handOff = new TerrainDetailHandOff(_terrain);

            handOff.Hold();
            handOff.Release();

            Assert.IsFalse(_terrain.ignoreQualitySettings);
            _terrain.ignoreQualitySettings = true;
            Assert.AreEqual(37f, _terrain.detailObjectDistance, "the terrain's own detail distance was lost");
            Assert.AreEqual(0.4f, _terrain.detailObjectDensity, 1e-6f, "the terrain's own density was lost");
            Assert.AreEqual(9f, _terrain.heightmapPixelError, "the terrain's own pixel error was lost");
        }

        [Test]
        public void ATerrainThatIgnoresThePresetIsDrawnByItsOwnSettings()
        {
            _terrain.ignoreQualitySettings = true;
            _terrain.detailObjectDistance = 42f;
            _terrain.detailObjectDensity = 0.5f;
            var handOff = new TerrainDetailHandOff(_terrain);

            handOff.Hold();

            Assert.AreEqual(42f, handOff.Distance);
            Assert.AreEqual(0.5f, handOff.Density, 1e-6f);
            Assert.AreEqual(0f, _terrain.detailObjectDistance);
            handOff.Release();
            Assert.IsTrue(_terrain.ignoreQualitySettings);
            Assert.AreEqual(42f, _terrain.detailObjectDistance);
        }

        [Test]
        public void ADistanceWrittenWhileHeldIsTheTerrainsOwnOnRelease()
        {
            _terrain.ignoreQualitySettings = true;
            var handOff = new TerrainDetailHandOff(_terrain);
            handOff.Hold();

            // DetailObjectQuality on an options save.
            _terrain.detailObjectDistance = 55f;
            handOff.Hold();

            Assert.AreEqual(0f, _terrain.detailObjectDistance, "a write from elsewhere put the terrain's details back on");
            Assert.AreEqual(55f, handOff.Distance, "the distance written meanwhile was ignored");
            handOff.Release();
            Assert.AreEqual(55f, _terrain.detailObjectDistance);
        }
    }
}
