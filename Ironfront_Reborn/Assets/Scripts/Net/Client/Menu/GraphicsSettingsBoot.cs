#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Puts the player's graphics preset into effect before the first scene draws: the one they
    /// saved, or on a first launch the one their machine suits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing did this before 2026-10-02.</b> <c>MenuSettingsScreen</c> applied a preset only when
    /// Save was pressed, and Unity does not remember a quality level between runs, so every launch
    /// started on the Standalone default -- Unity's "Fantastic" until then -- whatever the player
    /// had picked. See <see cref="GraphicsPresetRules"/> for the presets themselves.
    /// </para>
    /// <para>
    /// <b>Not on a dedicated server, and not in the Editor</b>, where the level is the developer's
    /// and a change at Play would be written back into <c>QualitySettings.asset</c>.
    /// </para>
    /// </remarks>
    public static class GraphicsSettingsBoot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyAtStartup()
        {
            if (Application.isEditor || Application.isBatchMode) return;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            int level = MenuSettingsModel.LoadQuality(Recommended());
            QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
            QualitySettings.vSyncCount = PlayerPrefs.GetInt(MenuSettingsModel.VSyncKey, 1) > 0 ? 1 : 0;

            Debug.Log($"[graphics] preset {QualitySettings.names[level]} "
                      + $"({(PlayerPrefs.HasKey(MenuSettingsModel.QualityKey) ? "saved" : "recommended")}) "
                      + $"on {SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsMemorySize} MB, "
                      + $"{SystemInfo.processorCount} logical processors");
        }

        /// <summary>The preset this machine suits (<see cref="GraphicsPresetRules.Recommended"/>).</summary>
        public static int Recommended()
            => GraphicsPresetRules.Recommended(
                SystemInfo.graphicsMemorySize,
                GraphicsPresetRules.IsIntegrated(SystemInfo.graphicsDeviceName),
                SystemInfo.processorCount);
    }
}
