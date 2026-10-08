#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>The settings screen's categories, in tab order.</summary>
    public enum SettingsCategory
    {
        Display,
        Graphics,
        Audio,
        Gameplay,
        Vehicles,
        Controls,
    }

    /// <summary>
    /// Every value the settings screen edits, held apart from the saved ones until APPLY: the
    /// display and graphics settings the menu always had, the gameplay options that used to live
    /// only on the match's own options panel, and a copy of the key bindings.
    /// </summary>
    /// <remarks>
    /// Plain data, so the save, load and per-tab reset rules are testable without a screen. The
    /// keys and defaults come from their owners: <see cref="MenuSettingsModel"/> for the display,
    /// <see cref="GameOptionsStore"/> for the game's options, <see cref="GameKeys"/> for the keys.
    /// </remarks>
    public sealed class SettingsDraft
    {
        // Display
        public int ResolutionWidth;
        public int ResolutionHeight;
        public int DisplayMode;
        public bool VSync;
        public int FpsLimit;
        public float FieldOfView;

        // Graphics
        public int Quality;
        public float VegetationDensity;
        public float VegetationDistance;

        // Audio
        public float MasterVolume;

        // Gameplay
        public float MouseSensitivity;
        public float ScopeMultiplier;
        public bool InvertMouse;
        public bool ToggleAim;
        public bool ToggleCrouch;
        public bool AutoReload;
        public bool HitIndicators;
        public int Difficulty;

        // Vehicles
        public int HelicopterStyle;
        public float HelicopterSensitivity;
        public bool HelicopterInvertPitch;
        public bool HelicopterInvertYaw;
        public bool HelicopterInvertRoll;
        public bool HelicopterInvertThrottle;

        // Controls
        public KeyBindingSet Keys = new KeyBindingSet();

        /// <summary>What is saved now, falling back to the running display for anything never saved.</summary>
        public static SettingsDraft Load()
        {
            var draft = new SettingsDraft();
            var fallback = new MenuSettingsData(
                Screen.width, Screen.height, MenuSettingsModel.DisplayModeIndexOf(Screen.fullScreenMode),
                QualitySettings.GetQualityLevel(), QualitySettings.vSyncCount > 0 ? 1 : 0,
                GameOptionsStore.DefaultMasterVolume, GameOptionsStore.DefaultFieldOfView,
                GameOptionsStore.DefaultMouseSensitivity, 0);
            MenuSettingsData display = MenuSettingsModel.Load(fallback);
            draft.ResolutionWidth = display.ResolutionWidth;
            draft.ResolutionHeight = display.ResolutionHeight;
            draft.DisplayMode = display.DisplayMode;
            draft.VSync = display.VSync > 0;
            draft.FpsLimit = display.FpsLimit;
            draft.Quality = display.Quality;

            draft.FieldOfView = GameOptionsStore.GetFloat(GameOptionsStore.FieldOfViewKey, GameOptionsStore.DefaultFieldOfView);
            draft.VegetationDensity = Mathf.Clamp01(GameOptionsStore.GetFloat(VegetationRules.DensityKey, VegetationRules.DefaultDensity));
            draft.VegetationDistance = Mathf.Clamp01(GameOptionsStore.GetFloat(VegetationRules.DistanceKey, VegetationRules.DefaultDistance));
            draft.MasterVolume = Mathf.Clamp01(GameOptionsStore.GetFloat(GameOptionsStore.MasterVolumeKey, GameOptionsStore.DefaultMasterVolume));

            draft.MouseSensitivity = GameOptionsStore.GetFloat(GameOptionsStore.MouseSensitivityKey, GameOptionsStore.DefaultMouseSensitivity);
            draft.ScopeMultiplier = GameOptionsStore.GetFloat(GameOptionsStore.ScopeMultiplierKey, GameOptionsStore.DefaultScopeMultiplier);
            draft.InvertMouse = GameOptionsStore.GetBool(GameOptionsStore.InvertMouseKey, false);
            draft.ToggleAim = GameOptionsStore.GetBool(GameOptionsStore.ToggleAimKey, false);
            draft.ToggleCrouch = GameOptionsStore.GetBool(GameOptionsStore.ToggleCrouchKey, false);
            draft.AutoReload = GameOptionsStore.GetBool(GameOptionsStore.AutoReloadKey, false);
            draft.HitIndicators = GameOptionsStore.GetBool(GameOptionsStore.HitIndicatorsKey, true);
            draft.Difficulty = GameOptionsStore.GetInt(GameOptionsStore.DifficultyKey, GameOptionsStore.DefaultDifficulty);

            draft.HelicopterStyle = GameOptionsStore.GetInt(GameOptionsStore.HelicopterStyleKey, GameOptionsStore.DefaultHelicopterStyle);
            draft.HelicopterSensitivity = GameOptionsStore.GetFloat(GameOptionsStore.HelicopterSensitivityKey, GameOptionsStore.DefaultHelicopterSensitivity);
            draft.HelicopterInvertPitch = GameOptionsStore.GetBool(GameOptionsStore.HelicopterInvertPitchKey, false);
            draft.HelicopterInvertYaw = GameOptionsStore.GetBool(GameOptionsStore.HelicopterInvertYawKey, false);
            draft.HelicopterInvertRoll = GameOptionsStore.GetBool(GameOptionsStore.HelicopterInvertRollKey, false);
            draft.HelicopterInvertThrottle = GameOptionsStore.GetBool(GameOptionsStore.HelicopterInvertThrottleKey, true);

            draft.Keys.CopyFrom(GameKeys.Bindings);
            return draft;
        }

        /// <summary>Puts one category back on the values the game ships with.</summary>
        public void ResetToDefaults(SettingsCategory category)
        {
            switch (category)
            {
                case SettingsCategory.Display:
                    ResolutionWidth = Screen.currentResolution.width;
                    ResolutionHeight = Screen.currentResolution.height;
                    DisplayMode = 1;
                    VSync = true;
                    FpsLimit = 0;
                    FieldOfView = GameOptionsStore.DefaultFieldOfView;
                    break;
                case SettingsCategory.Graphics:
                    // The preset this machine suits, not the highest there is.
                    Quality = GraphicsSettingsBoot.Recommended();
                    VegetationDensity = VegetationRules.DefaultDensity;
                    VegetationDistance = VegetationRules.DefaultDistance;
                    break;
                case SettingsCategory.Audio:
                    MasterVolume = GameOptionsStore.DefaultMasterVolume;
                    break;
                case SettingsCategory.Gameplay:
                    MouseSensitivity = GameOptionsStore.DefaultMouseSensitivity;
                    ScopeMultiplier = GameOptionsStore.DefaultScopeMultiplier;
                    InvertMouse = false;
                    ToggleAim = false;
                    ToggleCrouch = false;
                    AutoReload = false;
                    HitIndicators = true;
                    Difficulty = GameOptionsStore.DefaultDifficulty;
                    break;
                case SettingsCategory.Vehicles:
                    HelicopterStyle = GameOptionsStore.DefaultHelicopterStyle;
                    HelicopterSensitivity = GameOptionsStore.DefaultHelicopterSensitivity;
                    HelicopterInvertPitch = false;
                    HelicopterInvertYaw = false;
                    HelicopterInvertRoll = false;
                    HelicopterInvertThrottle = true;
                    break;
                case SettingsCategory.Controls:
                    Keys.ResetToDefaults();
                    break;
            }
        }

        /// <summary>Whether this draft differs from <paramref name="saved"/>.</summary>
        public bool DiffersFrom(SettingsDraft saved)
            => ResolutionWidth != saved.ResolutionWidth || ResolutionHeight != saved.ResolutionHeight
               || DisplayMode != saved.DisplayMode || VSync != saved.VSync || FpsLimit != saved.FpsLimit
               || !Mathf.Approximately(FieldOfView, saved.FieldOfView) || Quality != saved.Quality
               || !Mathf.Approximately(VegetationDensity, saved.VegetationDensity)
               || !Mathf.Approximately(VegetationDistance, saved.VegetationDistance)
               || !Mathf.Approximately(MasterVolume, saved.MasterVolume)
               || !Mathf.Approximately(MouseSensitivity, saved.MouseSensitivity)
               || !Mathf.Approximately(ScopeMultiplier, saved.ScopeMultiplier)
               || InvertMouse != saved.InvertMouse || ToggleAim != saved.ToggleAim || ToggleCrouch != saved.ToggleCrouch
               || AutoReload != saved.AutoReload || HitIndicators != saved.HitIndicators || Difficulty != saved.Difficulty
               || HelicopterStyle != saved.HelicopterStyle
               || !Mathf.Approximately(HelicopterSensitivity, saved.HelicopterSensitivity)
               || HelicopterInvertPitch != saved.HelicopterInvertPitch || HelicopterInvertYaw != saved.HelicopterInvertYaw
               || HelicopterInvertRoll != saved.HelicopterInvertRoll
               || HelicopterInvertThrottle != saved.HelicopterInvertThrottle
               || Keys.Serialize() != saved.Keys.Serialize();

        /// <summary>Saves every value, then applies them to the running game.</summary>
        public void Save()
        {
            MenuSettingsData display = Write();
            GameKeys.Apply(Keys);
            MenuSettingsModel.ApplyDisplay(display);
            GameOptionsStore.Apply();
        }

        /// <summary>Writes every value but the keys to <c>PlayerPrefs</c>, applying nothing.</summary>
        public MenuSettingsData Write()
        {
            var display = new MenuSettingsData(ResolutionWidth, ResolutionHeight, DisplayMode, Quality, VSync ? 1 : 0,
                MasterVolume, FieldOfView, MouseSensitivity, FpsLimit);
            MenuSettingsModel.Save(display);

            GameOptionsStore.SetFloat(VegetationRules.DensityKey, Mathf.Clamp01(VegetationDensity));
            GameOptionsStore.SetFloat(VegetationRules.DistanceKey, Mathf.Clamp01(VegetationDistance));
            GameOptionsStore.SetFloat(GameOptionsStore.ScopeMultiplierKey, Mathf.Max(GameOptionsStore.MinSensitivity, ScopeMultiplier));
            GameOptionsStore.SetBool(GameOptionsStore.InvertMouseKey, InvertMouse);
            GameOptionsStore.SetBool(GameOptionsStore.ToggleAimKey, ToggleAim);
            GameOptionsStore.SetBool(GameOptionsStore.ToggleCrouchKey, ToggleCrouch);
            GameOptionsStore.SetBool(GameOptionsStore.AutoReloadKey, AutoReload);
            GameOptionsStore.SetBool(GameOptionsStore.HitIndicatorsKey, HitIndicators);
            GameOptionsStore.SetInt(GameOptionsStore.DifficultyKey, Mathf.Clamp(Difficulty, 0, GameOptionsStore.DifficultyNames.Length - 1));
            GameOptionsStore.SetInt(GameOptionsStore.HelicopterStyleKey, Mathf.Clamp(HelicopterStyle, 0, GameOptionsStore.HelicopterStyleNames.Length - 1));
            GameOptionsStore.SetFloat(GameOptionsStore.HelicopterSensitivityKey, Mathf.Max(GameOptionsStore.MinSensitivity, HelicopterSensitivity));
            GameOptionsStore.SetBool(GameOptionsStore.HelicopterInvertPitchKey, HelicopterInvertPitch);
            GameOptionsStore.SetBool(GameOptionsStore.HelicopterInvertYawKey, HelicopterInvertYaw);
            GameOptionsStore.SetBool(GameOptionsStore.HelicopterInvertRollKey, HelicopterInvertRoll);
            GameOptionsStore.SetBool(GameOptionsStore.HelicopterInvertThrottleKey, HelicopterInvertThrottle);
            return display;
        }

        /// <summary>A copy, for remembering what was saved.</summary>
        public SettingsDraft Clone()
        {
            var copy = (SettingsDraft)MemberwiseClone();
            copy.Keys = new KeyBindingSet();
            copy.Keys.CopyFrom(Keys);
            return copy;
        }
    }
}
