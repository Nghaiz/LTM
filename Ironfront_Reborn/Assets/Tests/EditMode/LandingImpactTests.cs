using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A landing is paid on the speed into the ground the body lands on, read from that ground's
    /// collider: a jump down a hill no longer costs health (v4.5.0 playtest, 2026-10-07).
    /// </summary>
    /// <remarks>
    /// <b><c>Physics.SyncTransforms</c> before every cast</b>, as in <c>GroundSnapTests</c>: in
    /// EditMode nothing runs the physics step, so a collider made this frame would not be hit.
    /// </remarks>
    public sealed class LandingImpactTests
    {
        private const int PlayerLayer = 9;

        private static float Gravity => -MovementCore.Gravity;

        // Well off every map, so whatever scene the Editor has open cannot be the ground.
        private static readonly Vector3 Far = new Vector3(-20000f, 0f, -20000f);

        private GameObject _world;

        [SetUp]
        public void SetUp() => _world = new GameObject("landing-impact-fixture");

        [TearDown]
        public void TearDown()
        {
            if (_world != null) Object.DestroyImmediate(_world);
        }

        [Test]
        public void OnFlatGroundTheFallIsPaidInFull()
        {
            Ground(tiltDegrees: 0f, layer: 0);

            Assert.AreEqual(12f, LandingImpact.OnGroundBelow(Far + new Vector3(0f, 0.9f, 0f), 12f, 6.5f, 0f), 0.01f);
        }

        [Test]
        public void AJumpDownAHillsideLandsUnhurt()
        {
            // Falls away toward +X; the jumper runs that way at 6.5 m/s and hits it at 12 m/s, which
            // read as a 6 m drop and 18 damage.
            Ground(tiltDegrees: 30f, layer: 0);

            float impact = LandingImpact.OnGroundBelow(Far + new Vector3(0f, 0.9f, 0f), 12f, 6.5f, 0f);

            Assert.Less(impact, 8f);
            Assert.AreEqual(0f, FallDamage.ForImpact(impact, Gravity));
            Assert.Greater(FallDamage.ForImpact(12f, Gravity), 15f);
        }

        [Test]
        public void AnotherBodyUnderneathIsNotTheGround()
        {
            Ground(tiltDegrees: 30f, layer: PlayerLayer);

            Assert.AreEqual(12f, LandingImpact.OnGroundBelow(Far + new Vector3(0f, 0.9f, 0f), 12f, 6.5f, 0f), 0.01f);
        }

        [Test]
        public void WithNoGroundUnderneathTheFallIsPaidInFull()
        {
            Assert.AreEqual(12f, LandingImpact.OnGroundBelow(Far + new Vector3(0f, 500f, 0f), 12f, 6.5f, 0f), 0.01f);
            Assert.AreEqual(0f, LandingImpact.OnGroundBelow(Far + new Vector3(0f, 500f, 0f), 0f, 6.5f, 0f));
        }

        private void Ground(float tiltDegrees, int layer)
        {
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.transform.SetParent(_world.transform, false);
            slab.transform.localScale = new Vector3(40f, 1f, 40f);
            slab.transform.position = Far + new Vector3(0f, -0.5f, 0f);
            // A positive tilt about -Z lowers the +X side, so the slope's normal leans toward +X.
            slab.transform.rotation = Quaternion.Euler(0f, 0f, -tiltDegrees);
            slab.layer = layer;
            Physics.SyncTransforms();
        }
    }
}
