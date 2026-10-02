using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A network-driven vehicle's wheels are simulated only near the camera; a vehicle this process
    /// moves with physics always keeps them.
    /// </summary>
    public sealed class RemoteWheelSimulationTests
    {
        private GameObject _viewer;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _viewer = new GameObject("Viewer");
            _camera = _viewer.AddComponent<Camera>();
            _viewer.transform.position = new Vector3(500f, 60f, 500f);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_viewer);

        [Test]
        public void ARemoteVehicleNearTheCameraKeepsItsWheels()
        {
            Vector3 near = _viewer.transform.position + new Vector3(40f, -50f, 30f);

            Assert.IsTrue(RemoteWheelSimulation.IsSimulated(true, near, _camera),
                "a vehicle in front of the camera rides with frozen suspension");
        }

        [Test]
        public void ARemoteVehicleFarFromTheCameraRestsItsWheels()
        {
            Vector3 far = _viewer.transform.position + new Vector3(RemoteWheelSimulation.WithinMetres + 1f, 0f, 0f);

            Assert.IsFalse(RemoteWheelSimulation.IsSimulated(true, far, _camera),
                "PhysX simulates the wheels of a vehicle nobody can see turning");
        }

        [Test]
        public void AVehicleUnderLocalPhysicsAlwaysKeepsItsWheels()
        {
            Vector3 far = _viewer.transform.position + new Vector3(5000f, 0f, 0f);

            Assert.IsTrue(RemoteWheelSimulation.IsSimulated(false, far, _camera),
                "a vehicle the server or practice drives lost the wheels it stands on");
            Assert.IsTrue(RemoteWheelSimulation.IsSimulated(false, far, null),
                "a headless server's vehicles lost the wheels they stand on");
        }
    }
}
