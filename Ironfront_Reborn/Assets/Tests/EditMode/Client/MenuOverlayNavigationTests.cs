#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuOverlayNavigationTests
    {
        private GameObject _root = null!;
        private MenuScreenController _controller = null!;

        private FakeOverlays _overlays = null!;

        [SetUp]
        public void SetUp()
        {
            _overlays = FakeOverlays.Install();
            _root = new GameObject("Controller");
            _controller = _root.AddComponent<MenuScreenController>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            _overlays.Dispose();
        }

        [Test]
        public void SettingsIsTheOverlayOverWhicheverScreenIsUp()
        {
            _controller.OpenPractice();
            Assert.IsTrue(_controller.IsPracticeScreenOpen);
            Assert.IsFalse(_controller.IsSettingsScreenOpen);

            _controller.OpenSettings();
            Assert.IsTrue(_controller.IsSettingsScreenOpen, "SETTINGS opens the shared overlay page.");
            Assert.IsTrue(_controller.IsPracticeScreenOpen, "The overlay draws over the screen; it does not replace it.");

            _controller.CloseSettings();
            Assert.IsFalse(_controller.IsSettingsScreenOpen);
        }
    }
}
