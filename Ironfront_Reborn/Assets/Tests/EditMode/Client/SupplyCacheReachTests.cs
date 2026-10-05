using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A supply cache serves a soldier standing anywhere against its crates, across the ground, on
    /// its own floor.
    /// </summary>
    /// <remarks>
    /// v4.3.0 playtest: "sometimes standing by the ammo or health crate refills nothing". The 6 m
    /// the scenes author was measured in three dimensions from the module's centre, and a module's
    /// crates reach 2.9 m from it. <c>SupplyCache</c> lives in Assembly-CSharp, so it is reached by
    /// name; its <c>Reaches</c> reads only its transform and range.
    /// </remarks>
    public sealed class SupplyCacheReachTests
    {
        private GameObject _cache;
        private Component _component;

        private static Type CacheType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("SupplyCache", false))
            .First(type => type != null);

        [SetUp]
        public void SetUp()
        {
            _cache = new GameObject("cache");
            _cache.transform.position = new Vector3(100f, 50f, 100f);
            _component = _cache.AddComponent(CacheType);
            CacheType.GetField("range").SetValue(_component, 6f);
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_cache);

        [TestCase(0f, 0f, 0f, true)]
        [TestCase(6.5f, 0f, 0f, true)]      // the friend's 6.5 m, refused before
        [TestCase(5f, 0f, 5f, true)]        // 7.1 m on the diagonal
        [TestCase(0f, 2.5f, 7.5f, true)]    // a step up, against the far crate
        [TestCase(9f, 0f, 0f, false)]
        [TestCase(0f, 4f, 0f, false)]       // a storey above is somebody else's floor
        public void ItReachesWhoStandsAgainstItsCrates(float dx, float dy, float dz, bool reaches)
        {
            var at = new Vector3(100f + dx, 50f + dy, 100f + dz);
            var result = (bool)CacheType.GetMethod("Reaches").Invoke(_component, new object[] { at });

            Assert.AreEqual(reaches, result, $"offset ({dx}, {dy}, {dz})");
        }
    }
}
