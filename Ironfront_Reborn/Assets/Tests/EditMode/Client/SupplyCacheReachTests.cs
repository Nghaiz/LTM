using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A supply cache serves a soldier standing against its crates, within 5 m of its centre across
    /// the ground, on its own floor, and nobody further out.
    /// </summary>
    /// <remarks>
    /// v4.3.0 playtest: "sometimes standing by the ammo or health crate refills nothing" took the
    /// reach to 8 m (#553); the owner then ruled a soldier must stand close, 5 m (2026-10-06). A
    /// module's crates reach 2.9 m from its centre, so 5 m still covers the far crate.
    /// <c>SupplyCache</c> lives in Assembly-CSharp, so it is reached by name; its <c>Reaches</c>
    /// reads only its transform.
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
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_cache);

        [TestCase(0f, 0f, 0f, true)]
        [TestCase(4.9f, 0f, 0f, true)]
        [TestCase(3.4f, 0f, 3.4f, true)]    // 4.8 m on the diagonal
        [TestCase(0f, 2.5f, 3.5f, true)]    // a step up, against the far crate
        [TestCase(5.2f, 0f, 0f, false)]     // the owner's ruling: 5 m, no further
        [TestCase(4f, 0f, 4f, false)]       // 5.7 m on the diagonal
        [TestCase(6.5f, 0f, 0f, false)]     // the 8 m reach of #553 served this
        [TestCase(0f, 4f, 0f, false)]       // a storey above is somebody else's floor
        public void ItReachesWhoStandsAgainstItsCrates(float dx, float dy, float dz, bool reaches)
        {
            var at = new Vector3(100f + dx, 50f + dy, 100f + dz);
            var result = (bool)CacheType.GetMethod("Reaches").Invoke(_component, new object[] { at });

            Assert.AreEqual(reaches, result, $"offset ({dx}, {dy}, {dz})");
        }
    }
}
