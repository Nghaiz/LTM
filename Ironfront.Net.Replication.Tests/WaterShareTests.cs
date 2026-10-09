using System;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A path triangle's share of swimming-deep water is read from its whole area, not its centre.
    /// </summary>
    /// <remarks>
    /// Forest Lake's recast graph crossed the bay north of the Island with triangles whose centres
    /// stood on the bank, so no water penalty applied and bots swam the bay and drowned: every long
    /// swim of three bot soaks, and five drownings in the v4.5.0 playtest of 2026-10-07.
    /// </remarks>
    public sealed class WaterShareTests
    {
        private static readonly Vec3 A = new Vec3(0f, 0f, 0f);
        private static readonly Vec3 B = new Vec3(200f, 0f, 0f);
        private static readonly Vec3 C = new Vec3(0f, 0f, 200f);

        [Fact]
        public void ATriangleOnDryGroundHasNoWater()
        {
            Assert.Equal(0f, WaterShare.OfTriangle(A, B, C, (x, y, z) => false));
        }

        [Fact]
        public void ATriangleUnderWaterIsAllWater()
        {
            Assert.Equal(1f, WaterShare.OfTriangle(A, B, C, (x, y, z) => true));
        }

        [Fact]
        public void ATriangleWhoseCentreIsAshoreStillCountsTheWaterAcrossIt()
        {
            // Water beyond x = 80: the centre (66.7, 66.7) is dry, a third of the area is not.
            Func<float, float, float, bool> deep = (x, y, z) => x > 80f;
            Assert.False(deep((A.X + B.X + C.X) / 3f, 0f, (A.Z + B.Z + C.Z) / 3f));

            float share = WaterShare.OfTriangle(A, B, C, deep);

            Assert.InRange(share, 0.25f, 0.45f);
        }

        [Fact]
        public void AHugeTriangleIsSampledAtMostFortyFiveTimes()
        {
            int calls = 0;
            WaterShare.OfTriangle(A, new Vec3(5000f, 0f, 0f), new Vec3(0f, 0f, 5000f), (x, y, z) => { calls++; return false; });

            Assert.Equal(45, calls);
        }

        [Fact]
        public void ASmallTriangleIsSampledAtItsCorners()
        {
            int calls = 0;
            WaterShare.OfTriangle(A, new Vec3(1f, 0f, 0f), new Vec3(0f, 0f, 1f), (x, y, z) => { calls++; return false; });

            Assert.Equal(3, calls);
        }

        [Fact]
        public void TheSamplesFollowTheTriangleUpAndDownHill()
        {
            // The ground's height at a sample is the triangle's plane there, which is what decides depth.
            Vec3 high = new Vec3(0f, 10f, 200f);
            float share = WaterShare.OfTriangle(A, B, high, (x, y, z) => y < 5f);

            Assert.InRange(share, 0.4f, 0.75f);
        }

        [Fact]
        public void AShoreTriangleCostsAShareOfTheWaterPenalty()
        {
            const uint water = 1000000;

            Assert.Equal(0u, WaterShare.ShorePenalty(0f, water));
            Assert.Equal(water / 2, WaterShare.ShorePenalty(0.25f, water));
            Assert.Equal(water, WaterShare.ShorePenalty(WaterShare.TagShare, water));
            Assert.Equal(water, WaterShare.ShorePenalty(1f, water));
            Assert.Equal(0u, WaterShare.ShorePenalty(float.NaN, water));
        }

        [Fact]
        public void ASampleTestIsRequired()
        {
            Assert.Throws<ArgumentNullException>(() => WaterShare.OfTriangle(A, B, C, null!));
        }
    }
}
