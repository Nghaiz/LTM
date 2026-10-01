using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The terrain surface a bot's ground probe starts from and a fallen bot is stood back on
    /// (P30, Forest Lake). A real <see cref="Terrain"/> rather than a stand-in, because the rule
    /// is only as good as its reading of heightmaps, terrain positions and holes.
    /// </summary>
    public sealed class TerrainSurfaceTests
    {
        /// <summary>World Y of the flat test terrain's surface: 40 % of 50 m, on a terrain at y 3.</summary>
        private const float Surface = 23f;

        private TerrainData _data;
        private GameObject _terrain;

        [SetUp]
        public void SetUp()
        {
            _data = new TerrainData { heightmapResolution = 33 };
            _data.size = new Vector3(100f, 50f, 100f);

            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
            {
                for (int x = 0; x < 33; x++) heights[z, x] = 0.4f;
            }
            _data.SetHeights(0, 0, heights);

            _terrain = Terrain.CreateTerrainGameObject(_data);
            _terrain.transform.position = new Vector3(-50f, 3f, -50f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_terrain != null) Object.DestroyImmediate(_terrain);
            if (_data != null) Object.DestroyImmediate(_data);
        }

        [Test]
        public void TheSurfaceIsTheTerrainsHeightInWorldSpace()
        {
            Assert.IsTrue(TerrainSurface.TryGetHeight(new Vector3(0f, -200f, 0f), out float height),
                "found no surface over the middle of a terrain");
            Assert.AreEqual(Surface, height, 0.01f, "the terrain's own position was not added");
        }

        [Test]
        public void OffTheTerrainThereIsNoSurface()
        {
            Assert.IsFalse(TerrainSurface.TryGetHeight(new Vector3(70f, 0f, 0f), out float height));
            Assert.AreEqual(float.NegativeInfinity, height);
        }

        /// <summary>
        /// A hole has no collider, so a floor under it is somewhere a body may really stand. No
        /// shipping map has one yet, which is exactly why this must not be left to be found.
        /// </summary>
        [Test]
        public void AHoleIsNotASurface()
        {
            PunchHoleUnder(new Vector3(0f, 0f, 0f));

            Assert.IsFalse(TerrainSurface.TryGetHeight(new Vector3(0f, 0f, 0f), out _), "a hole read as ground");
            Assert.IsTrue(TerrainSurface.TryGetHeight(new Vector3(30f, 0f, 30f), out _), "the hole took the whole terrain with it");
        }

        /// <summary>
        /// The probe fix itself: a probe that starts inside a hillside passes through the surface
        /// it is looking for, so it must start on it.
        /// </summary>
        [Test]
        public void APointUnderTheSurfaceIsRaisedOntoIt()
        {
            Vector3 raised = TerrainSurface.AtOrAbove(new Vector3(5f, Surface - 6f, 5f));

            Assert.AreEqual(Surface, raised.y, 0.01f);
            Assert.AreEqual(5f, raised.x);
            Assert.AreEqual(5f, raised.z);
        }

        /// <summary>A body on a bridge or a roof keeps its height: only what is under the terrain moves.</summary>
        [Test]
        public void APointAboveTheSurfaceIsLeftAlone()
        {
            Assert.AreEqual(Surface + 4f, TerrainSurface.AtOrAbove(new Vector3(5f, Surface + 4f, 5f)).y, 1e-4f);
        }

        [Test]
        public void APointUnderAHoleIsLeftAlone()
        {
            PunchHoleUnder(new Vector3(0f, 0f, 0f));

            Assert.AreEqual(Surface - 6f, TerrainSurface.AtOrAbove(new Vector3(0f, Surface - 6f, 0f)).y, 1e-4f,
                "a body on a floor under a hole was lifted onto the terrain above it");
        }

        /// <summary>
        /// A limb dipping under a slope comes back up on its own; only a body the given depth
        /// under has fallen through.
        /// </summary>
        [Test]
        public void UnderMeansDeeperThanTheDepthGiven()
        {
            Assert.IsFalse(TerrainSurface.IsUnder(new Vector3(5f, Surface - 2f, 5f), 3f), "two metres under counted as three");
            Assert.IsTrue(TerrainSurface.IsUnder(new Vector3(5f, Surface - 4f, 5f), 3f), "four metres under did not count");
            Assert.IsFalse(TerrainSurface.IsUnder(new Vector3(70f, -500f, 0f), 3f), "off the terrain counted as under it");
        }

        /// <summary>
        /// A dedicated server build turns the <see cref="Terrain"/> component off
        /// (<c>ServerBuildSceneStrip</c>) and keeps the collider: the server's bots must still
        /// find the ground. <see cref="Terrain.GetActiveTerrains"/> lists no disabled terrain.
        /// </summary>
        [Test]
        public void ATerrainWithItsRendererOffIsStillGround()
        {
            _terrain.GetComponent<Terrain>().enabled = false;

            Assert.IsTrue(TerrainSurface.TryGetHeight(new Vector3(0f, -200f, 0f), out float height),
                "a server-built terrain lost its ground with its renderer");
            Assert.AreEqual(Surface, height, 0.01f);
        }

        /// <summary>The collider is what holds a body up; without one there is nothing to stand on.</summary>
        [Test]
        public void ATerrainWithNoColliderIsNotGround()
        {
            _terrain.GetComponent<TerrainCollider>().enabled = false;

            Assert.IsFalse(TerrainSurface.TryGetHeight(new Vector3(0f, -200f, 0f), out _),
                "a terrain nothing collides with read as ground");
        }

        /// <summary>
        /// The colliders are searched for once and kept, so a terrain that replaces a destroyed
        /// one -- the next map's -- must still be found.
        /// </summary>
        [Test]
        public void ATerrainThatReplacesADestroyedOneIsFound()
        {
            Assert.IsTrue(TerrainSurface.TryGetHeight(Vector3.zero, out _), "the fixture terrain was not found");

            Object.DestroyImmediate(_terrain);
            _terrain = Terrain.CreateTerrainGameObject(_data);
            _terrain.transform.position = new Vector3(-50f, 13f, -50f);

            Assert.IsTrue(TerrainSurface.TryGetHeight(Vector3.zero, out float height), "the replacement terrain was not found");
            Assert.AreEqual(Surface + 10f, height, 0.01f, "the destroyed terrain's height was kept");
        }

        /// <summary>Clears the one hole cell under <paramref name="point"/> (32 cells of 3.125 m).</summary>
        private void PunchHoleUnder(Vector3 point)
        {
            Vector3 origin = _terrain.transform.position;
            int resolution = _data.holesResolution;
            int x = Mathf.FloorToInt((point.x - origin.x) / _data.size.x * resolution);
            int z = Mathf.FloorToInt((point.z - origin.z) / _data.size.z * resolution);
            _data.SetHoles(x, z, new[,] { { false } });
            Assert.IsTrue(_data.IsHole(x, z), "the fixture failed to punch its hole, so the hole tests prove nothing");
        }
    }
}
