using Ironfront.Net.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The spawn ground-snap, and the three ways it used to lie. Ledger <b>X-81</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every one of these fails against the old implementation</b>
    /// (<c>Physics.Raycast(ray, out hit)</c> — no mask, no distance limit, silent on a miss), and
    /// that is the point: each fault produced a perfectly ordinary-looking <c>Vector3</c>, so
    /// nothing downstream could tell a good snap from a bad one. The measurement that found them
    /// came from the server's own "placed at spawn point" lines across every recorded lane-B
    /// run: spawn point 0's modal height is 103.4-103.5 m against three placements at 23.3-23.9.
    /// </para>
    /// <para>
    /// <b><c>Physics.SyncTransforms</c> before every cast.</b> In EditMode nothing runs the
    /// physics step, so a collider created this frame is not in the physics scene yet and every
    /// raycast here would miss — a suite that passes for a reason that has nothing to do with
    /// the code under test.
    /// </para>
    /// </remarks>
    public sealed class GroundSnapTests
    {
        private const int PlayerLayer = 9;
        private const int WaterLayer = 4;

        private GameObject _world;

        [SetUp]
        public void SetUp() => _world = new GameObject("ground-snap-fixture");

        [TearDown]
        public void TearDown()
        {
            if (_world != null) Object.DestroyImmediate(_world);
        }

        [Test]
        public void SnapsToGroundUnderThePoint()
        {
            Floor(atY: 0f, layer: 0);

            Assert.IsTrue(GroundSnap.TrySnap(new Vector3(0f, 1f, 0f), out Vector3 grounded));
            Assert.AreEqual(0f, grounded.y, 0.05f);
        }

        /// <summary>
        /// A player standing on the spot is not the ground.
        /// </summary>
        /// <remarks>
        /// The ray had no mask, so the FIRST thing under the point won — and on a busy spawn
        /// that is another player, a vehicle, or a ragdoll. The next body was then placed on top
        /// of a body that is about to walk away.
        /// </remarks>
        [Test]
        public void APlayerStandingThereIsNotGround()
        {
            Floor(atY: 0f, layer: 0);
            Floor(atY: 1.5f, layer: PlayerLayer);   // closer to the ray's origin than the floor

            Assert.IsTrue(GroundSnap.TrySnap(new Vector3(0f, 2f, 0f), out Vector3 grounded));
            Assert.AreEqual(0f, grounded.y, 0.05f, "snapped onto the body on the Player layer");
        }

        [Test]
        public void WaterIsNotGround()
        {
            Floor(atY: 0f, layer: 0);
            Floor(atY: 1.5f, layer: WaterLayer);

            Assert.IsTrue(GroundSnap.TrySnap(new Vector3(0f, 2f, 0f), out Vector3 grounded));
            Assert.AreEqual(0f, grounded.y, 0.05f, "snapped onto water");
        }

        /// <summary>
        /// A trigger volume is not ground, even though this project makes queries hit triggers.
        /// </summary>
        /// <remarks>
        /// <c>m_QueriesHitTriggers</c> is 1 in <c>DynamicsManager.asset</c>, so the DEFAULT
        /// behaviour of an unqualified raycast is to hit capture-point volumes, damage zones and
        /// water triggers. Passing <c>QueryTriggerInteraction.Ignore</c> is what makes this pass,
        /// and it is easy to drop in a refactor because nothing else would visibly change.
        /// </remarks>
        [Test]
        public void ATriggerVolumeIsNotGround()
        {
            Floor(atY: 0f, layer: 0);
            GameObject volume = Floor(atY: 1.5f, layer: 0);
            volume.GetComponent<BoxCollider>().isTrigger = true;

            Physics.SyncTransforms();
            Assert.IsTrue(GroundSnap.TrySnap(new Vector3(0f, 2f, 0f), out Vector3 grounded));
            Assert.AreEqual(0f, grounded.y, 0.05f, "snapped onto a trigger volume");
        }

        /// <summary>
        /// Ground far below the point is a MISS, not an eighty-metre placement.
        /// </summary>
        /// <remarks>
        /// This is the measured defect itself. The old cast used <c>Mathf.Infinity</c>, so a
        /// point whose own terrain was missing snapped to whatever the ray eventually met and
        /// reported success — three recorded placements eighty metres below a spawn whose modal
        /// height is 103.4 m.
        /// </remarks>
        [Test]
        public void GroundFarBelowIsAMissRatherThanAPlacement()
        {
            Floor(atY: -80f, layer: 0);

            Assert.IsFalse(
                GroundSnap.TrySnap(new Vector3(0f, 0f, 0f), out Vector3 grounded),
                "reported a successful snap onto ground 80 m below");
            Assert.AreEqual(0f, grounded.y, 0.001f, "a miss must hand back the caller's own point");
        }

        [Test]
        public void NothingUnderneathIsAMiss()
        {
            Assert.IsFalse(GroundSnap.TrySnap(new Vector3(0f, 5f, 0f), out _));
        }

        /// <summary>
        /// The snap window reaches DOWN as far as it claims to.
        /// </summary>
        /// <remarks>
        /// Bounding the cast is only safe if the bound is generous enough for real level
        /// geometry; a limit that refused an ordinary two-metre drop would turn one silent defect
        /// into a loud one on every spawn in the game.
        /// </remarks>
        [Test]
        public void GroundInsideTheWindowStillSnaps()
        {
            Floor(atY: -6f, layer: 0);

            Assert.IsTrue(GroundSnap.TrySnap(Vector3.zero, out Vector3 grounded));
            Assert.AreEqual(-6f, grounded.y, 0.05f);
        }

        /// <summary>A thin box collider centred at <paramref name="atY"/>, spanning the origin.</summary>
        private GameObject Floor(float atY, int layer)
        {
            var floor = new GameObject("floor-" + atY + "-" + layer);
            floor.transform.SetParent(_world.transform);
            floor.layer = layer;
            floor.transform.position = new Vector3(0f, atY - 0.05f, 0f);

            BoxCollider box = floor.AddComponent<BoxCollider>();
            box.size = new Vector3(50f, 0.1f, 50f);

            // EditMode never steps physics, so without this the collider is not in the physics
            // scene and every cast below would miss for a reason unrelated to the code here.
            Physics.SyncTransforms();
            return floor;
        }
    }
}
