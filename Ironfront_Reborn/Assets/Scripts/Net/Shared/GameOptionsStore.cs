using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The player's gameplay options: their <c>PlayerPrefs</c> keys, defaults and ranges, in one
    /// place for the settings screen and for the game that reads them (<c>OptionsUi.Options</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The keys are the original game's.</b> They were literals in <c>OptionsUi</c> (and some again
    /// in the menu's settings model); a player's saved values must keep working, so the strings do
    /// not change, they only move here.
    /// </para>
    /// <para>
    /// <b>Applying</b>: the settings screen writes the values, then calls <see cref="Apply"/>, which
    /// reaches <c>OptionsUi</c> through <see cref="ApplyHandler"/> (it installs itself; this assembly
    /// cannot name it) so a value changed in the menu holds in the very next match and a value
    /// changed in a match holds at once.
    /// </para>
    /// </remarks>
    public static class GameOptionsStore
    {
        public const string MouseSensitivityKey = "mouse sensitivity";
        public const string ScopeMultiplierKey = "sniper multiplier";
        public const string InvertMouseKey = "mouse invert";
        public const string HelicopterStyleKey = "helicopter type 2";
        public const string HelicopterSensitivityKey = "helicopter sensitivity 2";
        public const string HelicopterInvertPitchKey = "helicopter invert pitch 2";
        public const string HelicopterInvertYawKey = "helicopter invert yaw";
        public const string HelicopterInvertRollKey = "helicopter invert roll";
        public const string HelicopterInvertThrottleKey = "helicopter invert throttle";
        public const string HitIndicatorsKey = "hitmarkers2";
        public const string AutoReloadKey = "auto reload";
        public const string DifficultyKey = "difficulty";
        public const string MasterVolumeKey = "master volume";
        public const string ToggleAimKey = "toggle aim";
        public const string ToggleCrouchKey = "toggle crouch";
        public const string FieldOfViewKey = "field of view";

        public const float DefaultMouseSensitivity = 0.5f;
        public const float DefaultScopeMultiplier = 0.3f;
        public const int DefaultHelicopterStyle = 1;
        public const float DefaultHelicopterSensitivity = 0.5f;
        public const int DefaultDifficulty = 1;
        public const float DefaultMasterVolume = 1f;
        public const float DefaultFieldOfView = 90f;

        public const float MinSensitivity = 0.05f;
        public const float MaxSensitivity = 1f;
        public const float MinFieldOfView = 60f;
        public const float MaxFieldOfView = 120f;

        /// <summary>The helicopter control styles, by the value <see cref="HelicopterStyleKey"/> stores.</summary>
        public static readonly string[] HelicopterStyleNames = { "MOUSE ROLL (BATTLEFIELD)", "MOUSE YAW (ARMA)", "JOYSTICK (CUSTOM)" };

        /// <summary>The practice bots' skill, by the value <see cref="DifficultyKey"/> stores.</summary>
        public static readonly string[] DifficultyNames = { "EASY", "CHALLENGING" };

        /// <summary>Set by <c>OptionsUi</c>: re-reads the options and applies them to the running game.</summary>
        public static Action ApplyHandler;

        /// <summary>Raised after options were saved and applied.</summary>
        public static event Action Applied;

        /// <summary>Saves pending writes and applies the options to the running game.</summary>
        public static void Apply()
        {
            PlayerPrefs.Save();
            ApplyHandler?.Invoke();
            Applied?.Invoke();
        }

        public static float GetFloat(string key, float fallback) => PlayerPrefs.GetFloat(key, fallback);
        public static bool GetBool(string key, bool fallback) => PlayerPrefs.GetInt(key, fallback ? 1 : 0) == 1;
        public static int GetInt(string key, int fallback) => PlayerPrefs.GetInt(key, fallback);
        public static void SetFloat(string key, float value) => PlayerPrefs.SetFloat(key, value);
        public static void SetBool(string key, bool value) => PlayerPrefs.SetInt(key, value ? 1 : 0);
        public static void SetInt(string key, int value) => PlayerPrefs.SetInt(key, value);
    }
}
