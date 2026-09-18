#nullable enable

using System;
using System.Reflection;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuTitleSettingsTests
    {
        private GameObject _root = null!;
        private IGameSettingsLauncher? _previous;

        [SetUp]
        public void SetUp()
        {
            _previous = NetClientBindings.Settings;
            _root = new GameObject("Multiplayer Menu", typeof(RectTransform), typeof(Canvas));
        }

        [TearDown]
        public void TearDown()
        {
            NetClientBindings.Settings = _previous;
            UnityEngine.Object.DestroyImmediate(_root);
        }

        [Test]
        public void SettingsButtonInvokesTheSharedLauncherExactlyOnce()
        {
            FakeSettingsLauncher launcher = new FakeSettingsLauncher(true);
            NetClientBindings.Settings = launcher;
            Button settings = BuildTitleAndGetSettingsButton();

            settings.onClick.Invoke();

            Assert.AreEqual(1, launcher.ShowCount);
        }

        [Test]
        public void SettingsButtonIsDisabledWhenTheLegacyOwnerIsUnavailable()
        {
            NetClientBindings.Settings = new FakeSettingsLauncher(false);
            Button settings = BuildTitleAndGetSettingsButton();

            Assert.False(settings.interactable);
        }

        [Test]
        public void ThemePlacesSettingsAfterPracticeInKeyboardOrder()
        {
            GameObject title = new GameObject("Title", typeof(RectTransform), typeof(Image));
            title.transform.SetParent(_root.transform, false);
            MakeButton(title, "Multiplayer");
            MakeButton(title, "Practice");
            Button settings = MakeButton(title, "Settings");

            MenuRuntimeTheme.Apply(_root);

            MenuKeyboardNavigator navigator = title.GetComponent<MenuKeyboardNavigator>();
            Selectable[] order = (Selectable[])typeof(MenuKeyboardNavigator)
                .GetField("_order", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(navigator);
            Assert.AreSame(settings, order[2]);
        }

        private Button BuildTitleAndGetSettingsButton()
        {
            GameObject title = new GameObject("Title", typeof(RectTransform));
            title.transform.SetParent(_root.transform, false);
            MenuTitleScreen screen = title.AddComponent<MenuTitleScreen>();
            Button settings = MakeButton(title, "Settings");
            typeof(MenuTitleScreen).GetField("_settingsButton", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(screen, settings);
            typeof(MenuTitleScreen).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(screen, null);
            return settings;
        }

        private static Button MakeButton(GameObject parent, string name)
        {
            GameObject host = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            host.transform.SetParent(parent.transform, false);
            return host.GetComponent<Button>();
        }

        private sealed class FakeSettingsLauncher : IGameSettingsLauncher
        {
            public FakeSettingsLauncher(bool available) => IsAvailable = available;
            public bool IsAvailable { get; }
            public int ShowCount { get; private set; }
            public void ShowSettings() => ShowCount++;
        }
    }
}
