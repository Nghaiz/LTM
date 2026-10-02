using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A map loaded for practice starts no server: the <c>NetServer</c> object switches itself off
    /// and the role stays Offline.
    /// </summary>
    /// <remarks>
    /// Edit mode runs no <c>Awake</c> on <c>AddComponent</c>, so it is invoked by hand, as a scene
    /// load would. The control is a declared client, whose guard returns but leaves the object on.
    /// </remarks>
    public sealed class OfflineServerStandsDownTests
    {
        private GameObject _host;
        private float _maximumDeltaTime;

        [SetUp]
        public void SetUp()
        {
            NetContext.Clear();
            _maximumDeltaTime = Time.maximumDeltaTime;
        }

        [TearDown]
        public void TearDown()
        {
            NetContext.Clear();
            // Awake sets this engine knob before any guard; edit mode would keep it.
            Time.maximumDeltaTime = _maximumDeltaTime;
            if (_host != null) Object.DestroyImmediate(_host);
        }

        [Test]
        public void APracticeMapsServerSwitchesItsObjectOffAndStartsNothing()
        {
            NetContext.DeclareOfflineProcess();

            NetServerBootstrap server = Wake();

            Assert.IsFalse(_host.activeSelf, "the server's object stayed on in practice: its tick loop and match run");
            Assert.AreEqual(NetRole.Offline, NetContext.Role,
                "the server claimed the role, and at Server the player has no input but walking");
            Assert.IsNull(server.Loopback, "an offline map started a server on the loopback wire");
            Assert.IsNull(server.Udp, "an offline map bound a UDP port");
        }

        [Test]
        public void ADeclaredClientsServerReturnsWithoutSwitchingItsObjectOff()
        {
            NetContext.DeclareClientProcess();

            Wake();

            Assert.IsTrue(_host.activeSelf, "only the offline declaration switches the object off");
        }

        private NetServerBootstrap Wake()
        {
            _host = new GameObject("NetServer");
            NetServerBootstrap server = _host.AddComponent<NetServerBootstrap>();
            MethodInfo awake = typeof(NetServerBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(awake, "Setup: NetServerBootstrap has no Awake");
            awake.Invoke(server, null);
            return server;
        }
    }
}
