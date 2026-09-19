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

        public static MenuSettingsData Load(MenuSettingsData fallback)
            => new MenuSettingsData(
                PlayerPrefs.GetInt(ResolutionWidthKey, fallback.ResolutionWidth),
                PlayerPrefs.GetInt(ResolutionHeightKey, fallback.ResolutionHeight),
                PlayerPrefs.GetInt(FullscreenModeKey, fallback.DisplayMode),
                PlayerPrefs.GetInt(QualityKey, fallback.Quality),
                PlayerPrefs.GetInt(VSyncKey, fallback.VSync),
                PlayerPrefs.GetFloat(MasterVolumeKey, fallback.MasterVolume),
                PlayerPrefs.GetFloat(FieldOfViewKey, fallback.FieldOfView),
                PlayerPrefs.GetFloat(SensitivityKey, fallback.Sensitivity));

        public static void Save(MenuSettingsData value)
        {
            PlayerPrefs.SetInt(ResolutionWidthKey, Mathf.Max(1, value.ResolutionWidth));
            PlayerPrefs.SetInt(ResolutionHeightKey, Mathf.Max(1, value.ResolutionHeight));
            PlayerPrefs.SetInt(FullscreenModeKey, Mathf.Clamp(value.DisplayMode, 0, 2));
            PlayerPrefs.SetInt(QualityKey, Mathf.Max(0, value.Quality));
            PlayerPrefs.SetInt(VSyncKey, Mathf.Clamp(value.VSync, 0, 1));
            PlayerPrefs.SetFloat(MasterVolumeKey, Mathf.Clamp01(value.MasterVolume));
            PlayerPrefs.SetFloat(FieldOfViewKey, Mathf.Clamp(value.FieldOfView, 60f, 120f));
            PlayerPrefs.SetFloat(SensitivityKey, Mathf.Max(0.05f, value.Sensitivity));
            PlayerPrefs.Save();
        }
    }
}
