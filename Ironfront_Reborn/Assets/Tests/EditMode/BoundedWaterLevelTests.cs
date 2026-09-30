using System;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A lake -- <c>WaterLevel</c> with <c>SurfaceMesh</c> coverage -- is water only over its own
    /// mesh, and a networked player's movement sees exactly the water the game does (P30, Forest
    /// Lake). Without the second half the lake is a floor a player walks along with no breath to
    /// lose while the bots, which swim by <c>WaterLevel</c>, swim it.
    /// </summary>
    /// <remarks>
    /// <c>WaterLevel</c> lives in Assembly-CSharp, which no asmdef can reference, so it is reached by
    /// name; its Awake/OnEnable/OnDisable/OnDestroy do not run in edit mode and are called here.
    /// </remarks>
    public sealed class BoundedWaterLevelTests
    {
        private const float Surface = 30f;
        private const float LakeCentre = 100f;
        private const float LakeHalfSize = 10f;

        private static readonly Type WaterLevelType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("WaterLevel", false))
            .First(type => type != null);

        private float _savedSea;
        private IBoundedWater _savedBounded;
        private GameObject _body;
        private Component _water;
        private Mesh _mesh;

        [SetUp]
        public void SetUp()
        {
            _savedSea = MovementCore.WaterHeight;
            _savedBounded = MovementCore.BoundedWater;
            MovementCore.WaterHeight = float.NegativeInfinity;
            MovementCore.BoundedWater = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_water != null)
            {
                Invoke(_water, "OnDisable");
                Invoke(_water, "OnDestroy");
            }
            if (_body != null) UnityEngine.Object.DestroyImmediate(_body);
            if (_mesh != null) UnityEngine.Object.DestroyImmediate(_mesh);
            MovementCore.WaterHeight = _savedSea;
            MovementCore.BoundedWater = _savedBounded;
        }

        [Test]
        public void ALakeIsWaterOnlyOverItsOwnMesh()
        {
            MakeBody("SurfaceMesh", Surface);

            Assert.IsNotNull(MovementCore.BoundedWater, "the lake did not reach the movement");
            Assert.AreEqual(Surface, MovementCore.SurfaceAt(LakeCentre, LakeCentre), 1e-4f);
            Assert.IsTrue(MovementCore.IsInWater(LakeCentre, Surface - 1f, LakeCentre));
            Assert.IsTrue(InWater(new Vector3(LakeCentre, Surface - 1f, LakeCentre)));

            float beside = LakeCentre + LakeHalfSize + 20f;
            Assert.IsTrue(float.IsNegativeInfinity(MovementCore.SurfaceAt(beside, LakeCentre)));
            Assert.IsFalse(MovementCore.IsInWater(beside, Surface - 1f, LakeCentre));
            Assert.IsFalse(InWater(new Vector3(beside, Surface - 1f, LakeCentre)));
        }

        [Test]
        public void AValleyLowerThanTheLakeBesideItIsDry()
        {
            MakeBody("SurfaceMesh", Surface);

            // The case a single sea level gets wrong, and the reason bounded water exists.
            var valley = new Vector3(LakeCentre + LakeHalfSize + 20f, Surface - 12f, LakeCentre);
            Assert.IsFalse(InWater(valley), "the game flooded a dry valley below the lake");
            Assert.IsFalse(MovementCore.IsInWater(valley.x, valley.y, valley.z),
                "the movement flooded a dry valley below the lake");
        }

        [Test]
        public void TheMovementSeesTheSameSurfaceAsTheGame()
        {
            MakeBody("SurfaceMesh", Surface);

            for (float x = LakeCentre - 15f; x <= LakeCentre + 15f; x += 2.5f)
            {
                for (float z = LakeCentre - 15f; z <= LakeCentre + 15f; z += 2.5f)
                {
                    var point = new Vector3(x, 0f, z);
                    float gameSurface = Depth(point) + point.y;
                    float movementSurface = MovementCore.SurfaceAt(x, z);
                    if (float.IsNegativeInfinity(gameSurface) || float.IsNegativeInfinity(movementSurface))
                    {
                        Assert.AreEqual(float.IsNegativeInfinity(gameSurface), float.IsNegativeInfinity(movementSurface),
                            $"at ({x}, {z}) one side had water and the other had none");
                        continue;
                    }
                    Assert.AreEqual(gameSurface, movementSurface, 1e-4f, $"at ({x}, {z})");
                }
            }
        }

        [Test]
        public void TakingTheLakeAwayTakesItsWaterWithIt()
        {
            MakeBody("SurfaceMesh", Surface);
            Invoke(_water, "OnDisable");

            Assert.IsNull(MovementCore.BoundedWater, "a lake that left the map kept its water in the movement");
            Assert.IsTrue(float.IsNegativeInfinity(MovementCore.SurfaceAt(LakeCentre, LakeCentre)));
        }

        [Test]
        public void ASeaStillPublishesItsHeightAndClearsItOnTheWayOut()
        {
            MakeBody("Everywhere", 5f);

            Assert.AreEqual(5f, MovementCore.WaterHeight);
            Assert.IsNull(MovementCore.BoundedWater, "a sea registered itself as a lake");
            Assert.IsTrue(MovementCore.IsInWater(-4000f, 4f, 9000f), "a sea stopped covering the whole map");

            Invoke(_water, "OnDestroy");
            Assert.IsTrue(float.IsNegativeInfinity(MovementCore.WaterHeight), "the sea outlived its map");
            _water = null;
        }

        private void MakeBody(string coverage, float height)
        {
            _body = new GameObject("Test Water");
            _body.transform.position = new Vector3(LakeCentre, height, LakeCentre);
            _mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-LakeHalfSize, 0f, -LakeHalfSize), new Vector3(-LakeHalfSize, 0f, LakeHalfSize),
                    new Vector3(LakeHalfSize, 0f, LakeHalfSize), new Vector3(LakeHalfSize, 0f, -LakeHalfSize),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            _body.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _water = _body.AddComponent(WaterLevelType);
            FieldInfo field = WaterLevelType.GetField("coverage");
            field.SetValue(_water, Enum.Parse(field.FieldType, coverage));
            Invoke(_water, "Awake");
            Invoke(_water, "OnEnable");
        }

        private static void Invoke(Component target, string method)
            => WaterLevelType.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

        private static bool InWater(Vector3 position)
            => (bool)WaterLevelType.GetMethod("InWater", BindingFlags.Static | BindingFlags.Public)!
                .Invoke(null, new object[] { position });

        private static float Depth(Vector3 position)
            => (float)WaterLevelType.GetMethod("Depth", BindingFlags.Static | BindingFlags.Public)!
                .Invoke(null, new object[] { position });
    }
}
