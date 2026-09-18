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

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Controller");
            _controller = _root.AddComponent<MenuScreenController>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void PracticeAndSettingsAreMutuallyExclusiveLocalScreens()
        {
            _controller.OpenPractice();
            Assert.IsTrue(_controller.IsPracticeScreenOpen);
            Assert.IsFalse(_controller.IsSettingsScreenOpen);

            _controller.OpenSettings();
            Assert.IsFalse(_controller.IsPracticeScreenOpen);
            Assert.IsTrue(_controller.IsSettingsScreenOpen);

            _controller.CloseSettings();
            Assert.IsFalse(_controller.IsSettingsScreenOpen);
        }
    }
}
