#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuScreenTransitionTests
    {
        private GameObject _panel = null!;
        private CanvasGroup _group = null!;
        private MenuScreenTransition _transition = null!;

        [SetUp]
        public void SetUp()
        {
            _panel = new GameObject("Panel", typeof(RectTransform), typeof(CanvasGroup));
            _group = _panel.GetComponent<CanvasGroup>();
            _transition = _panel.AddComponent<MenuScreenTransition>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_panel);

        [Test]
        public void ImmediateShowActivatesAndEnablesInput()
        {
            _transition.SetVisible(false, immediate: true);

            _transition.SetVisible(true, immediate: true);

            Assert.IsTrue(_panel.activeSelf);
            Assert.AreEqual(1f, _group.alpha);
            Assert.IsTrue(_group.interactable);
            Assert.IsTrue(_group.blocksRaycasts);
            Assert.IsTrue(_transition.IsTargetVisible);
        }

        [Test]
        public void ImmediateHideBlocksInputAndDeactivates()
        {
            _transition.SetVisible(false, immediate: true);

            Assert.IsFalse(_group.interactable);
            Assert.IsFalse(_group.blocksRaycasts);
            Assert.IsFalse(_panel.activeSelf);
            Assert.IsFalse(_transition.IsTargetVisible);
        }
    }
}
