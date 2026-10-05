using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="DetailCatalog"/> takes exactly the detail prototypes it can draw as the terrain
    /// does -- Forest Lake's grass and rocks -- and refuses the whole terrain for any other, so the
    /// terrain keeps drawing it.
    /// </summary>
    public sealed class DetailCatalogTests
    {
        internal const string GrassPath = "Assets/ForestLake/Prefabs/Forest/Ter_Grass_A.prefab";
        internal const string RockPath = "Assets/ForestLake/Prefabs/Forest/Ter_Rock_C3.prefab";

        [Test]
        public void ForestLakeGrassAndRocksAreDrawnOnTheGpu()
        {
            DetailCatalog.Prototype grass = Read(MeshDetail(GrassPath), out string reason);
            Assert.IsNotNull(grass, reason);
            Assert.AreEqual(ShadowCastingMode.On, grass.ShadowCasting);
            StringAssert.StartsWith(ProceduralShaderCopy.Details.NamePrefix, grass.Parts[0].Material.shader.name);

            DetailCatalog.Prototype rock = Read(MeshDetail(RockPath), out reason);
            Assert.IsNotNull(rock, reason);
            Assert.AreEqual(ProceduralShaderCopy.Details.NamePrefix + "Standard", rock.Parts[0].Material.shader.name);
            Assert.IsFalse(rock.Casts);
        }

        [Test]
        public void TheGrassReachesAsFarAsItsLargestScaleAllows()
        {
            DetailPrototype source = MeshDetail(GrassPath);
            DetailCatalog.Prototype grass = Read(source, out string reason);
            Assert.IsNotNull(grass, reason);

            Mesh mesh = source.prototype.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(mesh.bounds.max.y, grass.Height, 1e-5f);
            Assert.GreaterOrEqual(grass.Radius, mesh.bounds.extents.magnitude, "the culling sphere is smaller than the mesh");
            Assert.AreEqual(Mathf.Max(source.maxWidth, source.maxHeight) * DetailCatalog.ScaleOvershoot, grass.MaxScale, 1e-5f);
        }

        [Test]
        public void ATextureDetailIsRefused()
        {
            var source = new DetailPrototype { usePrototypeMesh = false, renderMode = DetailRenderMode.GrassBillboard };
            Assert.IsNull(Read(source, out string reason));
            StringAssert.Contains("texture", reason);
        }

        [Test]
        public void AMeshDetailDrawnWithoutInstancingIsRefused()
        {
            DetailPrototype source = MeshDetail(GrassPath);
            source.useInstancing = false;
            Assert.IsNull(Read(source, out string reason));
            StringAssert.Contains("without instancing", reason);
        }

        [Test]
        public void ADetailAlignedToTheGroundIsRefused()
        {
            DetailPrototype source = MeshDetail(GrassPath);
            source.alignToGround = 0.5f;
            Assert.IsNull(Read(source, out string reason));
            StringAssert.Contains("aligned", reason);
        }

        [Test]
        public void ADetailOnATransformedPrefabRootIsRefused()
        {
            var prefab = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(GrassPath));
            try
            {
                prefab.transform.localScale = new Vector3(2f, 2f, 2f);
                DetailPrototype source = MeshDetail(GrassPath);
                source.prototype = prefab;
                Assert.IsNull(Read(source, out string reason));
                StringAssert.Contains("transformed root", reason);
            }
            finally
            {
                Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void AMaterialWithoutACopyIsRefused()
        {
            var prefab = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(RockPath));
            var material = new Material(Shader.Find("Unlit/Color")) { enableInstancing = true };
            try
            {
                prefab.GetComponent<MeshRenderer>().sharedMaterial = material;
                DetailPrototype source = MeshDetail(RockPath);
                source.prototype = prefab;
                Assert.IsNull(Read(source, out string reason));
                StringAssert.Contains("no procedural copy", reason);
            }
            finally
            {
                Object.DestroyImmediate(prefab);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AKeywordTheCopyLacksIsRefused()
        {
            var prefab = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(RockPath));
            var material = new Material(prefab.GetComponent<MeshRenderer>().sharedMaterial) { enableInstancing = true };
            material.EnableKeyword("_EMISSION");
            try
            {
                prefab.GetComponent<MeshRenderer>().sharedMaterial = material;
                DetailPrototype source = MeshDetail(RockPath);
                source.prototype = prefab;
                Assert.IsNull(Read(source, out string reason),
                    "an emissive Standard material went onto a copy that declares no _EMISSION, and would have lost its glow");
                StringAssert.Contains("does not declare", reason);
            }
            finally
            {
                Object.DestroyImmediate(prefab);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void OneRefusedPrototypeRefusesTheWholeTerrain()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(100f, 50f, 100f) };
            try
            {
                data.detailPrototypes = new[] { MeshDetail(GrassPath), new DetailPrototype { usePrototypeMesh = false } };
                Terrain terrain = Terrain.CreateTerrainGameObject(data).GetComponent<Terrain>();
                try
                {
                    Assert.IsNull(DetailCatalog.TryBuild(terrain, out string reason), "a terrain was taken with a prototype nobody would draw");
                    StringAssert.Contains("detail prototype 1", reason);
                }
                finally
                {
                    Object.DestroyImmediate(terrain.gameObject);
                }
            }
            finally
            {
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void ATerrainWithoutDetailsIsRefused()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(100f, 50f, 100f) };
            Terrain terrain = Terrain.CreateTerrainGameObject(data).GetComponent<Terrain>();
            try
            {
                Assert.IsNull(DetailCatalog.TryBuild(terrain, out string reason));
                StringAssert.Contains("no detail prototypes", reason);
            }
            finally
            {
                Object.DestroyImmediate(terrain.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        internal static DetailPrototype MeshDetail(string prefabPath)
        {
            return new DetailPrototype
            {
                prototype = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath),
                usePrototypeMesh = true,
                useInstancing = true,
                renderMode = DetailRenderMode.VertexLit,
                minWidth = 1f,
                maxWidth = 2f,
                minHeight = 1f,
                maxHeight = 2f,
            };
        }

        private static DetailCatalog.Prototype Read(DetailPrototype source, out string reason) =>
            DetailCatalog.TryReadPrototype(source, 0, out reason);
    }
}
