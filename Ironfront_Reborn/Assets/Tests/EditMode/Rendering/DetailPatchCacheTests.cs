using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// <see cref="DetailPatchCache"/> keeps the terrain's own details, the same every time they are
    /// read, packed so the GPU decodes them within a hair of where the terrain draws them.
    /// </summary>
    public sealed class DetailPatchCacheTests
    {
        private const string ForestLakePath = "Assets/TerrainData/ForestLake_Terrain.asset";

        [Test]
        public void PackingKeepsEveryDetailWithinAHairOfItsPlace()
        {
            var details = new[]
            {
                Detail(10f, 4f, 20f, 0.1f, 1.2f, 1f),
                Detail(108.7f, 38.5f, 119.3f, 6.2f, 3.36f, 2.38f),
                Detail(55f, 12f, 70f, 3.14159f, 0.83f, 0.9f),
                Detail(60f, 13f, 71f, -0.5f, 2f, 1.5f),
            };
            var terrainOrigin = new Vector3(-500f, 10f, 250f);

            DetailPatchCache.Packed[] packed = DetailPatchCache.Pack(details, terrainOrigin, out Vector2 scaleMin, out Vector2 scaleRange);

            for (int i = 0; i < details.Length; i++)
            {
                DetailPatchCache.Unpack(packed[i], scaleMin, scaleRange,
                    out Vector3 position, out float rotation, out float scaleXZ, out float scaleY);
                var expected = new Vector3(details[i].posX, details[i].posY, details[i].posZ) + terrainOrigin;
                Assert.AreEqual(expected, position, $"detail {i} moved: positions are kept whole");
                Assert.AreEqual(details[i].scaleXZ, scaleXZ, 1e-4f, $"detail {i} changed width");
                Assert.AreEqual(details[i].scaleY, scaleY, 1e-4f, $"detail {i} changed height");
                float turned = Mathf.DeltaAngle(details[i].rotationY * Mathf.Rad2Deg, rotation * Mathf.Rad2Deg);
                Assert.Less(Mathf.Abs(turned), 0.01f, $"detail {i} turned");
            }
        }

        [Test]
        public void ForestLakeIsReadTheSameEveryTime()
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(ForestLakePath);
            Assert.IsNotNull(data, "Setup: no Forest Lake terrain data");

            DetailInstanceTransform[] first = data.ComputeDetailInstanceTransforms(13, 13, 0, 1f, out Bounds a);
            DetailInstanceTransform[] second = data.ComputeDetailInstanceTransforms(13, 13, 0, 1f, out Bounds b);
            Assert.Greater(first.Length, 1000, "Setup: the densest grass patch is nearly empty");
            Assert.AreEqual(first.Length, second.Length);
            Assert.AreEqual(a, b);

            DetailPatchCache.Packed[] packedFirst = DetailPatchCache.Pack(first, Vector3.zero, out _, out _);
            DetailPatchCache.Packed[] packedSecond = DetailPatchCache.Pack(second, Vector3.zero, out _, out _);
            for (int i = 0; i < packedFirst.Length; i++)
            {
                Assert.AreEqual(packedFirst[i].Position, packedSecond[i].Position, $"detail {i} moved between two reads: grass would shimmer");
                Assert.AreEqual(packedFirst[i].TurnWidth, packedSecond[i].TurnWidth);
                Assert.AreEqual(packedFirst[i].Height, packedSecond[i].Height);
            }
        }

        [Test]
        public void ALowerDensityKeepsASubsetOfTheSameDetails()
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(ForestLakePath);
            DetailInstanceTransform[] full = data.ComputeDetailInstanceTransforms(13, 13, 0, 1f, out _);
            DetailInstanceTransform[] thin = data.ComputeDetailInstanceTransforms(13, 13, 0, 0.35f, out _);

            var places = new System.Collections.Generic.HashSet<(float, float)>();
            foreach (DetailInstanceTransform detail in full) places.Add((detail.posX, detail.posZ));
            Assert.Less(thin.Length, full.Length);
            foreach (DetailInstanceTransform detail in thin)
                Assert.IsTrue(places.Contains((detail.posX, detail.posZ)), "a thinner density placed grass where the full one has none");
        }

        private static DetailInstanceTransform Detail(float x, float y, float z, float rotation, float scaleXZ, float scaleY)
        {
            return new DetailInstanceTransform { posX = x, posY = y, posZ = z, rotationY = rotation, scaleXZ = scaleXZ, scaleY = scaleY };
        }
    }
}
