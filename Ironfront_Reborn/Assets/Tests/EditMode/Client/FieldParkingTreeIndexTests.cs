using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The vehicle parking tests know about the trees of a terrain whose Terrain component is
    /// switched off, as every terrain on a dedicated server is.
    /// </summary>
    /// <remarks>
    /// Owner, 2026-10-08: vehicles placed at random were often stuck among trees. The live servers
    /// indexed no tree at all: <c>FieldParking</c> read <c>Terrain.activeTerrains</c>, which is empty
    /// when the components are off, so every tree clearance test passed. <c>FieldParking</c> lives in
    /// Assembly-CSharp, so it is reached by name.
    /// </remarks>
    public sealed class FieldParkingTreeIndexTests
    {
        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false))
            .First(type => type != null);

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void TheTreesOfASwitchedOffTerrainAreIndexed()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var tree = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(200f, 50f, 200f) };
            data.treePrototypes = new[] { new TreePrototype { prefab = tree } };
            data.SetTreeInstances(new[]
            {
                new TreeInstance { position = new Vector3(0.25f, 0f, 0.25f), prototypeIndex = 0, widthScale = 1f, heightScale = 1f },
                new TreeInstance { position = new Vector3(0.50f, 0f, 0.50f), prototypeIndex = 0, widthScale = 1f, heightScale = 1f },
                new TreeInstance { position = new Vector3(0.75f, 0f, 0.75f), prototypeIndex = 0, widthScale = 1f, heightScale = 1f },
            }, snapToHeightmap: false);

            Terrain terrain = Terrain.CreateTerrainGameObject(data).GetComponent<Terrain>();
            terrain.enabled = false;
            Assert.AreEqual(0, Terrain.activeTerrains.Length, "a switched-off terrain is not in activeTerrains");

            ScriptableObject config = ScriptableObject.CreateInstance(Find("FieldSupplyConfig"));
            object parking = Activator.CreateInstance(Find("FieldParking"), new object[] { config });
            int indexed = (int)parking.GetType().GetProperty("IndexedTrees").GetValue(parking);

            Assert.AreEqual(3, indexed, "the parking tests must see the trees a dedicated server's switched-off terrain carries");

            UnityEngine.Object.DestroyImmediate(config);
            UnityEngine.Object.DestroyImmediate(tree);
        }
    }
}
