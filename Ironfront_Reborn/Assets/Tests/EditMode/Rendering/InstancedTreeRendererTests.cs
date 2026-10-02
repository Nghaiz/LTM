using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="InstancedTreeRenderer"/> draws a terrain's trees on their LODs, shadowed near the
    /// camera, and the terrain draws none of them meanwhile -- on a terrain of Forest Lake's own pine.
    /// </summary>
    /// <remarks>
    /// The camera stands at z = 1000 looking along +z. Pines stand 60 m ahead (LOD 3, inside the
    /// shadows), 500 m and 1000 m ahead (LOD 4), 4000 m ahead (under 1% of the view: culled by its
    /// LOD at a 60-degree view) and 500 m behind (outside the view and the shadows).
    /// </remarks>
    public sealed class InstancedTreeRendererTests
    {
        private const string PinePath = "Assets/ForestLake/Prefabs/Forest/SM_Pine_01.prefab";
        private const float Side = 6000f;

        private TerrainData _data;
        private Terrain _terrain;
        private InstancedTreeRenderer _trees;
        private GameObject _viewer;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(Side, 600f, Side) };
            _data.treePrototypes = new[] { new TreePrototype { prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PinePath) } };
            _data.SetTreeInstances(new[] { Tree(1060f), Tree(1500f), Tree(2000f), Tree(5000f), Tree(500f) }, false);

            _terrain = Terrain.CreateTerrainGameObject(_data).GetComponent<Terrain>();
            _trees = _terrain.gameObject.AddComponent<InstancedTreeRenderer>();
            _trees.Build();
            Assert.IsTrue(_trees.IsBuilt, "Setup: the pine terrain was refused");

            _viewer = new GameObject("Viewer");
            _camera = _viewer.AddComponent<Camera>();
            _camera.fieldOfView = 60f;
            _camera.farClipPlane = 8000f;
            _viewer.transform.position = new Vector3(Side * 0.5f, 20f, 1000f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_viewer);
            Object.DestroyImmediate(_terrain.gameObject);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void EveryTreeInViewOrInTheShadowsIsDrawnOnItsLod()
        {
            _trees.Frame(_camera);

            Assert.AreEqual(3, _trees.Drawn,
                "the trees 60 m, 500 m and 1000 m ahead, and only those: not the one under 1% of the view, "
                + "nor the one behind the camera and past the shadows");
            Assert.AreEqual(1, _trees.DrawnAtLod[3], "the tree 60 m ahead is not on LOD 3");
            Assert.AreEqual(2, _trees.DrawnAtLod[4], "the trees 500 m and 1000 m ahead are not on the last LOD");
            Assert.AreEqual(1, _trees.DrawnShadowed, "only the tree inside the shadow distance casts a shadow");
        }

        [Test]
        public void TheTerrainDrawsNoTreeMeanwhile()
        {
            float bias = _terrain.treeLODBiasMultiplier;

            _trees.Frame(_camera);

            Assert.IsTrue(_trees.IsHolding);
            Assert.AreEqual(InstancedTreeRenderer.CulledBias, _terrain.treeLODBiasMultiplier,
                "the terrain still draws its trees, every one of them twice");
            Assert.AreEqual(bias, _trees.TerrainBias, "the terrain's own LOD bias was lost");
        }

        [Test]
        public void ZoomingInDrawsFinerLodsAndFartherTrees()
        {
            _camera.fieldOfView = 10f;

            _trees.Frame(_camera);

            Assert.AreEqual(4, _trees.Drawn, "the tree 4000 m ahead, visible through a scope, was not drawn");
            Assert.AreEqual(1, _trees.DrawnAtLod[0], "the tree 60 m ahead fills a scope and is not on its finest LOD");
            Assert.GreaterOrEqual(_trees.DrawnAtLod[3], 1, "the tree 500 m ahead, large through a scope, stayed on its last LOD");
            Assert.AreEqual(3, _trees.DrawnAtLod[3] + _trees.DrawnAtLod[4]);
        }

        [Test]
        public void WithNoCameraTheTerrainGetsItsTreesBack()
        {
            float bias = _terrain.treeLODBiasMultiplier;
            _trees.Frame(_camera);

            _trees.Frame(null);

            Assert.IsFalse(_trees.IsHolding);
            Assert.AreEqual(bias, _terrain.treeLODBiasMultiplier, "the trees are drawn by neither");
            Assert.AreEqual(0, _trees.Drawn);
        }

        [Test]
        public void WithVegetationOffNoTreeIsDrawn()
        {
            _terrain.drawTreesAndFoliage = false;

            _trees.Frame(_camera);

            Assert.AreEqual(0, _trees.Drawn, "trees were drawn with vegetation switched off");
            Assert.IsFalse(_trees.IsHolding);
        }

        [Test]
        public void SwitchedOffItHandsTheTreesBack()
        {
            float bias = _terrain.treeLODBiasMultiplier;
            _trees.Frame(_camera);

            // Edit mode sends no OnDisable to this component: invoked as disabling it would.
            typeof(InstancedTreeRenderer).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_trees, null);

            Assert.AreEqual(bias, _terrain.treeLODBiasMultiplier, "a disabled renderer left the terrain without trees");
        }

        [Test]
        public void ATerrainWithoutTreesIsLeftAlone()
        {
            var empty = new TerrainData { heightmapResolution = 33, size = new Vector3(100f, 100f, 100f) };
            Terrain bare = Terrain.CreateTerrainGameObject(empty).GetComponent<Terrain>();
            try
            {
                InstancedTreeRenderer trees = bare.gameObject.AddComponent<InstancedTreeRenderer>();
                trees.Build();

                Assert.IsFalse(trees.IsBuilt);
                Assert.IsFalse(trees.enabled);
            }
            finally
            {
                Object.DestroyImmediate(bare.gameObject);
                Object.DestroyImmediate(empty);
            }
        }

        private static TreeInstance Tree(float z)
        {
            return new TreeInstance
            {
                position = new Vector3(0.5f, 0f, z / Side),
                widthScale = 1f,
                heightScale = 1f,
                color = Color.white,
                lightmapColor = Color.white,
                prototypeIndex = 0,
            };
        }
    }
}
