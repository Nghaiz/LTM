#nullable enable

using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuSettingsModelTests
    {
        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(MenuSettingsModel.ResolutionWidthKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.ResolutionHeightKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.FullscreenModeKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.QualityKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.VSyncKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.MasterVolumeKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.FieldOfViewKey);
            PlayerPrefs.DeleteKey(MenuSettingsModel.SensitivityKey);
        }

        [Test]
        public void ResolutionCatalogRemovesDuplicatesAndSortsByPixelCount()
        {
            DisplayResolutionOption[] result = DisplayResolutionCatalog.Build(new[]
            {
                new DisplayResolutionOption(1920, 1080),
                new DisplayResolutionOption(1280, 720),
                new DisplayResolutionOption(1920, 1080),
                new DisplayResolutionOption(0, 0),
            });

            Assert.AreEqual(2, result.Length);
            Assert.AreEqual("1280 × 720", result[0].Label);
            Assert.AreEqual("1920 × 1080", result[1].Label);
            Assert.AreEqual(1, DisplayResolutionCatalog.FindBestIndex(result, 1900, 1060));
        }

        [Test]
        public void SettingsRoundTripUsesExistingGameplayPreferenceKeys()
        {
            var expected = new MenuSettingsData(
                1600, 900, 1, 3, 1, 0.65f, 103f, 0.42f);

            MenuSettingsModel.Save(expected);
            MenuSettingsData actual = MenuSettingsModel.Load(expected);

            Assert.AreEqual(expected.ResolutionWidth, actual.ResolutionWidth);
            Assert.AreEqual(expected.ResolutionHeight, actual.ResolutionHeight);
            Assert.AreEqual(expected.DisplayMode, actual.DisplayMode);
            Assert.AreEqual(expected.Quality, actual.Quality);
            Assert.AreEqual(expected.VSync, actual.VSync);
            Assert.AreEqual(expected.MasterVolume, actual.MasterVolume, 0.001f);
            Assert.AreEqual(expected.FieldOfView, actual.FieldOfView, 0.001f);
            Assert.AreEqual(expected.Sensitivity, actual.Sensitivity, 0.001f);
        }

        [Test]
        public void BorderlessIgnoresTheChosenResolutionSoTheImageIsNotUpscaled()
        {
            // The reported bug, as a value. Borderless renders at whatever this returns and scales
            // it to the window, so returning the player's 1280x720 here is exactly the call that
            // made the resolution row blur the game instead of resizing it.
            var data = new MenuSettingsData(1280, 720, 1, 3, 1, 1f, 90f, 0.5f);

            MenuSettingsScreen.DisplayApplication applied =
                MenuSettingsScreen.ResolveApplication(data, 2560, 1440);

            Assert.AreEqual(2560, applied.Width);
            Assert.AreEqual(1440, applied.Height);
            Assert.AreEqual(FullScreenMode.FullScreenWindow, applied.Mode);
        }

        [Test]
        public void WindowedResizesTheWindowToTheChosenResolution()
        {
            var data = new MenuSettingsData(1280, 720, 0, 3, 1, 1f, 90f, 0.5f);

            MenuSettingsScreen.DisplayApplication applied =
                MenuSettingsScreen.ResolveApplication(data, 2560, 1440);

            Assert.AreEqual(1280, applied.Width);
            Assert.AreEqual(720, applied.Height);
            Assert.AreEqual(FullScreenMode.Windowed, applied.Mode);
        }

        [Test]
        public void FullscreenChangesTheDisplayModeToTheChosenResolution()
        {
            var data = new MenuSettingsData(1280, 720, 2, 3, 1, 1f, 90f, 0.5f);

            MenuSettingsScreen.DisplayApplication applied =
                MenuSettingsScreen.ResolveApplication(data, 2560, 1440);

            Assert.AreEqual(1280, applied.Width);
            Assert.AreEqual(720, applied.Height);
            Assert.AreEqual(FullScreenMode.ExclusiveFullScreen, applied.Mode);
        }

        [Test]
        public void TheDisplayModeRowDistinguishesTheTwoFullscreenModes()
        {
            // Screen.fullScreen is true for BOTH, which is why reading the row back through it
            // reported a fullscreen build as BORDERLESS -- and once the two modes do different
            // things with the resolution, misreading the mode means the next Save discards it.
            Assert.AreEqual(0, MenuSettingsScreen.DisplayModeIndexOf(FullScreenMode.Windowed));
            Assert.AreEqual(1, MenuSettingsScreen.DisplayModeIndexOf(FullScreenMode.FullScreenWindow));
            Assert.AreEqual(2, MenuSettingsScreen.DisplayModeIndexOf(FullScreenMode.ExclusiveFullScreen));
        }

        [Test]
        public void TheResolutionRowIsDisabledOnlyWhileBorderlessIsSelected()
        {
            var root = new GameObject("Settings");

            // Built inactive so Awake runs AFTER Configure has handed the component its controls.
            // On an active object Awake would run first, wire nothing, and never subscribe to the
            // mode row -- the same reason the scene's serialized fields are assigned before play.
            root.SetActive(false);
            MenuSettingsScreen screen = root.AddComponent<MenuSettingsScreen>();
            var resolution = new GameObject("Resolution", typeof(Dropdown)).GetComponent<Dropdown>();
            var displayMode = new GameObject("DisplayMode", typeof(Dropdown)).GetComponent<Dropdown>();
            var quality = new GameObject("Quality", typeof(Dropdown)).GetComponent<Dropdown>();
            var vSync = new GameObject("VSync", typeof(Toggle)).GetComponent<Toggle>();
            var volume = new GameObject("Volume", typeof(Slider)).GetComponent<Slider>();
            var fov = new GameObject("Fov", typeof(Slider)).GetComponent<Slider>();
            var sensitivity = new GameObject("Sensitivity", typeof(Slider)).GetComponent<Slider>();

            try
            {
                screen.Configure(resolution, displayMode, quality, vSync, volume, fov, sensitivity);
                screen.SetResolutionOptions(new[] { new DisplayResolutionOption(1280, 720) });
                root.SetActive(true);

                displayMode.value = 1;
                Assert.IsFalse(resolution.interactable,
                    "BORDERLESS cannot resize the window, so the row must not offer to.");

                displayMode.value = 0;
                Assert.IsTrue(resolution.interactable,
                    "WINDOWED is exactly the mode the player asked for, so the row must be usable.");
            }
            finally
            {
                Object.DestroyImmediate(resolution.gameObject);
                Object.DestroyImmediate(displayMode.gameObject);
                Object.DestroyImmediate(quality.gameObject);
                Object.DestroyImmediate(vSync.gameObject);
                Object.DestroyImmediate(volume.gameObject);
                Object.DestroyImmediate(fov.gameObject);
                Object.DestroyImmediate(sensitivity.gameObject);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SettingsScreenSavesValuesFromTheHtmlControls()
        {
            var root = new GameObject("Settings", typeof(MenuSettingsScreen));
            var resolution = new GameObject("Resolution", typeof(Dropdown)).GetComponent<Dropdown>();
            var displayMode = new GameObject("DisplayMode", typeof(Dropdown)).GetComponent<Dropdown>();
            var quality = new GameObject("Quality", typeof(Dropdown)).GetComponent<Dropdown>();
            var vSync = new GameObject("VSync", typeof(Toggle)).GetComponent<Toggle>();
            var volume = new GameObject("Volume", typeof(Slider)).GetComponent<Slider>();
            var fov = new GameObject("Fov", typeof(Slider)).GetComponent<Slider>();
            var sensitivity = new GameObject("Sensitivity", typeof(Slider)).GetComponent<Slider>();

            try
            {
                MenuSettingsScreen screen = root.GetComponent<MenuSettingsScreen>();
                screen.Configure(resolution, displayMode, quality, vSync, volume, fov, sensitivity);
                screen.SetResolutionOptions(new[]
                {
                    new DisplayResolutionOption(1280, 720),
                    new DisplayResolutionOption(1920, 1080),
                });
                resolution.value = 1;
                displayMode.value = 2;
                quality.value = 1;
                vSync.isOn = true;
                volume.value = 0.7f;
                fov.value = 105f;
                sensitivity.value = 0.6f;

                screen.Save(applyRuntime: false);

                MenuSettingsData actual = MenuSettingsModel.Load(default);
                Assert.AreEqual(1920, actual.ResolutionWidth);
                Assert.AreEqual(1080, actual.ResolutionHeight);
                Assert.AreEqual(2, actual.DisplayMode);
                Assert.AreEqual(1, actual.Quality);
                Assert.AreEqual(1, actual.VSync);
                Assert.AreEqual(0.7f, actual.MasterVolume, 0.001f);
                Assert.AreEqual(105f, actual.FieldOfView, 0.001f);
                Assert.AreEqual(0.6f, actual.Sensitivity, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(resolution.gameObject);
                Object.DestroyImmediate(displayMode.gameObject);
                Object.DestroyImmediate(quality.gameObject);
                Object.DestroyImmediate(vSync.gameObject);
                Object.DestroyImmediate(volume.gameObject);
                Object.DestroyImmediate(fov.gameObject);
                Object.DestroyImmediate(sensitivity.gameObject);
                Object.DestroyImmediate(root);
            }
        }
    }
}
