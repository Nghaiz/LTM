#nullable enable

using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

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
            PlayerPrefs.DeleteKey(MenuSettingsModel.FpsLimitKey);
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
        public void TheFpsLimitRoundTripsAndAnUnofferedOneSavesAsTheDefault()
        {
            var fallback = new MenuSettingsData(1600, 900, 1, 3, 0, 0.65f, 103f, 0.42f);
            Assert.AreEqual(0, MenuSettingsModel.Load(fallback).FpsLimit, "nothing saved: the display's refresh");

            MenuSettingsModel.Save(new MenuSettingsData(1600, 900, 1, 3, 0, 0.65f, 103f, 0.42f, fpsLimit: 144));
            Assert.AreEqual(144, MenuSettingsModel.Load(fallback).FpsLimit);

            MenuSettingsModel.Save(new MenuSettingsData(1600, 900, 1, 3, 0, 0.65f, 103f, 0.42f, fpsLimit: -1));
            Assert.AreEqual(-1, MenuSettingsModel.Load(fallback).FpsLimit);

            MenuSettingsModel.Save(new MenuSettingsData(1600, 900, 1, 3, 0, 0.65f, 103f, 0.42f, fpsLimit: 30));
            Assert.AreEqual(0, MenuSettingsModel.Load(fallback).FpsLimit);
        }

        [Test]
        public void BorderlessIgnoresTheChosenResolutionSoTheImageIsNotUpscaled()
        {
            // The reported bug, as a value. Borderless renders at whatever this returns and scales
            // it to the window, so returning the player's 1280x720 here is exactly the call that
            // made the resolution row blur the game instead of resizing it.
            var data = new MenuSettingsData(1280, 720, 1, 3, 1, 1f, 90f, 0.5f);

            MenuSettingsModel.DisplayApplication applied =
                MenuSettingsModel.ResolveApplication(data, 2560, 1440);

            Assert.AreEqual(2560, applied.Width);
            Assert.AreEqual(1440, applied.Height);
            Assert.AreEqual(FullScreenMode.FullScreenWindow, applied.Mode);
        }

        [Test]
        public void WindowedResizesTheWindowToTheChosenResolution()
        {
            var data = new MenuSettingsData(1280, 720, 0, 3, 1, 1f, 90f, 0.5f);

            MenuSettingsModel.DisplayApplication applied =
                MenuSettingsModel.ResolveApplication(data, 2560, 1440);

            Assert.AreEqual(1280, applied.Width);
            Assert.AreEqual(720, applied.Height);
            Assert.AreEqual(FullScreenMode.Windowed, applied.Mode);
        }

        [Test]
        public void FullscreenChangesTheDisplayModeToTheChosenResolution()
        {
            var data = new MenuSettingsData(1280, 720, 2, 3, 1, 1f, 90f, 0.5f);

            MenuSettingsModel.DisplayApplication applied =
                MenuSettingsModel.ResolveApplication(data, 2560, 1440);

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
            Assert.AreEqual(0, MenuSettingsModel.DisplayModeIndexOf(FullScreenMode.Windowed));
            Assert.AreEqual(1, MenuSettingsModel.DisplayModeIndexOf(FullScreenMode.FullScreenWindow));
            Assert.AreEqual(2, MenuSettingsModel.DisplayModeIndexOf(FullScreenMode.ExclusiveFullScreen));
        }
    }
}
