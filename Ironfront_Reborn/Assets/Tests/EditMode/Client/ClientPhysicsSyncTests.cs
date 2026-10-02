using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// <see cref="ClientPhysicsSync"/>: an online client's match syncs moved transforms once a
    /// frame instead of on every query, and leaves auto-sync as it found it everywhere else.
    /// </summary>
    /// <remarks>
    /// <c>Update</c> and <c>OnDisable</c> are invoked by reflection, as the bootstrap tests invoke
    /// <c>Awake</c>: an EditMode test has no player loop to call them.
    /// </remarks>
    public sealed class ClientPhysicsSyncTests
    {
        private GameObject _host;
        private GameObject _wall;
        private bool _autoSyncBefore;

        [SetUp]
        public void SetUp()
        {
            NetContext.Clear();
            _autoSyncBefore = Physics.autoSyncTransforms;
            Physics.autoSyncTransforms = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            if (_wall != null) Object.DestroyImmediate(_wall);
            Physics.autoSyncTransforms = _autoSyncBefore;
            NetContext.Clear();
        }

        [Test]
        public void AClientsFrameSyncsWhatMovedWithAutoSyncOff()
        {
            ClientPhysicsSync sync = NewSync();
            Tick(sync);
            Assert.IsFalse(Physics.autoSyncTransforms, "auto-sync is still on: every query re-syncs");

            _wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Physics.SyncTransforms();
            _wall.transform.position = new Vector3(10f, 0f, 0f);

            Tick(sync);

            Assert.IsTrue(
                Physics.Raycast(new Vector3(10f, 5f, 0f), Vector3.down, 10f),
                "a collider moved before the frame's sync is not where the frame's queries look");
        }

        [Test]
        public void LeavingTheMatchGivesAutoSyncBack()
        {
            ClientPhysicsSync sync = NewSync();
            Tick(sync);

            Invoke(sync, "OnDisable");

            Assert.IsTrue(Physics.autoSyncTransforms,
                "the menu and a later practice map would run the original game without auto-sync");
        }

        [Test]
        public void AProcessThatIsTheServerKeepsAutoSync()
        {
            NetContext.SetRole(NetRole.Server);
            ClientPhysicsSync sync = NewSync();

            Tick(sync);

            Assert.IsTrue(Physics.autoSyncTransforms, "the server's simulation was written against auto-sync");
            Assert.IsFalse(sync.enabled);
            Assert.IsFalse(sync.IsEngaged);
        }

        private ClientPhysicsSync NewSync()
        {
            _host = new GameObject("NetClient");
            return _host.AddComponent<ClientPhysicsSync>();
        }

        private static void Tick(ClientPhysicsSync sync) => Invoke(sync, "Update");

        private static void Invoke(ClientPhysicsSync sync, string message)
        {
            MethodInfo method = typeof(ClientPhysicsSync).GetMethod(message, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Setup: ClientPhysicsSync has no {message}");
            method.Invoke(sync, null);
        }
    }
}
