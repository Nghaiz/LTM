#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    [DisallowMultipleComponent]
    public sealed class MenuSettingsScreen : MonoBehaviour
    {
        [Header("Implemented controls")]
        [SerializeField] private Dropdown? _resolution;
        [SerializeField] private Dropdown? _displayMode;
        [SerializeField] private Dropdown? _quality;
        [SerializeField] private Toggle? _vSync;
        [SerializeField] private Slider? _masterVolume;
        [SerializeField] private Slider? _fieldOfView;
        [SerializeField] private Slider? _sensitivity;
        [SerializeField] private Button? _saveButton;
        [SerializeField] private Button? _resetButton;

        [Header("Deferred controls")]
        [SerializeField] private Button[] _unsupportedButtons = System.Array.Empty<Button>();
        [SerializeField] private MenuToast? _toast;

        private readonly List<DisplayResolutionOption> _resolutionOptions =
            new List<DisplayResolutionOption>();
        private bool _wired;

        public void Configure(Dropdown resolution, Dropdown displayMode, Dropdown quality,
            Toggle vSync, Slider masterVolume, Slider fieldOfView, Slider sensitivity)
        {
            _resolution = resolution;
            _displayMode = displayMode;
            _quality = quality;
            _vSync = vSync;
            _masterVolume = masterVolume;
            _fieldOfView = fieldOfView;
            _sensitivity = sensitivity;
            ConfigureRanges();
        }

        private void Awake()
        {
            ConfigureRanges();
            WireButtons();
        }

        private void OnEnable()
        {
            if (_resolutionOptions.Count == 0) SetResolutionOptions(DetectedResolutions());
            LoadIntoControls();
        }

        public void SetResolutionOptions(IEnumerable<DisplayResolutionOption> options)
        {
            _resolutionOptions.Clear();
            _resolutionOptions.AddRange(DisplayResolutionCatalog.Build(options));
            if (_resolution == null) return;
            _resolution.ClearOptions();
            var labels = new List<string>(_resolutionOptions.Count);
            foreach (DisplayResolutionOption option in _resolutionOptions) labels.Add(option.Label);
            _resolution.AddOptions(labels);
        }

        public void Save(bool applyRuntime = true)
        {
            if (_resolutionOptions.Count == 0) return;
            int resolutionIndex = Mathf.Clamp(_resolution != null ? _resolution.value : 0,
                0, _resolutionOptions.Count - 1);
            DisplayResolutionOption selected = _resolutionOptions[resolutionIndex];
            var data = new MenuSettingsData(
                selected.Width,
                selected.Height,
                _displayMode != null ? _displayMode.value : 0,
                _quality != null ? _quality.value : QualitySettings.GetQualityLevel(),
                _vSync != null && _vSync.isOn ? 1 : 0,
                _masterVolume != null ? _masterVolume.value : 1f,
                _fieldOfView != null ? _fieldOfView.value : 90f,
                _sensitivity != null ? _sensitivity.value : 0.5f);
            MenuSettingsModel.Save(data);
            if (applyRuntime) ApplyRuntime(data);
        }

        public void ResetToDefaults()
        {
            var defaults = new MenuSettingsData(
                Screen.currentResolution.width,
                Screen.currentResolution.height,
                1,
                Mathf.Max(0, QualitySettings.names.Length - 1),
                1,
                1f,
                90f,
                0.5f);
            PutIntoControls(defaults);
        }

        private void LoadIntoControls()
        {
            var fallback = new MenuSettingsData(
                Screen.width,
                Screen.height,
                Screen.fullScreen ? 1 : 0,
                QualitySettings.GetQualityLevel(),
                QualitySettings.vSyncCount > 0 ? 1 : 0,
                PlayerPrefs.GetFloat(MenuSettingsModel.MasterVolumeKey, 1f),
                PlayerPrefs.GetFloat(MenuSettingsModel.FieldOfViewKey, 90f),
                PlayerPrefs.GetFloat(MenuSettingsModel.SensitivityKey, 0.5f));
            PutIntoControls(MenuSettingsModel.Load(fallback));
        }

        private void PutIntoControls(MenuSettingsData data)
        {
            if (_resolution != null)
                _resolution.value = DisplayResolutionCatalog.FindBestIndex(
                    _resolutionOptions, data.ResolutionWidth, data.ResolutionHeight);
            if (_displayMode != null) _displayMode.value = Mathf.Clamp(data.DisplayMode, 0, 2);
            if (_quality != null)
                _quality.value = Mathf.Clamp(data.Quality, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
            if (_vSync != null) _vSync.isOn = data.VSync > 0;
            if (_masterVolume != null) _masterVolume.value = data.MasterVolume;
            if (_fieldOfView != null) _fieldOfView.value = data.FieldOfView;
            if (_sensitivity != null) _sensitivity.value = data.Sensitivity;
        }

        private void ConfigureRanges()
        {
            EnsureOptions(_displayMode, new[] { "WINDOWED", "BORDERLESS", "FULLSCREEN" });
            EnsureOptions(_quality, QualitySettings.names);
            if (_masterVolume != null) { _masterVolume.minValue = 0f; _masterVolume.maxValue = 1f; }
            if (_fieldOfView != null) { _fieldOfView.minValue = 60f; _fieldOfView.maxValue = 120f; }
            if (_sensitivity != null) { _sensitivity.minValue = 0.05f; _sensitivity.maxValue = 1f; }
        }

        private static void EnsureOptions(Dropdown? dropdown, IEnumerable<string> labels)
        {
            if (dropdown == null || dropdown.options.Count > 0) return;
            dropdown.AddOptions(new List<string>(labels));
        }

        private void WireButtons()
        {
            if (_wired) return;
            _wired = true;
            if (_saveButton != null) _saveButton.onClick.AddListener(() => Save());
            if (_resetButton != null) _resetButton.onClick.AddListener(ResetToDefaults);
            foreach (Button button in _unsupportedButtons)
                if (button != null) button.onClick.AddListener(ShowDevelopment);
        }

        private void ShowDevelopment() => _toast?.ShowDevelopment();

        private static IEnumerable<DisplayResolutionOption> DetectedResolutions()
        {
            foreach (Resolution resolution in Screen.resolutions)
                yield return new DisplayResolutionOption(resolution.width, resolution.height);
            yield return new DisplayResolutionOption(Screen.width, Screen.height);
        }

        private static void ApplyRuntime(MenuSettingsData data)
        {
            FullScreenMode mode = data.DisplayMode switch
            {
                2 => FullScreenMode.ExclusiveFullScreen,
                1 => FullScreenMode.FullScreenWindow,
                _ => FullScreenMode.Windowed,
            };
            Screen.SetResolution(data.ResolutionWidth, data.ResolutionHeight, mode);
            QualitySettings.SetQualityLevel(data.Quality, applyExpensiveChanges: true);
            QualitySettings.vSyncCount = data.VSync;
            AudioListener.volume = data.MasterVolume;
        }
    }
}
