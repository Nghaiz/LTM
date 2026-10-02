using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>LODGroup's selection, as <see cref="TreeLod"/> computes it.</summary>
    public sealed class TreeLodTests
    {
        private static readonly float TanHalf60 = Mathf.Tan(30f * Mathf.Deg2Rad);
        private static readonly float[] Pine = { 0.99f, 0.98f, 0.97f, 0.15f, 0.01f };

        [Test]
        public void AGroupFillsTheViewItsSizeAway()
        {
            // At the distance where the view is exactly the group's size high, it fills the view.
            float distance = 10f / (2f * TanHalf60);

            Assert.AreEqual(1f, TreeLod.RelativeHeight(10f, distance, TanHalf60, 1f), 1e-5f);
            Assert.AreEqual(2f, TreeLod.RelativeHeight(10f, distance, TanHalf60, 2f), 1e-5f,
                "LOD bias does not scale the relative height");
        }

        [Test]
        public void DistanceForHeightIsTheInverse()
        {
            float distance = TreeLod.DistanceForHeight(23.1f, 0.15f, TanHalf60, 1.5f);

            Assert.AreEqual(0.15f, TreeLod.RelativeHeight(23.1f, distance, TanHalf60, 1.5f), 1e-5f);
        }

        [Test]
        public void EachLodLastsUntilItsThresholdAndNothingPastTheLast()
        {
            var squared = new float[Pine.Length];
            TreeLod.SquaredDistances(Pine, 23.1f, TanHalf60, 1f, squared);
            float lod3Ends = TreeLod.DistanceForHeight(23.1f, 0.15f, TanHalf60, 1f);
            float culled = TreeLod.DistanceForHeight(23.1f, 0.01f, TanHalf60, 1f);

            Assert.AreEqual(3, TreeLod.Select(Sq(lod3Ends * 0.99f), 1f, squared));
            Assert.AreEqual(4, TreeLod.Select(Sq(lod3Ends * 1.01f), 1f, squared));
            Assert.AreEqual(4, TreeLod.Select(Sq(culled * 0.99f), 1f, squared));
            Assert.AreEqual(-1, TreeLod.Select(Sq(culled * 1.01f), 1f, squared), "a group under its last threshold was drawn");
            Assert.AreEqual(4, TreeLod.Select(Sq(culled * 1.5f), Sq(2f), squared),
                "a tree twice the size does not last twice as far");
        }

        private static float Sq(float value) => value * value;
    }
}
