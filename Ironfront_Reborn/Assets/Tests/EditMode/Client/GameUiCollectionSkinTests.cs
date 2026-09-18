#nullable enable

using Ironfront.Unity.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class GameUiCollectionSkinTests
    {
        private GameObject _host = null!;

        [SetUp]
        public void SetUp() => _host = new GameObject("UI Skin Test");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_host);

        [Test]
        public void LoadReturnsEverySpriteRequiredByTheShippedMenu()
        {
            GameUiCollectionResources resources = GameUiCollectionResources.Load();

            Assert.NotNull(resources.PanelCyan);
            Assert.NotNull(resources.ButtonCyan);
            Assert.NotNull(resources.ButtonYellow);
            Assert.NotNull(resources.ButtonBorderCyan);
            Assert.NotNull(resources.ButtonBorderYellow);
            Assert.NotNull(resources.IconSpeaker);
            Assert.NotNull(resources.IconMuted);
            Assert.NotNull(resources.IconBack);
            Assert.NotNull(resources.IconClose);
            Assert.NotNull(resources.IconConfirm);
        }

        [Test]
        public void StyleButtonMakesEveryInteractiveStatePerceptiblyDistinct()
        {
            Image image = _host.AddComponent<Image>();
            Button button = _host.AddComponent<Button>();

            GameUiCollectionSkin.StyleButton(button, primary: false);

            ColorBlock colors = button.colors;
            Assert.AreNotEqual(colors.normalColor, colors.highlightedColor);
            Assert.AreNotEqual(colors.normalColor, colors.pressedColor);
            Assert.AreNotEqual(colors.normalColor, colors.selectedColor);
            Assert.AreNotEqual(colors.normalColor, colors.disabledColor);
            Assert.AreSame(GameUiCollectionResources.Load().ButtonCyan, image.sprite);
        }

        [Test]
        public void StylePanelNeverBlocksControlsWithDecorativeRaycasts()
        {
            Image panel = _host.AddComponent<Image>();

            GameUiCollectionSkin.StylePanel(panel);

            Assert.False(panel.raycastTarget);
            Assert.AreSame(GameUiCollectionResources.Load().PanelCyan, panel.sprite);
        }
    }
}
