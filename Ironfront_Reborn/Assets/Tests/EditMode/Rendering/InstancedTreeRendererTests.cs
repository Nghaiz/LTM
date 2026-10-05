using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="InstancedTreeRenderer"/> keeps a terrain's trees on their LODs, shadowed near the
    /// camera, culled by the GPU and read back here, and the terrain draws none of them meanwhile --
    /// on a terrain of Forest Lake's own pine.
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
            GpuDrivenDevice.RequireBuilt(_trees.IsBuilt, InstancedTreeRenderer.VertexBufferInputs, "pine");

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
        public void EveryTreeInViewOrInTheShadowsIsKeptOnItsLod()
        {
            _trees.Frame(_camera);

            Assert.AreEqual(1, _trees.Kept(0, 3, shadowed: true),
                "the tree 60 m ahead is not on LOD 3 with its shadow");
            Assert.AreEqual(2, _trees.Kept(0, 4, shadowed: false),
                "the trees 500 m and 1000 m ahead are not on the last LOD, unshadowed");
            Assert.AreEqual(3, KeptInAll(),
                "a tree under 1% of the view, or behind the camera and past the shadows, was kept");
            Assert.Greater(_trees.DrawCalls, 0, "nothing was drawn");
        }

        [Test]
        public void OnlyTheLodsATreeIsOnAreDrawn()
        {
            _trees.Frame(_camera);

            // One cell a tree here, so the reachable LODs are exactly the kept ones: LOD 3 with its
            // shadow, and the last LOD without.
            LOD[] lods = AssetDatabase.LoadAssetAtPath<GameObject>(PinePath).GetComponent<LODGroup>().GetLODs();
            int expected = Submeshes(lods[3], shadowCastersOnly: false) + Submeshes(lods[4], shadowCastersOnly: false);
            Assert.AreEqual(expected, _trees.DrawCalls - _trees.ShadowOnlyDraws,
                "draws were issued for LODs and shadows no tree is on: each costs a draw call a pass with no instances");
            // The out-of-view shadows of the one cell inside the shadow range, at most: with no
            // light set here, a cell the view only partly covers keeps them.
            Assert.LessOrEqual(_trees.ShadowOnlyDraws, Submeshes(lods[3], shadowCastersOnly: true));
        }

        private static int Submeshes(LOD lod, bool shadowCastersOnly)
        {
            int count = 0;
            foreach (Renderer renderer in lod.renderers)
            {
                if (shadowCastersOnly && renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off) continue;
                count += renderer.GetComponent<MeshFilter>().sharedMesh.subMeshCount;
            }
            return count;
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
        public void ZoomingInKeepsFinerLodsAndFartherTrees()
        {
            _camera.fieldOfView = 10f;

            _trees.Frame(_camera);

            Assert.AreEqual(1, _trees.Kept(0, 0, shadowed: true), "the tree 60 m ahead fills a scope and is not on its finest LOD");
            Assert.AreEqual(4, KeptInAll(), "the tree 4000 m ahead, visible through a scope, was not kept");
            Assert.GreaterOrEqual(_trees.Kept(0, 3, shadowed: false), 1, "the tree 500 m ahead, large through a scope, stayed on its last LOD");
        }

        [Test]
        public void WithNoCameraTheTerrainGetsItsTreesBack()
        {
            float bias = _terrain.treeLODBiasMultiplier;
            _trees.Frame(_camera);

            _trees.Frame(null);

            Assert.IsFalse(_trees.IsHolding);
            Assert.AreEqual(bias, _terrain.treeLODBiasMultiplier, "the trees are drawn by neither");
            Assert.AreEqual(0, _trees.DrawCalls);
        }

        [Test]
        public void WithVegetationOffNoTreeIsDrawn()
        {
            _terrain.drawTreesAndFoliage = false;

            _trees.Frame(_camera);

            Assert.AreEqual(0, _trees.DrawCalls, "trees were drawn with vegetation switched off");
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

        [Test]
        public void HowEachPassCasts()
        {
            var on = UnityEngine.Rendering.ShadowCastingMode.On;
            var off = UnityEngine.Rendering.ShadowCastingMode.Off;
            var only = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            Assert.AreEqual(off, InstancedTreeRenderer.CastingFor(InstancedTreeRenderer.Pass.Unshadowed, on));
            Assert.AreEqual(on, InstancedTreeRenderer.CastingFor(InstancedTreeRenderer.Pass.Shadowed, on));
            Assert.AreEqual(off, InstancedTreeRenderer.CastingFor(InstancedTreeRenderer.Pass.Shadowed, off));
            Assert.AreEqual(only, InstancedTreeRenderer.CastingFor(InstancedTreeRenderer.Pass.ShadowOnly, on),
                "a tree out of view drew in the main pass");
            Assert.AreEqual(off, InstancedTreeRenderer.CastingFor(InstancedTreeRenderer.Pass.ShadowOnly, off));
        }

        private int KeptInAll()
        {
            int kept = 0;
            for (int lod = 0; lod < 5; lod++)
                kept += _trees.Kept(0, lod, shadowed: false) + _trees.Kept(0, lod, shadowed: true);
            return kept;
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
