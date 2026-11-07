using System;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A swimmer with no way out finds the nearest place it can stand, and a cliff is not one.
    /// Server log 2026-11-07: two bots drowned at the surface off Island.
    /// </summary>
    public sealed class ShoreFinderTests
    {
        private const float Surface = 18f;
        private const float StandDepth = 1.2f;
        private const float ClimbLip = 0.6f;

        /// <summary>A sea floor 8 m down everywhere west of <paramref name="shoreX"/>, dry land east of it.</summary>
        private static Func<float, float, float> BeachAt(float shoreX)
            => (x, z) => x >= shoreX ? Surface + 0.3f : Surface - 8f;

        [Test]
        public void ASwimmerFindsTheBeachAhead()
        {
            Assert.IsTrue(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, BeachAt(20f), out float x, out float z));
            Assert.GreaterOrEqual(x, 20f, "on the beach");
            Assert.LessOrEqual(Math.Sqrt(x * x + z * z), 20f + ShoreFinder.StepMetres,
                "no further than one step past the nearest point of the shore");
        }

        [Test]
        public void TheNearerOfTwoShoresWins()
        {
            Func<float, float, float> strait = (x, z) => x >= 30f || x <= -12f ? Surface : Surface - 8f;

            Assert.IsTrue(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, strait, out float x, out _));
            Assert.Less(x, 0f, "the west shore is 12 m away, the east 30 m");
        }

        /// <summary>
        /// The defect's shape: the nearest land is a cliff the swim cannot climb, and a beach lies
        /// further off. The beach is the answer.
        /// </summary>
        [Test]
        public void ACliffIsNotAShore()
        {
            Func<float, float, float> coast = (x, z) =>
                x >= 10f ? Surface + 6f      // a cliff 10 m east
                : z >= 40f ? Surface + 0.2f  // a beach 40 m north
                : Surface - 8f;

            Assert.IsTrue(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, coast, out float x, out float z));
            Assert.Less(x, 10f, "never onto the cliff");
            Assert.GreaterOrEqual(z, 40f - ShoreFinder.StepMetres);
        }

        [Test]
        public void ShallowsCountAsStanding()
        {
            Func<float, float, float> shallows = (x, z) => x >= 6f ? Surface - 1f : Surface - 5f;

            Assert.IsTrue(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, shallows, out float x, out _));
            Assert.Less(x, 6f + ShoreFinder.StepMetres);
        }

        [Test]
        public void OpenSeaHasNoShore()
        {
            Func<float, float, float> sea = (x, z) => Surface - 30f;

            Assert.IsFalse(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, sea, out _, out _));
        }

        [Test]
        public void AShoreOutOfReachIsNotLookedFor()
        {
            Assert.IsFalse(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip,
                BeachAt(ShoreFinder.ReachMetres + 10f), out _, out _));
        }

        [Test]
        public void OffTheMapIsNotAShore()
        {
            Func<float, float, float> edge = (x, z) => x >= 5f ? float.NegativeInfinity : Surface - 8f;

            Assert.IsFalse(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, edge, out _, out _));
        }

        [Test]
        public void NoSurfaceNoSearch()
        {
            Assert.IsFalse(ShoreFinder.TryFind(0f, 0f, float.NegativeInfinity, StandDepth, ClimbLip, BeachAt(5f), out _, out _));
            Assert.IsFalse(ShoreFinder.TryFind(0f, 0f, Surface, StandDepth, ClimbLip, null, out _, out _));
        }
    }
}
