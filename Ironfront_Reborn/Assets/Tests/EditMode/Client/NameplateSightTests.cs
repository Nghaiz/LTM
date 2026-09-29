#nullable enable

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A name plate is drawn only for a body the viewer can see, tested with real colliders:
    /// the owner's report of 2026-09-30 was a plate floating over the wall its player hid behind,
    /// and the other side firing a rocket into the wall.
    /// </summary>
    public sealed class NameplateSightTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly float[] _heights = new float[3];
        private Camera? _camera;

        private static readonly Vector3 Eye = new Vector3(0f, 1.6f, 0f);

        [SetUp]
        public void SetUp()
        {
            var cameraObject = new GameObject("Nameplate Sight Camera", typeof(Camera));
            _made.Add(cameraObject);
            _camera = cameraObject.GetComponent<Camera>();
            _camera.transform.position = Eye;
            _camera.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            _camera.fieldOfView = 60f;
            _camera.aspect = 16f / 9f;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject made in _made)
                if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
        }

        /// <summary>A solid box on the world layer, like the maps' walls and rocks.</summary>
        private void Wall(Vector3 centre, Vector3 size, int layer = 0)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _made.Add(wall);
            wall.layer = layer;
            wall.transform.position = centre;
            wall.transform.localScale = size;
            Physics.SyncTransforms();
        }

        private bool Sees(Vector3 feet, bool crouching = false, bool seated = false)
            => NameplatePresenter.CanSee(_camera!, Eye, feet, crouching, false, seated, _heights);

        [Test]
        public void ABodyInTheOpen_IsSeen()
            => Assert.IsTrue(Sees(new Vector3(0f, 0f, 20f)));

        /// <summary>
        /// The report's case: the wall is lower than the line to the old anchor over the head, and
        /// higher than every line to the body. The old test saw the anchor and drew the plate.
        /// </summary>
        [Test]
        public void ABodyBehindAWall_IsNotSeen_EvenWhereTheOldAnchorLineClearedTheWall()
        {
            var feet = new Vector3(0f, 0f, 12f);
            Wall(new Vector3(0f, 0.925f, 10f), new Vector3(4f, 1.85f, 0.3f));

            Vector3 oldAnchor = feet + Vector3.up * (1.8f + 0.45f);
            Assert.IsFalse(
                Physics.Linecast(Eye, oldAnchor, 1, QueryTriggerInteraction.Ignore),
                "the scene no longer reproduces the report: the old anchor line must clear the wall");

            Assert.IsFalse(Sees(feet), "a plate would float over the wall again");
        }

        [Test]
        public void AHeadOverTheWall_IsSeen()
        {
            // A wall at chest height: the head shows over it, so the plate does too.
            Wall(new Vector3(0f, 0.6f, 10f), new Vector3(4f, 1.2f, 0.3f));
            Assert.IsTrue(Sees(new Vector3(0f, 0f, 20f)));
        }

        [Test]
        public void ACrouchedBodyBehindTheSameWall_IsNotSeen()
        {
            Wall(new Vector3(0f, 0.6f, 10f), new Vector3(4f, 1.2f, 0.3f));
            Assert.IsFalse(Sees(new Vector3(0f, 0f, 11f), crouching: true));
        }

        [Test]
        public void ABodyBehindTheCamera_IsNotSeen()
            => Assert.IsFalse(Sees(new Vector3(0f, 0f, -20f)));

        [Test]
        public void ABodyOutsideTheView_IsNotSeen()
            => Assert.IsFalse(Sees(new Vector3(60f, 0f, 10f)));

        /// <summary>A parked vehicle hides whoever stands behind it, as a wall does.</summary>
        [Test]
        public void ABodyBehindAVehicle_IsNotSeen()
        {
            Wall(new Vector3(0f, 1.2f, 10f), new Vector3(5f, 2.4f, 2f), layer: 12);
            Assert.IsFalse(Sees(new Vector3(0f, 0f, 13f)));
        }

        /// <summary>A driver's own vehicle wraps round them; it must not hide every driver from view.</summary>
        [Test]
        public void ASeatedBody_IsNotHiddenByVehicles()
        {
            Wall(new Vector3(0f, 0.8f, 19f), new Vector3(3f, 1.6f, 4f), layer: 12);
            Assert.IsTrue(Sees(new Vector3(0f, 0f, 20f), seated: true));
        }

        /// <summary>Bodies, ragdolls and hitboxes are not cover: only the world and vehicles are.</summary>
        [Test]
        public void AnotherBodyInTheWay_IsNotCover()
        {
            Wall(new Vector3(0f, 1f, 10f), new Vector3(1f, 2f, 1f), layer: 8);   // Hitbox
            Wall(new Vector3(0f, 1f, 12f), new Vector3(1f, 2f, 1f), layer: 14);  // Actor
            Assert.IsTrue(Sees(new Vector3(0f, 0f, 20f)));
        }
    }
}
