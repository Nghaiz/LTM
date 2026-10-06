using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Forest Lake moors a boat at a random place along its shore each match.
    /// </summary>
    /// <remarks>
    /// Owner request 2026-10-07: "a boat placed at random each match, round the lake's edge or the
    /// edge of the island in the middle of it". <c>FieldSupplyDirector</c> places it from the map's
    /// field supply config; with no boat listed, or a count of zero, nothing is moored and nothing
    /// says so.
    /// </remarks>
    public sealed class ForestLakeShoreBoatTests
    {
        private static Type TypeNamed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false))
            .First(type => type != null);

        private static object Field(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.GetType().Name}.{name}");
            return field.GetValue(owner);
        }

        [Test]
        public void TheMapMoorsOneBoatAlongItsShore()
        {
            var config = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/Resources/FieldSupply/ForestLake.asset");

            Assert.AreEqual(1, (int)Field(config, "shoreVehicleCount"));
            Assert.Greater((float)Field(config, "shoreReach"), 0f);
            object boat = ((IEnumerable)Field(config, "shoreVehicles")).Cast<object>().Single();
            Assert.Greater((float)Field(boat, "weight"), 0f);
            var prefab = (GameObject)Field(boat, "prefab");
            Assert.IsNotNull(prefab.GetComponent(TypeNamed("Boat")), prefab.name + " is not a boat");
        }
    }
}
