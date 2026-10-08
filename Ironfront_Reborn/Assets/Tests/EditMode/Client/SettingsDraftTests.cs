#nullable enable

using Ironfront.Net.Unity.Client.Menu;
using Ironfront.Net.Unity.Client.Overlay;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The settings screen's draft (owner's list of 2026-10-09, items 1 and 3): one screen for the
    /// menu and the match, writing the keys the game already reads.
    /// </summary>
    public sealed class SettingsDraftTests
    {
        private static readonly string[] Keys =
        {
            MenuSettingsModel.ResolutionWidthKey, MenuSettingsModel.ResolutionHeightKey, MenuSettingsModel.FullscreenModeKey,
            MenuSettingsModel.QualityKey, MenuSettingsModel.VSyncKey, MenuSettingsModel.FpsLimitKey,
            GameOptionsStore.MasterVolumeKey, GameOptionsStore.FieldOfViewKey, GameOptionsStore.MouseSensitivityKey,
            GameOptionsStore.ScopeMultiplierKey, GameOptionsStore.InvertMouseKey, GameOptionsStore.ToggleAimKey,
            GameOptionsStore.ToggleCrouchKey, GameOptionsStore.AutoReloadKey, GameOptionsStore.HitIndicatorsKey,
            GameOptionsStore.DifficultyKey, GameOptionsStore.HelicopterStyleKey, GameOptionsStore.HelicopterSensitivityKey,
            GameOptionsStore.HelicopterInvertPitchKey, GameOptionsStore.HelicopterInvertYawKey,
            GameOptionsStore.HelicopterInvertRollKey, GameOptionsStore.HelicopterInvertThrottleKey,
            VegetationRules.DensityKey, VegetationRules.DistanceKey,
        };

        [TearDown]
        public void TearDown()
        {
            foreach (string key in Keys) PlayerPrefs.DeleteKey(key);
        }

        [Test]
        public void WhatTheScreenWritesIsWhatTheGameReads()
        {
            SettingsDraft draft = SettingsDraft.Load();
            draft.MouseSensitivity = 0.37f;
            draft.ScopeMultiplier = 0.22f;
            draft.FieldOfView = 101f;
            draft.MasterVolume = 0.4f;
            draft.ToggleCrouch = true;
            draft.HitIndicators = false;
            draft.HelicopterStyle = 0;
            draft.VegetationDensity = 0.5f;
            draft.Write();

            // The keys OptionsUi.Options.Load reads, by GameOptionsStore's constants.
            Assert.AreEqual(0.37f, PlayerPrefs.GetFloat(GameOptionsStore.MouseSensitivityKey), 1e-4f);
            Assert.AreEqual(0.22f, PlayerPrefs.GetFloat(GameOptionsStore.ScopeMultiplierKey), 1e-4f);
            Assert.AreEqual(101f, PlayerPrefs.GetFloat(GameOptionsStore.FieldOfViewKey), 1e-4f);
            Assert.AreEqual(0.4f, PlayerPrefs.GetFloat(GameOptionsStore.MasterVolumeKey), 1e-4f);
            Assert.AreEqual(1, PlayerPrefs.GetInt(GameOptionsStore.ToggleCrouchKey));
            Assert.AreEqual(0, PlayerPrefs.GetInt(GameOptionsStore.HitIndicatorsKey));
            Assert.AreEqual(0, PlayerPrefs.GetInt(GameOptionsStore.HelicopterStyleKey));
            Assert.AreEqual(0.5f, PlayerPrefs.GetFloat(VegetationRules.DensityKey), 1e-4f);

            SettingsDraft reloaded = SettingsDraft.Load();
            Assert.IsFalse(reloaded.DiffersFrom(draft), "Loading what was written must give the same draft back.");
        }

        [Test]
        public void ResettingATabTouchesOnlyThatTab()
        {
            SettingsDraft draft = SettingsDraft.Load();
            draft.MouseSensitivity = 0.9f;
            draft.MasterVolume = 0.2f;

            draft.ResetToDefaults(SettingsCategory.Gameplay);

            Assert.AreEqual(GameOptionsStore.DefaultMouseSensitivity, draft.MouseSensitivity, 1e-4f);
            Assert.AreEqual(0.2f, draft.MasterVolume, 1e-4f, "RESET TAB on gameplay must not reset the audio.");
        }

        [Test]
        public void AKeyChangeIsAnUnsavedChangeAndACloneDoesNotShareKeys()
        {
            SettingsDraft saved = SettingsDraft.Load();
            SettingsDraft draft = saved.Clone();
            Assert.IsFalse(draft.DiffersFrom(saved));

            draft.Keys.Bind(GameAction.Jump, 0, KeyCode.Mouse4);

            Assert.IsTrue(draft.DiffersFrom(saved), "A rebound key must count as unsaved, or leaving would lose it silently.");
            Assert.AreNotEqual(KeyCode.Mouse4, saved.Keys.Get(GameAction.Jump, 0), "The clone must own its key set.");
        }
    }
}
