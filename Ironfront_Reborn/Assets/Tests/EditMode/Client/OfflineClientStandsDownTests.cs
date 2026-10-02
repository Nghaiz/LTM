using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A map loaded for practice runs no client: the <c>NetClient</c> object switches itself off and
    /// the role stays Offline.
    /// </summary>
    /// <remarks>
    /// Edit mode runs no <c>Awake</c> on <c>AddComponent</c> (see
    /// <c>DedicatedServerDeclinesLocalClientTests</c>), so it is invoked by hand, as a scene load
    /// would. The control is a dedicated server, whose guard returns but leaves the object on: the
    /// switch-off is the offline declaration's, not every early return's.
    /// </remarks>
    public sealed class OfflineClientStandsDownTests
    {
        private GameObject _host;

        [SetUp]
        public void SetUp() => NetContext.Clear();

        [TearDown]
        public void TearDown()
        {
            NetContext.Clear();
            if (_host != null) Object.DestroyImmediate(_host);
        }

        [Test]
        public void APracticeMapsClientSwitchesItsObjectOffAndDialsNothing()
        {
            NetContext.DeclareOfflineProcess();

            NetClientBootstrap client = Wake();

            Assert.IsFalse(_host.activeSelf, "the client's object stayed on in practice: its presenters and registry run");
            Assert.AreEqual(NetRole.Offline, NetContext.Role,
                "the client claimed the role, so the single-player paths stop reading Offline");
            Assert.AreNotSame(client, NetClientBootstrap.Current, "an offline map published a client");
        }

        [Test]
        public void ADedicatedServersClientReturnsWithoutSwitchingItsObjectOff()
        {
            NetContext.DeclareDedicatedServer();

            Wake();

            Assert.IsTrue(_host.activeSelf, "only the offline declaration switches the object off");
        }

        [Test]
        public void JoiningOnlineAfterPracticeRunsTheClientAgain()
        {
            NetContext.DeclareOfflineProcess();

            NetContext.DeclareClientProcess();

            Assert.IsFalse(NetContext.IsDeclaredOffline,
                "an online match after practice would load with its client switched off");
        }

        private NetClientBootstrap Wake()
        {
            _host = new GameObject("NetClient");
            NetClientBootstrap client = _host.AddComponent<NetClientBootstrap>();
            MethodInfo awake = typeof(NetClientBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(awake, "Setup: NetClientBootstrap has no Awake");
            awake.Invoke(client, null);
            return client;
        }
    }
}
