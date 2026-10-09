#nullable enable

using UnityEngine;

namespace Ironfront.Net.Unity.Client.Menu
{
    public readonly struct MenuSettingsData
    {
        public MenuSettingsData(int resolutionWidth, int resolutionHeight, int displayMode,
            int quality, int vSync, float masterVolume, float fieldOfView, float sensitivity,
            int fpsLimit = 0)
        {
            ResolutionWidth = resolutionWidth;
            ResolutionHeight = resolutionHeight;
            DisplayMode = displayMode;
            Quality = quality;
            VSync = vSync;
            MasterVolume = masterVolume;
            FieldOfView = fieldOfView;
            Sensitivity = sensitivity;
            FpsLimit = fpsLimit;
        }

        public int ResolutionWidth { get; }
        public int ResolutionHeight { get; }
        public int DisplayMode { get; }
        public int Quality { get; }
        public int VSync { get; }
        public float MasterVolume { get; }
        public float FieldOfView { get; }
        public float Sensitivity { get; }

        /// <summary>One of <c>CpuBudgetRules.FrameRateLimits</c>: 0 the display's refresh, -1 none.</summary>
        public int FpsLimit { get; }
    }

    public static class MenuSettingsModel
    {
        public const string ResolutionWidthKey = "ironfront resolution width";
        public const string ResolutionHeightKey = "ironfront resolution height";
        public const string FullscreenModeKey = "ironfront display mode";
        public const string QualityKey = "ironfront quality";
        public const string VSyncKey = "ironfront vsync";
        public const string MasterVolumeKey = GameOptionsStore.MasterVolumeKey;
        public const string FieldOfViewKey = GameOptionsStore.FieldOfViewKey;
        public const string SensitivityKey = GameOptionsStore.MouseSensitivityKey;
        public const string FpsLimitKey = "ironfront fps limit";

        /// <summary>
        /// The layout the saved quality belongs to (<see cref="GraphicsPresetRules.SettingsVersion"/>);
        /// absent for the six Unity levels of v3.1.1 and before.
        /// </summary>
        public const string SettingsVersionKey = "ironfront settings version";

        public static MenuSettingsData Load(MenuSettingsData fallback)
            => new MenuSettingsData(
                PlayerPrefs.GetInt(ResolutionWidthKey, fallback.ResolutionWidth),
                PlayerPrefs.GetInt(ResolutionHeightKey, fallback.ResolutionHeight),
                PlayerPrefs.GetInt(FullscreenModeKey, fallback.DisplayMode),
                LoadQuality(fallback.Quality),
                PlayerPrefs.GetInt(VSyncKey, fallback.VSync),
                PlayerPrefs.GetFloat(MasterVolumeKey, fallback.MasterVolume),
                PlayerPrefs.GetFloat(FieldOfViewKey, fallback.FieldOfView),
                PlayerPrefs.GetFloat(SensitivityKey, fallback.Sensitivity),
                PlayerPrefs.GetInt(FpsLimitKey, fallback.FpsLimit));

        /// <summary>
        /// The player's saved graphics preset, or <paramref name="fallback"/> when nothing is saved.
        /// </summary>
        /// <remarks>
        /// A preset saved under the six Unity levels is carried over to the four once
        /// (<see cref="GraphicsPresetRules.FromLegacyLevel"/>) and saved back in the new layout, so a
        /// player who had "Fantastic" -- everybody who never opened the settings and pressed Save had
        /// whatever the default was -- comes back on High rather than on the highest level there is.
        /// </remarks>
        public static int LoadQuality(int fallback)
        {
            if (!PlayerPrefs.HasKey(QualityKey)) return GraphicsPresetRules.Clamp(fallback);

            int saved = PlayerPrefs.GetInt(QualityKey, fallback);
            if (PlayerPrefs.GetInt(SettingsVersionKey, 1) < GraphicsPresetRules.SettingsVersion)
            {
                saved = GraphicsPresetRules.FromLegacyLevel(saved);
                PlayerPrefs.SetInt(QualityKey, saved);
                PlayerPrefs.SetInt(SettingsVersionKey, GraphicsPresetRules.SettingsVersion);
                PlayerPrefs.Save();
            }

            return GraphicsPresetRules.Clamp(saved);
        }

        public static void Save(MenuSettingsData value)
        {
            PlayerPrefs.SetInt(ResolutionWidthKey, Mathf.Max(1, value.ResolutionWidth));
            PlayerPrefs.SetInt(ResolutionHeightKey, Mathf.Max(1, value.ResolutionHeight));
            PlayerPrefs.SetInt(FullscreenModeKey, Mathf.Clamp(value.DisplayMode, 0, 2));
            PlayerPrefs.SetInt(QualityKey, GraphicsPresetRules.Clamp(value.Quality));
            PlayerPrefs.SetInt(SettingsVersionKey, GraphicsPresetRules.SettingsVersion);
            PlayerPrefs.SetInt(VSyncKey, Mathf.Clamp(value.VSync, 0, 1));
            PlayerPrefs.SetFloat(MasterVolumeKey, Mathf.Clamp01(value.MasterVolume));
            PlayerPrefs.SetFloat(FieldOfViewKey, Mathf.Clamp(value.FieldOfView, 60f, 120f));
            PlayerPrefs.SetFloat(SensitivityKey, Mathf.Max(0.05f, value.Sensitivity));
            PlayerPrefs.SetInt(FpsLimitKey,
                CpuBudgetRules.FrameRateLimits[CpuBudgetRules.FrameRateLimitIndex(value.FpsLimit)]);
            PlayerPrefs.Save();
        }

        /// <summary>The resolution and mode a <see cref="MenuSettingsData"/> asks the display for.</summary>
        public readonly struct DisplayApplication
        {
            public DisplayApplication(int width, int height, FullScreenMode mode)
            {
                Width = width;
                Height = height;
                Mode = mode;
            }

            public int Width { get; }
            public int Height { get; }
            public FullScreenMode Mode { get; }
        }

        /// <summary>
        /// Borderless always fills the display at its own resolution (a chosen one would only be
        /// upscaled); windowed and fullscreen use the chosen one.
        /// </summary>
        public static DisplayApplication ResolveApplication(MenuSettingsData data, int displayWidth, int displayHeight)
        {
            FullScreenMode mode = ModeFor(data.DisplayMode);
            return mode == FullScreenMode.FullScreenWindow
                ? new DisplayApplication(displayWidth, displayHeight, mode)
                : new DisplayApplication(data.ResolutionWidth, data.ResolutionHeight, mode);
        }

        public static FullScreenMode ModeFor(int displayModeIndex) => displayModeIndex switch
        {
            2 => FullScreenMode.ExclusiveFullScreen,
            1 => FullScreenMode.FullScreenWindow,
            _ => FullScreenMode.Windowed,
        };

        public static int DisplayModeIndexOf(FullScreenMode mode) => mode switch
        {
            FullScreenMode.ExclusiveFullScreen => 2,
            FullScreenMode.FullScreenWindow => 1,
            _ => 0,
        };

        /// <summary>The display mode names, by <see cref="MenuSettingsData.DisplayMode"/>.</summary>
        public static readonly string[] DisplayModeNames = { "WINDOWED", "BORDERLESS", "FULLSCREEN" };

        /// <summary>Applies the display half of <paramref name="data"/>: resolution, mode, preset, v-sync and frame cap.</summary>
        /// <remarks>
        /// Master volume is not applied here: the game's own options apply it through the mixer
        /// (<see cref="GameOptionsStore.Apply"/>), and setting the listener's volume as well
        /// turned it down twice in the menu.
        /// </remarks>
        public static void ApplyDisplay(MenuSettingsData data)
        {
            DisplayApplication application = ResolveApplication(
                data, Screen.currentResolution.width, Screen.currentResolution.height);
            Screen.SetResolution(application.Width, application.Height, application.Mode);

            // A preset carries its own v-sync, so the player's choice goes on after it, through the
            // frame cap, which keeps the menus' own cap in force until the player leaves them.
            QualitySettings.SetQualityLevel(data.Quality, applyExpensiveChanges: true);
            BackgroundFrameCap.ApplyPlayerChoice(
                data.VSync,
                CpuBudgetRules.FrameCapFor(data.FpsLimit, Screen.currentResolution.refreshRateRatio.value));
        }
    }
}
