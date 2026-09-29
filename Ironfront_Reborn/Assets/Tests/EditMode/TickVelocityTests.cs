using System.Collections.Generic;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins <c>NetMovementAgent.TickVelocity</c>: the speed the first-person controller's
    /// footsteps and weapon bob read on a networked client, in place of
    /// <c>CharacterController.velocity</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fault it replaces.</b> Unity computes <c>CharacterController.velocity</c> as the last
    /// move over <c>Time.deltaTime</c>. The netcode moves the capsule from <c>Update</c> at 30 Hz,
    /// so that property read one tick's displacement over one render frame: 71.6 m/s for a
    /// 3.5 m/s walk at 600 fps, measured 2026-09-29 in the Editor. The arms bobbed and the
    /// footsteps fired several times too fast (owner: "cơ thể chuyển động điên cuồng").
    /// </para>
    /// <para>
    /// Each test would go red on the fault it names: a tick velocity divided by anything but the
    /// tick, a stance change counted as movement, and a held body that keeps walking.
    /// </para>
    /// </remarks>
    public sealed class TickVelocityTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null) Object.DestroyImmediate(go);
            }

            _spawned.Clear();
        }

        /// <summary>A body standing on a floor, far from anything else in the test scene.</summary>
        private NetMovementAgent NewBodyOnFloor()
        {
            var floor = new GameObject("floor");
            _spawned.Add(floor);
            floor.AddComponent<BoxCollider>().size = new Vector3(200f, 1f, 200f);
            floor.transform.position = new Vector3(3000f, -0.5f, 3000f);

            // The player prefab's capsule, standing just clear of the floor. The component's own
            // default height is 2.0, which the first tick would shrink to the standing 1.8 and
            // shift the transform for -- a stance change this test does not mean to include.
            var go = new GameObject("body");
            _spawned.Add(go);
            go.transform.position = new Vector3(3000f, 0.99f, 3000f);
            CharacterController capsule = go.AddComponent<CharacterController>();
            capsule.height = MovementCore.StandHeight;
            capsule.radius = 0.3f;

            NetMovementAgent agent = go.AddComponent<NetMovementAgent>();
            agent.State = MoveState.AtRest(MovementSimulation.ToCore(go.transform.position));
            return agent;
        }

        private static MoveInput Walk(bool crouch = false)
            => new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: crouch);

        [Test]
        public void ATickReportsItsDisplacementOverTheTickNotOverARenderFrame()
        {
            NetMovementAgent agent = NewBodyOnFloor();
            Vector3 before = agent.transform.position;

            agent.Tick(Walk(), MovementSimulation.FixedDeltaTime);

            Vector3 expected = (agent.transform.position - before) / MovementSimulation.FixedDeltaTime;
            Assert.That(Vector3.Distance(expected, agent.TickVelocity), Is.LessThan(1e-3f),
                $"TickVelocity {agent.TickVelocity} is not the tick's displacement over the tick "
                + $"({expected}). Divided by a render frame it reads the frame rate, not the walk.");

            float horizontal = new Vector2(agent.TickVelocity.x, agent.TickVelocity.z).magnitude;
            Assert.AreEqual(MovementSimulation.WalkSpeed, horizontal, 0.01f,
                "An unobstructed walking tick must report the walk speed.");
        }

        [Test]
        public void AStanceChangeIsNotCountedAsMovement()
        {
            NetMovementAgent agent = NewBodyOnFloor();
            agent.Tick(Walk(), MovementSimulation.FixedDeltaTime);

            // The crouching tick shrinks the capsule and shifts the transform to keep the feet
            // down. That shift is a pose, not a step, exactly as the original's ForceEndCrouch
            // teleport never reached CharacterController.velocity.
            agent.Tick(Walk(crouch: true), MovementSimulation.FixedDeltaTime);

            Assert.AreEqual(0f, agent.TickVelocity.y, 1.5f,
                $"A crouching tick reported {agent.TickVelocity.y:F2} m/s vertically. The stance "
                + "shift is 0.65 m in one tick, which would read as ~20 m/s if it were counted.");
        }

        [Test]
        public void HoldingTheBodyStillReportsZero()
        {
            NetMovementAgent agent = NewBodyOnFloor();
            agent.Tick(Walk(), MovementSimulation.FixedDeltaTime);
            Assume.That(agent.TickVelocity.magnitude, Is.GreaterThan(1f));

            agent.HoldStill();

            Assert.AreEqual(Vector3.zero, agent.TickVelocity,
                "A body the clock holds still (dead, seated, parked) kept its last walking "
                + "velocity, so its footsteps and weapon bob would keep going.");
        }
    }
}
