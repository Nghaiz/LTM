using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A remote body on its feet is drawn on the ground under it (owner report 2026-09-29,
    /// image 3: a friend stood in the air after spawning), and a body in the air is not.
    /// </summary>
    public sealed class RemoteFootingTests
    {
        private GameObject _ground;

        [SetUp]
        public void SetUp()
        {
            _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            _ground.layer = 0;
            _ground.transform.position = new Vector3(5000f, 10f, 5000f);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_ground);
        }

        [Test]
        public void FeetAControllerSkinAboveTheGroundStandOnIt()
        {
            Assert.IsTrue(RemoteActorRegistry.TryGroundUnder(5000f, 10.15f, 5000f, out float ground),
                "a standing body is drawn floating at the height its CharacterController rests at");
            Assert.AreEqual(10f, ground, 1e-3f);
        }

        [Test]
        public void FeetALittleUnderTheSurfaceComeUpOntoIt()
        {
            Assert.IsTrue(RemoteActorRegistry.TryGroundUnder(5000f, 9.8f, 5000f, out float ground),
                "interpolation that cut through a bump leaves the feet buried in it");
            Assert.AreEqual(10f, ground, 1e-3f);
        }

        [Test]
        public void ABodyNobodySeesKeepsItsLastFootingWithoutAProbe()
        {
            var footing = new System.Collections.Generic.Dictionary<ushort, float>();
            Assert.IsTrue(RemoteActorRegistry.TryFooting(footing, 7, seen: true, 5000f, 10.15f, 5000f, out _),
                "Setup: the seen body found no ground");

            // The ground goes, so only a probe could notice; walking on, the body rose 2 m.
            _ground.transform.position = new Vector3(5000f, 500f, 5000f);
            Physics.SyncTransforms();
            Assert.IsTrue(RemoteActorRegistry.TryFooting(footing, 7, seen: false, 5000f, 12.15f, 5000f, out float ground),
                "a body out of view was probed, and lost its footing on ground a probe could not find");
            Assert.AreEqual(12f, ground, 1e-3f, "the unseen body did not keep its rise above its feet");

            Assert.IsFalse(RemoteActorRegistry.TryFooting(footing, 7, seen: true, 5000f, 12.15f, 5000f, out _),
                "a body in view was not probed: it kept a footing on ground that is gone");
        }

        [Test]
        public void ABodyNobodyHasProbedYetIsProbed()
        {
            var footing = new System.Collections.Generic.Dictionary<ushort, float>();

            Assert.IsTrue(RemoteActorRegistry.TryFooting(footing, 9, seen: false, 5000f, 10.15f, 5000f, out float ground),
                "a body with no footing yet was left in the air");
            Assert.AreEqual(10f, ground, 1e-3f);
        }

        [Test]
        public void ABodyInTheAirStaysInTheAir()
        {
            float midJump = 10f + RemoteActorRegistry.FootingReachMetres + 0.3f;
            Assert.IsFalse(RemoteActorRegistry.TryGroundUnder(5000f, midJump, 5000f, out _),
                "a jump was pulled down onto the ground");
        }

        [Test]
        public void AStandingBodyIsLoweredByTheIdlePosesLiftAndAWalkingOneIsNot()
        {
            // The idle pose stands its soles 0.08 m above the body's origin; the walk cycle
            // plants a foot at it. The lift eases out over the animator's own blend.
            Assert.AreEqual(1f, RemoteActorView.NextIdleWeight(1f, moving: false, elapsed: 0.5f));
            Assert.AreEqual(0.5f, RemoteActorView.NextIdleWeight(1f, moving: true,
                elapsed: RemoteActorView.IdleBlendSeconds * 0.5f), 1e-4f,
                "the lift was dropped at once: a body starting to walk jumps down by the whole lift");
            Assert.AreEqual(0f, RemoteActorView.NextIdleWeight(1f, moving: true,
                elapsed: RemoteActorView.IdleBlendSeconds), 1e-4f,
                "a walking body is still lowered by the idle pose's lift, so its planted foot sinks");
            Assert.AreEqual(1f, RemoteActorView.NextIdleWeight(0f, moving: false,
                elapsed: RemoteActorView.IdleBlendSeconds), 1e-4f);
        }

        [Test]
        public void OnlyABodyOnItsFeetIsStoodOnTheGround()
        {
            Assert.IsTrue(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsAlive, -10f), isHuman: true),
                "a standing player (MovementCore pins a grounded body at -10 m/s) was not stood on the ground");
            Assert.IsFalse(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsAlive, 5f), isHuman: true),
                "a player taking off on a jump was held to the ground");
            Assert.IsTrue(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsAlive, 2f), isHuman: false),
                "a bot walking uphill was left floating: bots do not jump");

            Assert.IsFalse(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsAlive | ActorStateFlags.IsSeated, 0f), true));
            Assert.IsFalse(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsAlive | ActorStateFlags.IsInWater, 0f), true));
            Assert.IsFalse(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsAlive | ActorStateFlags.IsRagdoll, 0f), false));
            Assert.IsFalse(RemoteActorRegistry.StandsOnGround(Entry(ActorStateFlags.IsRagdoll, 0f), false),
                "a corpse was moved onto the ground: its ragdoll owns where it lies");
        }

        private static ActorSnapshotEntry Entry(ActorStateFlags flags, float velocityY) => new ActorSnapshotEntry
        {
            ActorId    = 3,
            StateFlags = flags,
            VelY       = Quantize.PackVel(velocityY),
        };
    }
}
