using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="InstancedDetailRenderer"/> draws the details the terrain would -- every instance of
    /// a patch within the detail distance, none past it -- culled by the GPU and read back here, and
    /// the terrain draws none meanwhile; on a terrain of Forest Lake's own grass.
    /// </summary>
    /// <remarks>
    /// The terrain is 512 m square, 8 m cells, 64 m patches; its own settings, not the preset's, give
    /// a detail distance of 110 m. The camera stands at z = 100 looking along +z. Grass stands 60 m
    /// ahead (4); 92-100 m ahead (1) and 148 m ahead (3) in one patch, whose bounds -- those of its
    /// instances, not its square -- the near one brings within the distance; 60 m behind (4) and
    /// 380 m ahead (4); and 30 m ahead but 56 m to the right (2), in the patch of the grass 60 m
    /// ahead and outside the 16:9 view, so only the GPU's cull of each instance can leave it out.
    /// </remarks>
    public sealed class InstancedDetailRendererTests
    {
        private const float Side = 512f;
        private const int Resolution = 64;
        private const float Cell = Side / Resolution;
        private const float Distance = 110f;

        private TerrainData _data;
        private Terrain _terrain;
        private InstancedDetailRenderer _details;
        private GameObject _viewer;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(Side, 100f, Side) };
            _data.SetDetailResolution(Resolution, 8);
            _data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            DetailPrototype grass = DetailCatalogTests.MeshDetail(DetailCatalogTests.GrassPath);
            grass.useDensityScaling = false;
            _data.detailPrototypes = new[] { grass };
            var counts = new int[Resolution, Resolution];
            counts[CellAt(160f), Resolution / 2] = 4;
            counts[CellAt(192f), Resolution / 2] = 1;
            counts[CellAt(248f), Resolution / 2] = 3;
            counts[CellAt(40f), Resolution / 2] = 4;
            counts[CellAt(480f), Resolution / 2] = 4;
            counts[CellAt(130f), CellAt(316f)] = 2;
            _data.SetDetailLayer(0, 0, 0, counts);

            _terrain = Terrain.CreateTerrainGameObject(_data).GetComponent<Terrain>();
            _terrain.ignoreQualitySettings = true;
            _terrain.detailObjectDistance = Distance;
            _terrain.detailObjectDensity = 1f;
            _details = _terrain.gameObject.AddComponent<InstancedDetailRenderer>();
            _details.Build();
            GpuDrivenDevice.RequireBuilt(_details.IsBuilt, InstancedDetailRenderer.VertexBufferInputs, "grass");

            _viewer = new GameObject("Viewer");
            _camera = _viewer.AddComponent<Camera>();
            _camera.fieldOfView = 60f;
            _camera.farClipPlane = 2000f;
            _camera.aspect = 16f / 9f;
            _viewer.transform.position = new Vector3(Side * 0.5f + Cell * 0.5f, 2f, 100f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_viewer);
            Object.DestroyImmediate(_terrain.gameObject);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void EveryDetailOfANearPatchInViewIsKeptAndNoneOfAFarOne()
        {
            TakeOver();

            Assert.AreEqual(8, _details.Kept(0, caster: false),
                "the grass in view was not kept whole (4 at 60 m, and all 4 of the patch that comes within 110 m, 3 of them at 148 m), "
                + "or the 2 outside the view in a patch the view meets were kept");
            int casters = _details.Kept(0, caster: true);
            if (QualitySettings.shadows != ShadowQuality.Disable)
                Assert.GreaterOrEqual(casters, 4, "the grass 60 m ahead throws no shadow");
            Assert.LessOrEqual(_details.Kept(0, false) + casters, 8 + 8 + 4 + 2,
                "the grass 380 m ahead, in a patch past the distance, was kept");
            Assert.Greater(_details.DrawCalls, 0, "nothing was drawn");
        }

        [Test]
        public void OneDrawAPassIsIssuedForTheGrass()
        {
            TakeOver();

            int expected = QualitySettings.shadows != ShadowQuality.Disable ? 2 : 1;
            Assert.AreEqual(expected, _details.DrawCalls, "the grass is drawn in more than one draw a pass");
            Assert.AreEqual(expected - 1, _details.ShadowOnlyDraws);
        }

        [Test]
        public void TheTerrainDrawsNoDetailMeanwhile()
        {
            TakeOver();

            Assert.AreEqual(0f, _terrain.detailObjectDistance, "the terrain still draws its details, every one of them twice");
            Assert.AreEqual(Distance, _details.HandOff.Distance, "the terrain's own detail distance was lost");
        }

        [Test]
        public void UntilItsPatchesAreReadTheTerrainKeepsItsDetails()
        {
            _details.Frame(_camera);

            if (!_details.IsHolding)
                Assert.AreEqual(Distance, _terrain.detailObjectDistance, "the details are drawn by neither while their patches are read");
            Assert.Greater(_details.Baked, 0, "no patch was read");
        }

        [Test]
        public void AtADetailDistanceOfZeroNothingIsDrawn()
        {
            _terrain.detailObjectDistance = 0f;

            _details.Frame(_camera);

            Assert.AreEqual(0, _details.DrawCalls);
            Assert.IsFalse(_details.IsHolding);
            Assert.AreEqual(0f, _terrain.detailObjectDistance);
        }

        [Test]
        public void WithNoCameraTheTerrainGetsItsDetailsBack()
        {
            TakeOver();

            _details.Frame(null);

            Assert.IsFalse(_details.IsHolding);
            Assert.AreEqual(Distance, _terrain.detailObjectDistance, "the details are drawn by neither");
        }

        [Test]
        public void WithVegetationOffNoDetailIsDrawn()
        {
            _terrain.drawTreesAndFoliage = false;

            _details.Frame(_camera);

            Assert.AreEqual(0, _details.DrawCalls);
            Assert.IsFalse(_details.IsHolding);
        }

        [Test]
        public void ADensityChangeHandsTheDetailsBackWhileTheyAreReadAgain()
        {
            TakeOver();

            _terrain.detailObjectDensity = 0.5f;
            _details.Frame(_camera);

            Assert.AreEqual(0.5f, _details.Cache.Density, 1e-6f, "the patches were not read again at the new density");
            for (int frame = 0; frame < 20 && !_details.IsHolding; frame++) _details.Frame(_camera);
            Assert.IsTrue(_details.IsHolding, "the details were never taken back after the density changed");
        }

        [Test]
        public void SwitchedOffItHandsTheDetailsBack()
        {
            TakeOver();

            // Edit mode sends no OnDisable to this component: invoked as disabling it would.
            typeof(InstancedDetailRenderer).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_details, null);

            Assert.AreEqual(Distance, _terrain.detailObjectDistance, "a disabled renderer left the terrain without details");
        }

        [Test]
        public void FarPatchesAreDroppedAndReadAgainOnReturn()
        {
            TakeOver();
            int near = _details.Cache.Resident.Count;

            _viewer.transform.position = new Vector3(Side * 0.5f, 2f, Side - 10f);
            TakeOver();
            foreach (DetailPatchCache.Entry entry in _details.Cache.Resident)
                Assert.GreaterOrEqual(entry.PatchZ, 2, "a patch 400 m behind is still kept");

            _viewer.transform.position = new Vector3(Side * 0.5f + Cell * 0.5f, 2f, 100f);
            TakeOver();
            Assert.AreEqual(8, _details.Kept(0, caster: false), "the grass came back different");
            Assert.Greater(near, 0);
        }

        private void TakeOver()
        {
            for (int frame = 0; frame < 20 && !_details.IsHolding; frame++) _details.Frame(_camera);
            Assert.IsTrue(_details.IsHolding, "the details were never taken over from the terrain");
            _details.Frame(_camera);
        }

        private static int CellAt(float z) => Mathf.FloorToInt(z / Cell);
    }
}
