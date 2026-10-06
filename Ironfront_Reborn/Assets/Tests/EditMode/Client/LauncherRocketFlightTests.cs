using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The BEU-AW1's rocket flies dead straight for its first stretch and only then drops; every
    /// other projectile still drops from the muzzle.
    /// </summary>
    /// <remarks>
    /// Owner, v4.3.0 playtest: the launcher "must fly dead straight to show how powerful it is,
    /// and only drop once it has flown very far". <c>Projectile</c> lives in Assembly-CSharp, which
    /// no asmdef can reference, so its static step and the prefab's field are reached by name.
    /// </remarks>
    public sealed class LauncherRocketFlightTests
    {
        private const float Dt = 1f / 60f;

        private static Type ProjectileType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Projectile", false))
            .First(type => type != null);

        [Test]
        public void InsideItsStraightStretchARocketDoesNotDrop()
        {
            Vector3 velocity = new Vector3(0f, 0f, 190f);
            Vector3 delta = Step(ref velocity, travelled: 300f, straight: 500f);

            Assert.AreEqual(0f, delta.y, 1e-6f);
            Assert.AreEqual(0f, velocity.y, 1e-6f);
            Assert.AreEqual(190f * Dt, delta.z, 1e-4f);
        }

        [Test]
        public void PastItsStraightStretchItFallsLikeAnythingElse()
        {
            Vector3 velocity = new Vector3(0f, 0f, 190f);
            Vector3 delta = Step(ref velocity, travelled: 501f, straight: 500f);

            Assert.AreEqual(Physics.gravity.y * 0.5f * Dt * Dt, delta.y, 1e-6f);
            Assert.AreEqual(Physics.gravity.y * Dt, velocity.y, 1e-5f);
        }

        [Test]
        public void WithNoStraightStretchEveryStepFalls()
        {
            // The default, and the original game: a bullet one step out of the muzzle drops.
            Vector3 velocity = new Vector3(0f, 0f, 300f);
            Step(ref velocity, travelled: 300f * Dt, straight: 0f);

            Assert.Less(velocity.y, 0f);
        }

        [Test]
        public void TheLauncherRocketIsAuthoredToFlyFarBeforeDropping()
        {
            var rocket = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/rocket.prefab");
            Assert.IsNotNull(rocket, "the BEU-AW1's rocket prefab moved");

            Component body = rocket.GetComponent(ProjectileType);
            object configuration = ProjectileType.GetField("configuration").GetValue(body);
            var straight = (float)configuration.GetType().GetField("straightDistance").GetValue(configuration);

            Assert.GreaterOrEqual(straight, 400f, "the launcher rocket drops before it has flown very far");
        }

        [TestCase("AK Tracer", true)]
        [TestCase("Sniper Rifle Tracer", true)]
        [TestCase("Shotgun Pellet", true)]
        [TestCase("rocket", false)]
        [TestCase("Gatling Tracer", false)]
        [TestCase("Tank Projectile", false)]
        [TestCase("javelin missile", false)]
        public void OnlyPlainRoundsAreTheRoundsTheServerSweeps(string prefab, bool hitscan)
        {
            // The server sweeps a plain round along its gun's ballistic arc (Projectile.Round,
            // RoundBallistics); everything the server actually flies keeps its own flight step.
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefab/{prefab}.prefab");
            Assert.IsNotNull(asset, prefab);
            Component round = asset.GetComponent(ProjectileType);
            MethodInfo isHitscan = ProjectileType.GetMethod("IsHitscanRound", BindingFlags.Public | BindingFlags.Static);

            Assert.AreEqual(hitscan, (bool)isHitscan.Invoke(null, new object[] { round }), prefab);
        }

        private static Vector3 Step(ref Vector3 velocity, float travelled, float straight)
        {
            MethodInfo step = ProjectileType.GetMethod("FlightStep", BindingFlags.Public | BindingFlags.Static);
            object[] args = { velocity, travelled, straight, Dt };
            var delta = (Vector3)step.Invoke(null, args);
            velocity = (Vector3)args[0];
            return delta;
        }
    }
}
