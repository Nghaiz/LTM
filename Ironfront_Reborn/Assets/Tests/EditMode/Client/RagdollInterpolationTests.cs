using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A ragdoll is interpolated only near the camera: past
    /// <see cref="RemoteRagdoll.InterpolateWithinMetres"/> its parts are drawn at the physics step.
    /// </summary>
    public sealed class RagdollInterpolationTests
    {
        private GameObject _viewer;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _viewer = new GameObject("Viewer");
            _camera = _viewer.AddComponent<Camera>();
            _viewer.transform.position = new Vector3(100f, 20f, 100f);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_viewer);

        [Test]
        public void ABodyNearTheCameraIsInterpolated()
        {
            Vector3 near = _viewer.transform.position + new Vector3(10f, -5f, 10f);

            Assert.AreEqual(RigidbodyInterpolation.Interpolate, RemoteRagdoll.InterpolationAt(near, _camera),
                "a body falling in front of the camera jumps at the physics step");
        }

        [Test]
        public void ABodyFarFromTheCameraIsNot()
        {
            Vector3 far = _viewer.transform.position + new Vector3(0f, 0f, RemoteRagdoll.InterpolateWithinMetres + 1f);

            Assert.AreEqual(RigidbodyInterpolation.None, RemoteRagdoll.InterpolationAt(far, _camera),
                "a far body writes its transform every frame, and every raycast after resyncs it");
        }

        [Test]
        public void WithNoCameraNothingIsInterpolated()
        {
            Assert.AreEqual(RigidbodyInterpolation.None, RemoteRagdoll.InterpolationAt(Vector3.zero, null));
        }
    }
}
