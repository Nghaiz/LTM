using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The launcher's guided missile catches a target that turns hard, and finds a way past a wall
    /// in front of the tube.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owner, v4.3.0 playtest: the missile "keeps missing, far behind the target, or hits without
    /// doing any damage"; it must be smart about its path, avoid obstacles as it leaves the
    /// launcher, and chase the target to the end. The flight is driven here through the same
    /// static steps <c>JavelinMissile.Update</c> runs (<c>LeadHeading</c>, <c>Turn</c>,
    /// <c>ClearHeading</c>) at 60 Hz, with the missile's authored numbers, against colliders in an
    /// empty scene. <c>JavelinMissile</c> lives in Assembly-CSharp, so it is reached by name.
    /// </para>
    /// </remarks>
    public sealed class GuidedMissileFlightTests
    {
        private const float Dt = 1f / 60f;
        private const float Cruise = 100f;      // javelin missile.prefab configuration.speed
        private const float TurnAcceleration = 400f;
        private const float Boost = 150f;
        private const float MaxLead = 2f;
        private const float ProximityFuse = 2f;

        private static Type MissileType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("JavelinMissile", false))
            .First(type => type != null);

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void ItCatchesACarThatTurnsHardInFrontOfIt()
        {
            Vector3 missile = new Vector3(0f, 1f, 0f);
            Vector3 velocity = new Vector3(0f, 0f, Cruise);
            Vector3 target = new Vector3(0f, 1f, 250f);
            Vector3 targetVelocity = new Vector3(0f, 0f, 15f);

            float closest = float.MaxValue;
            for (float t = 0f; t < 10f; t += Dt)
            {
                if (t > 1.5f) targetVelocity = new Vector3(15f, 0f, 0f);   // a hard right turn
                target += targetVelocity * Dt;

                Vector3 desired = LeadHeading(missile, Cruise, target, targetVelocity);
                velocity = Turn(velocity, desired, Cruise);
                missile += velocity * Dt;

                closest = Mathf.Min(closest, Vector3.Distance(missile, target));
                if (closest <= ProximityFuse) break;
            }

            Assert.LessOrEqual(closest, ProximityFuse, "the missile never came within its fuse of a turning target");
        }

        [Test]
        public void ItCatchesASoldierWhoDodgesSidewaysAtTheLastMoment()
        {
            Vector3 missile = new Vector3(0f, 2f, 0f);
            Vector3 velocity = new Vector3(0f, 0f, Cruise);
            Vector3 target = new Vector3(0f, 1.1f, 150f);
            Vector3 targetVelocity = Vector3.zero;

            float closest = float.MaxValue;
            for (float t = 0f; t < 6f; t += Dt)
            {
                // Sprinting left, then cutting right, inside the last half second.
                if (t > 1.0f) targetVelocity = new Vector3(-7f, 0f, 0f);
                if (t > 1.3f) targetVelocity = new Vector3(7f, 0f, 0f);
                target += targetVelocity * Dt;

                Vector3 desired = LeadHeading(missile, Cruise, target, targetVelocity);
                velocity = Turn(velocity, desired, Cruise);
                missile += velocity * Dt;

                closest = Mathf.Min(closest, Vector3.Distance(missile, target));
                if (closest <= ProximityFuse) break;
            }

            Assert.LessOrEqual(closest, ProximityFuse, "a sidestep shook the missile off");
        }

        [Test]
        public void ItLeavesTheTubeOverAWallInsteadOfIntoIt()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.layer = 0;
            wall.transform.position = new Vector3(0f, 5f, 25f);
            wall.transform.localScale = new Vector3(30f, 10f, 1f);
            Physics.SyncTransforms();

            Vector3 missile = new Vector3(0f, 1f, 0f);
            Vector3 velocity = new Vector3(0f, 0f, 20f);       // ejected; the motor lights now
            Vector3 target = new Vector3(0f, 1.1f, 200f);

            float closest = float.MaxValue;
            float speed = velocity.magnitude;
            for (float t = 0f; t < 6f; t += Dt)
            {
                speed = Mathf.MoveTowards(speed, Cruise, Boost * Dt);
                Vector3 desired = LeadHeading(missile, speed, target, Vector3.zero);
                if (t < 2f)
                {
                    float reach = Mathf.Min(40f, Vector3.Distance(missile, target) - 1.2f);
                    desired = ClearHeading(missile, desired, reach, 0.6f);
                }
                velocity = Turn(velocity, desired, speed);
                Vector3 next = missile + velocity * Dt;

                Assert.IsFalse(Physics.Linecast(missile, next), $"the missile flew into the wall at t={t:F2}s");
                missile = next;

                closest = Mathf.Min(closest, Vector3.Distance(missile, target));
                if (closest <= ProximityFuse) break;
            }

            Assert.LessOrEqual(closest, ProximityFuse, "the missile cleared the wall but never reached the target");
        }

        [Test]
        public void WithNothingInTheWayItFliesStraightAtTheTarget()
        {
            Vector3 desired = Vector3.forward;
            Vector3 chosen = ClearHeading(Vector3.zero, desired, 40f, 0.6f);

            Assert.AreEqual(desired, chosen);
        }

        private static Vector3 LeadHeading(Vector3 position, float speed, Vector3 aim, Vector3 aimVelocity)
            => (Vector3)Static("LeadHeading").Invoke(null, new object[] { position, speed, aim, aimVelocity, MaxLead, Vector3.forward });

        private static Vector3 Turn(Vector3 velocity, Vector3 desired, float speed)
            => (Vector3)Static("Turn").Invoke(null, new object[] { velocity, desired, speed, TurnAcceleration, Dt, Vector3.forward });

        private static Vector3 ClearHeading(Vector3 position, Vector3 desired, float reach, float radius)
            => (Vector3)Static("ClearHeading").Invoke(null, new object[] { position, desired, reach, radius, null, null });

        private static MethodInfo Static(string name)
            => MissileType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
    }
}
