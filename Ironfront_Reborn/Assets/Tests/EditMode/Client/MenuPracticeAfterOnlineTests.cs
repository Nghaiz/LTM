#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Practice after an online match runs its own server, as it does on a fresh launch.
    /// </summary>
    /// <remarks>
    /// <c>ClientFlowBootstrap</c> declares the process a client before every online join, and
    /// nothing undid it, so a practice map loaded afterwards found <c>IsDeclaredClient</c> still
    /// set: its <c>NetServerBootstrap</c> declined to start and <c>VehicleSpawner</c>, which
    /// stands down on a client, spawned nothing.
    /// </remarks>
    public sealed class MenuPracticeAfterOnlineTests
    {
        private IPracticeLauncher? _savedPractice;
        private GameObject? _menu;

        [SetUp]
        public void SetUp()
        {
            _savedPractice = NetClientBindings.Practice;
            NetContext.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            NetClientBindings.Practice = _savedPractice!;
            NetContext.Clear();
            if (_menu != null) Object.DestroyImmediate(_menu);
        }

        [Test]
        public void APracticeMapLoadsWithTheOnlineClientDeclarationCleared()
        {
            // What an online join leaves behind: ClientFlowBootstrap.OnGameServerAccepted.
            NetContext.SetRole(NetRole.Client);
            NetContext.DeclareClientProcess();

            var practice = new RecordingPractice();
            NetClientBindings.Practice = practice;
            _menu = new GameObject("menu");
            MenuScreenController controller = _menu.AddComponent<MenuScreenController>();

            controller.LaunchPracticeMap("Dustbowl");

            Assert.AreEqual("Dustbowl", practice.Launched, "the practice map was not launched");
            Assert.IsFalse(practice.DeclaredClientAtLaunch,
                "the practice map loaded with the process still declared a client: its server declines to start");
            Assert.AreEqual(NetRole.Offline, practice.RoleAtLaunch,
                "the practice map loaded with the online match's role still set");
        }

        private sealed class RecordingPractice : IPracticeLauncher
        {
            public string? Launched;
            public bool DeclaredClientAtLaunch;
            public NetRole RoleAtLaunch;

            public bool IsAvailable => true;

            public void ShowPracticeMenu() { }

            public void HidePracticeMenu() { }

            public void LaunchMap(string sceneName)
            {
                Launched = sceneName;
                DeclaredClientAtLaunch = NetContext.IsDeclaredClient;
                RoleAtLaunch = NetContext.Role;
            }
        }
    }
}
