#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Practice is the offline single-player game, after an online match as on a fresh launch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ClientFlowBootstrap</c> declares the process a client before every online join, and
    /// nothing undid it, so a practice map loaded afterwards found <c>IsDeclaredClient</c> still
    /// set and <c>VehicleSpawner</c>, which stands down on a client, spawned nothing.
    /// </para>
    /// <para>
    /// Practice then declared the Server role, which every single-player path reads as a headless
    /// authority: the player could only walk, the score stayed 1000 - 1000 and the first spawn wave
    /// threw on team -1 (owner report 2026-10-02). It is declared offline now.
    /// </para>
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
                "practice is the single-player game: at the Server role the player has no input but walking");
            Assert.IsTrue(practice.DeclaredOfflineAtLaunch,
                "the practice map loaded undeclared, so its two bootstraps race for the role and dial a server");
        }

        private sealed class RecordingPractice : IPracticeLauncher
        {
            public string? Launched;
            public bool DeclaredClientAtLaunch;
            public bool DeclaredOfflineAtLaunch;
            public NetRole RoleAtLaunch;

            public bool IsAvailable => true;

            public void ShowPracticeMenu() { }

            public void HidePracticeMenu() { }

            public void LaunchMap(string sceneName)
            {
                Launched = sceneName;
                DeclaredClientAtLaunch = NetContext.IsDeclaredClient;
                DeclaredOfflineAtLaunch = NetContext.IsDeclaredOffline;
                RoleAtLaunch = NetContext.Role;
            }
        }
    }
}
