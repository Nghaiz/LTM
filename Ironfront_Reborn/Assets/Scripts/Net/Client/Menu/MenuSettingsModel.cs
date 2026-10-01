#nullable enable

using UnityEngine;

namespace Ironfront.Net.Unity.Client.Menu
{
    public readonly struct MenuSettingsData
    {
        public MenuSettingsData(int resolutionWidth, int resolutionHeight, int displayMode,
            int quality, int vSync, float masterVolume, float fieldOfView, float sensitivity)
        {
            ResolutionWidth = resolutionWidth;
            ResolutionHeight = resolutionHeight;
            DisplayMode = displayMode;
            Quality = quality;
            VSync = vSync;
            MasterVolume = masterVolume;
            FieldOfView = fieldOfView;
            Sensitivity = sensitivity;
        }

        public int ResolutionWidth { get; }
        public int ResolutionHeight { get; }
        public int DisplayMode { get; }
        public int Quality { get; }
        public int VSync { get; }
        public float MasterVolume { get; }
        public float FieldOfView { get; }
        public float Sensitivity { get; }
    }

    public static class MenuSettingsModel
    {
        public const string ResolutionWidthKey = "ironfront resolution width";
        public const string ResolutionHeightKey = "ironfront resolution height";
        public const string FullscreenModeKey = "ironfront display mode";
        public const string QualityKey = "ironfront quality";
        public const string VSyncKey = "ironfront vsync";
        public const string MasterVolumeKey = "master volume";
        public const string FieldOfViewKey = "field of view";
        public const string SensitivityKey = "mouse sensitivity";

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
                PlayerPrefs.GetFloat(SensitivityKey, fallback.Sensitivity));

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
            PlayerPrefs.Save();
        }
    }
}
