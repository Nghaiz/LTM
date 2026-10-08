using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The client's vehicle-prefab directory answers for the map loaded now, not for the first map
    /// of the session. v3.1.0 release test, 2026-10-01: a client that played Forest Lake and
    /// Dustbowl before Island logged "S_VEHICLE_SPAWN named a networkTypeId no vehicle prefab in this
    /// scene declares" and drew none of Island's boats, the one vehicle only Island fields.
    /// </summary>
    /// <remarks>
    /// <c>SceneVehiclePrefabDirectory</c>, <c>VehicleSpawner</c> and <c>Vehicle</c> live in
    /// Assembly-CSharp, which no asmdef can reference, so they are reached by name. Each "map" is a
    /// new single scene, which is what loading the next map does. Network ids 251 and 252 belong to
    /// no real prefab.
    /// </remarks>
    public sealed class VehiclePrefabDirectoryTests
    {
        private const byte FirstMapVehicle = 251;
        private const byte SecondMapVehicle = 252;

        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false))
            .First(type => type != null);

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void AVehicleOnlyTheNextMapFieldsResolvesOnThatMap()
        {
            var directory = (IVehiclePrefabDirectory)Activator.CreateInstance(
                Find("Ironfront.Net.Unity.Bindings.SceneVehiclePrefabDirectory"), nonPublic: true);

            LoadMapFielding(FirstMapVehicle);
            Assert.IsTrue(directory.TryGetPrefab(FirstMapVehicle, out _),
                "the first map's own vehicle did not resolve");

            LoadMapFielding(SecondMapVehicle);
            Assert.IsTrue(directory.TryGetPrefab(SecondMapVehicle, out GameObject prefab),
                "the second map's vehicle was unknown: the directory kept the first map's scan");
            Assert.AreEqual("vehicle " + SecondMapVehicle, prefab.name);
        }

        /// <summary>
        /// Every vehicle a map's field supply config can scatter resolves on that map, though no pad
        /// in the client's scene fields it.
        /// </summary>
        /// <remarks>
        /// v4.5.0 playtest, 2026-10-07: the server moored Forest Lake's shore boat, the one vehicle
        /// there no authored pad fields, and every online client logged "S_VEHICLE_SPAWN named a
        /// networkTypeId no vehicle prefab in this scene declares" and drew no boat. Run on an empty
        /// scene, so only the config can answer.
        /// </remarks>
        [Test]
        public void EveryVehicleAMapScattersResolvesOnThatMap()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            MethodInfo collect = Find("Ironfront.Net.Unity.Bindings.SceneVehiclePrefabDirectory")
                .GetMethod("CollectMapVehicles", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(collect, "SceneVehiclePrefabDirectory.CollectMapVehicles");
            Type vehicleType = Find("Vehicle");
            MethodInfo collectPrefabs = Find("FieldSupplyConfig").GetMethod("CollectVehiclePrefabs");

            string[] configs = AssetDatabase.FindAssets("t:FieldSupplyConfig", new[] { "Assets/Resources/FieldSupply" });
            Assert.IsNotEmpty(configs, "no field supply config to check");

            foreach (string guid in configs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string map = Path.GetFileNameWithoutExtension(path);
                var prefabs = new List<GameObject>();
                collectPrefabs.Invoke(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path), new object[] { prefabs });
                Assert.IsNotEmpty(prefabs, map + " scatters no vehicle");

                var known = new Dictionary<byte, GameObject>();
                collect.Invoke(null, new object[] { map, known });

                foreach (GameObject prefab in prefabs)
                {
                    var networkId = (byte)vehicleType.GetProperty("NetworkId").GetValue(prefab.GetComponent(vehicleType));
                    Assert.Greater(networkId, 0, prefab.name + " has no network id");
                    Assert.IsTrue(known.ContainsKey(networkId),
                        $"{map} scatters {prefab.name} (network id {networkId}), which a client on that map cannot resolve");
                }
            }
        }

        /// <summary>A fresh scene with one spawner whose prefab carries <paramref name="networkId"/>.</summary>
        private static void LoadMapFielding(byte networkId)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Type vehicleType = Find("Vehicle");
            var vehicle = new GameObject("vehicle " + networkId);
            Component body = vehicle.AddComponent(vehicleType);
            vehicleType.GetField("networkId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(body, networkId);

            Type spawnerType = Find("VehicleSpawner");
            Component spawner = new GameObject("spawner " + networkId).AddComponent(spawnerType);
            spawnerType.GetField("prefab").SetValue(spawner, vehicle);
        }
    }
}
