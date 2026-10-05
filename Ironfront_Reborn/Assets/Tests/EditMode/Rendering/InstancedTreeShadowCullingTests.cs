using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="InstancedTreeRenderer"/> draws a tree behind the camera only for a shadow that can
    /// fall into view, and then in the shadow passes alone (P31): every tree inside the shadow range
    /// used to be kept all round the camera, through the main pass and every cascade.
    /// </summary>
    /// <remarks>
    /// One pine 25 m behind a camera 20 m up at z = 1000, looking along +z and 30 degrees down at
    /// the ground ahead, inside the shadow range, and a directional sun 30 degrees up. Lit from
    /// behind, its shadow falls ahead into view; lit from ahead, it falls further behind.
    /// </remarks>
    public sealed class InstancedTreeShadowCullingTests
    {
        private const string PinePath = "Assets/ForestLake/Prefabs/Forest/SM_Pine_01.prefab";
        private const float Side = 6000f;
        private const int Lods = 5;

        private TerrainData _data;
        private Terrain _terrain;
        private InstancedTreeRenderer _trees;
        private GameObject _viewer;
        private Camera _camera;
        private GameObject _sunObject;
        private Light _sun;
        private Light _previousSun;
        private ShadowQuality _previousShadows;
        private float _previousDistance;

        [SetUp]
        public void SetUp()
        {
            _previousShadows = QualitySettings.shadows;
            _previousDistance = QualitySettings.shadowDistance;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 150f;

            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(Side, 600f, Side) };
            _data.treePrototypes = new[] { new TreePrototype { prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PinePath) } };
            _data.SetTreeInstances(new[] { Tree(975f) }, false);
            _terrain = Terrain.CreateTerrainGameObject(_data).GetComponent<Terrain>();
            _trees = _terrain.gameObject.AddComponent<InstancedTreeRenderer>();
            _trees.Build();
            Assert.IsTrue(_trees.IsBuilt, "Setup: the pine terrain was refused");

            _viewer = new GameObject("Viewer");
            _camera = _viewer.AddComponent<Camera>();
            _camera.fieldOfView = 60f;
            _camera.farClipPlane = 8000f;
            _viewer.transform.SetPositionAndRotation(new Vector3(Side * 0.5f, 20f, 1000f), Quaternion.Euler(30f, 0f, 0f));

            _sunObject = new GameObject("Sun");
            _sun = _sunObject.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
            _previousSun = RenderSettings.sun;
            RenderSettings.sun = _sun;
        }

        [TearDown]
        public void TearDown()
        {
            RenderSettings.sun = _previousSun;
            QualitySettings.shadows = _previousShadows;
            QualitySettings.shadowDistance = _previousDistance;
            Object.DestroyImmediate(_sunObject);
            Object.DestroyImmediate(_viewer);
            Object.DestroyImmediate(_terrain.gameObject);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void ATreeBehindWhoseShadowFallsIntoViewIsDrawnForItsShadowAlone()
        {
            // The light travels +z and down: from behind the camera, toward what it sees.
            _sunObject.transform.rotation = Quaternion.Euler(30f, 0f, 0f);

            _trees.Frame(_camera);

            Assert.AreEqual(1, Kept(InstancedTreeRenderer.Pass.ShadowOnly), "the shadow a seen patch of ground gets was dropped");
            Assert.AreEqual(0, Kept(InstancedTreeRenderer.Pass.Shadowed), "a tree behind the camera went through the main pass");
            Assert.Greater(_trees.ShadowOnlyDraws, 0, "nothing was drawn for the shadow");
        }

        [Test]
        public void ATreeBehindWhoseShadowFallsAwayIsNotDrawn()
        {
            // The light travels -z: the shadow falls further behind the camera.
            _sunObject.transform.rotation = Quaternion.Euler(30f, 180f, 0f);

            _trees.Frame(_camera);

            Assert.AreEqual(0, Kept(InstancedTreeRenderer.Pass.ShadowOnly) + Kept(InstancedTreeRenderer.Pass.Shadowed)
                               + Kept(InstancedTreeRenderer.Pass.Unshadowed),
                "a tree nobody can see, casting a shadow nobody can see, was drawn");
            Assert.AreEqual(0, _trees.ShadowOnlyDraws, "a draw was issued for a shadow no tree throws into view");
        }

        [Test]
        public void WithNoLightEveryTreeInTheShadowRangeKeepsItsShadow()
        {
            RenderSettings.sun = null;

            _trees.Frame(_camera);

            Assert.AreEqual(1, Kept(InstancedTreeRenderer.Pass.ShadowOnly),
                "with no light to say where shadows fall, a shadow in range was dropped");
        }

        private int Kept(InstancedTreeRenderer.Pass pass)
        {
            int kept = 0;
            for (int lod = 0; lod < Lods; lod++) kept += _trees.Kept(0, lod, pass);
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
            };
        }
    }
}
