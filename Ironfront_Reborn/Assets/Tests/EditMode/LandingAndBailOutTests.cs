using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A player who leaves a helicopter in the air is left in the air, and a landing hurts by how
    /// fast it was: nothing from a jump, health from a high drop, death from a very high one.
    /// Owner request 2026-10-06.
    /// </summary>
    public sealed class LandingAndBailOutTests
    {
        private const ushort ActorId = 243;

        private GameObject _ground;
        private GameObject _body;
        private float _savedWater;

        [SetUp]
        public void SetUp()
        {
            _savedWater = MovementCore.WaterHeight;
            MovementCore.WaterHeight = float.NegativeInfinity;

            // A floor whose top is y = 0.
            _ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _ground.name = "landing floor";
            _ground.transform.position = new Vector3(0f, -0.5f, 0f);
            _ground.transform.localScale = new Vector3(200f, 1f, 200f);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            MovementCore.WaterHeight = _savedWater;
            if (_body != null)
            {
                ServerActorRegistry.Instance.Unregister(_body.GetComponent<NetServerActor>());
                Object.DestroyImmediate(_body);
            }
            Object.DestroyImmediate(_ground);
        }

        [Test]
        public void LeavingAVehicleOnTheGroundStepsOntoTheGroundBesideIt()
        {
            CharacterController capsule = FarCapsule();
            var hull = new Bounds(new Vector3(0f, 1.5f, 0f), new Vector3(3f, 3f, 5f));

            Assert.IsTrue(ServerPlayer.TryFindExitSpot(new Vector3(2f, 2f, 0f), hull, capsule, out Vector3 root));
            Assert.AreEqual(0f, root.y + capsule.center.y - capsule.height * 0.5f, 0.1f, "not stood on the ground");
        }

        [Test]
        public void LeavingAHelicopterHighUpLeavesThePlayerInTheAir()
        {
            // The old ray reached 40 m under the hull and put this player on the ground, unhurt.
            CharacterController capsule = FarCapsule();
            var hull = new Bounds(new Vector3(0f, 30f, 0f), new Vector3(4f, 3f, 10f));
            var preferred = new Vector3(2f, 29f, 0f);

            Assert.IsFalse(ServerPlayer.TryFindExitSpot(preferred, hull, capsule, out Vector3 root));
            Assert.AreEqual(preferred, root);
        }

        [Test]
        public void AHoverLowerThanTheSafeDropStillStepsDown()
        {
            CharacterController capsule = FarCapsule();
            float bottom = FallDamage.SafeDropMetres - 0.5f;
            var hull = new Bounds(new Vector3(0f, bottom + 1.5f, 0f), new Vector3(4f, 3f, 10f));

            Assert.IsTrue(ServerPlayer.TryFindExitSpot(new Vector3(2f, bottom, 0f), hull, capsule, out _));
        }

        [Test]
        public void AOneMetreDropLandsUnhurt()
        {
            ServerPlayer player = PlayerFallingFrom(1f, out NetServerActor body);

            Land(player);

            Assert.AreEqual(100f, body.Health, 0.001f);
            Assert.IsTrue(body.IsAlive);
        }

        [Test]
        public void AHighDropCostsHealthByTheHeightFallen()
        {
            const float height = 7.5f;   // in the game's 1.2 g: half a soldier's health
            ServerPlayer player = PlayerFallingFrom(height, out NetServerActor body);

            Land(player);

            float expected = 100f - FallDamage.ForImpact(FallTracker.ImpactSpeed(height, 0f));
            Assert.AreEqual(expected, body.Health, 1f);
            Assert.IsTrue(body.IsAlive);
        }

        [Test]
        public void ABailOutFromFortyMetresKills()
        {
            ServerPlayer player = PlayerFallingFrom(40f, out NetServerActor body);

            Land(player);

            Assert.IsFalse(body.IsAlive, "a 40 m fall left the player alive");
            Assert.AreEqual(0f, body.Health, 0.001f);
        }

        [Test]
        public void FallingWithoutLandingCostsNothing()
        {
            ServerPlayer player = PlayerFallingFrom(40f, out NetServerActor body);

            player.Tick(1f / 30f);

            Assert.AreEqual(100f, body.Health, 0.001f);
        }

        private CharacterController FarCapsule()
        {
            _body = new GameObject("exit capsule");
            _body.transform.position = new Vector3(500f, 500f, 500f);
            CharacterController capsule = _body.AddComponent<CharacterController>();
            capsule.height = MovementCore.StandHeight;
            capsule.radius = 0.4f;
            _body.AddComponent<NetServerActor>().ActorId = ActorId;
            Physics.SyncTransforms();
            return capsule;
        }

        /// <summary>
        /// A player in the air <paramref name="height"/> metres above the floor, ticked once there
        /// so the server has seen the fall begin.
        /// </summary>
        private ServerPlayer PlayerFallingFrom(float height, out NetServerActor body)
        {
            _body = new GameObject("falling player");
            CharacterController capsule = _body.AddComponent<CharacterController>();
            capsule.height = MovementCore.StandHeight;
            capsule.radius = 0.4f;
            body = _body.AddComponent<NetServerActor>();
            body.ActorId = ActorId;
            NetMovementAgent agent = body.AttachMovementAgent();
            agent.Teleport(new Vector3(0f, StandingCentre + height, 0f));
            ServerActorRegistry.Instance.Register(body);
            Physics.SyncTransforms();
            Assume.That(agent.IsGrounded, Is.False, "the body is not in the air");

            var player = new ServerPlayer(connectionId: 5, actorId: ActorId) { Actor = body };
            player.SyncFromActor();
            player.Session.State.IsGrounded = false;
            player.Tick(1f / 30f);
            return player;
        }

        /// <summary>The fall's end: the body is on the floor, as its fall left it, and ticks there.</summary>
        private void Land(ServerPlayer player)
        {
            NetMovementAgent agent = player.Actor.Movement;
            agent.Teleport(new Vector3(0f, StandingCentre + 0.05f, 0f), resetVelocity: false);
            Physics.SyncTransforms();
            _body.GetComponent<CharacterController>().Move(Vector3.down * 0.1f);
            Assume.That(agent.IsGrounded, "the capsule did not reach the floor");

            Vector3 at = _body.transform.position;
            player.Session.State.Position = new Vec3(at.x, at.y, at.z);
            player.Session.PreviousPosition = player.Session.State.Position;
            player.Tick(1f / 30f);
        }

        private static float StandingCentre => MovementCore.StandHeight * 0.5f;
    }
}
