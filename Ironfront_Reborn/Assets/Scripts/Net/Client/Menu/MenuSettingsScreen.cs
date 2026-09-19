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
        [SerializeField] private Text? _statusText;

        [Header("Categories")]
        [SerializeField] private Button[] _categoryButtons = System.Array.Empty<Button>();
        [SerializeField] private GameObject[] _categoryGroups = System.Array.Empty<GameObject>();

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

            // Wire here as well as in Awake, and refresh here rather than waiting for OnEnable.
            //
            // In Play the scene's serialized fields are already assigned when Awake runs, so Awake
            // wires and OnEnable loads. In an EDIT-MODE test neither lifecycle method runs at all:
            // Unity calls Awake and OnEnable for a component in edit mode only when the class is
            // [ExecuteAlways]. Without this call a test could assign a display mode and see the
            // resolution row stay enabled, and the wiring it was checking would be unreachable
            // rather than wrong. `WireButtons` guards on `_wired`, so the two paths cannot
            // double-subscribe.
            WireButtons();
            RefreshResolutionAvailability();
        }

        private void Awake()
        {
            ConfigureRanges();
            WireButtons();
            ShowCategory(0);
        }

        private void OnEnable()
        {
            if (_resolutionOptions.Count == 0) SetResolutionOptions(DetectedResolutions());
            LoadIntoControls();
            SetStatus("SETTINGS LOADED");
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
            SetStatus("ALL CHANGES SAVED");
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
            SetStatus("DEFAULTS RESTORED");
        }

        private void LoadIntoControls()
        {
            var fallback = new MenuSettingsData(
                Screen.width,
                Screen.height,
                DisplayModeIndexOf(Screen.fullScreenMode),
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

            // After the mode row, not before it: the row's availability is a consequence of the
            // mode, and it has to be refreshed explicitly because assigning a Dropdown the value it
            // already holds does not raise onValueChanged.
            RefreshResolutionAvailability();
        }

        /// <summary>
        /// Enables the resolution row only in the modes where it changes anything.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A row that silently ignores the player is worse than one that says no.</b> In
        /// borderless the resolution is the display's, so <see cref="ResolveApplication"/> discards
        /// whatever this row holds — and a player who picks a smaller value, sees no change and
        /// gets no explanation has found the same bug they reported, only quieter. Greyed out, the
        /// row says the choice exists and that this mode is not where it applies.
        /// </para>
        /// <para>
        /// Disabled rather than hidden: hiding it would make the screen change shape as the mode
        /// changes, and a player looking for the resolution they set yesterday would find it
        /// missing rather than inapplicable.
        /// </para>
        /// </remarks>
        private void RefreshResolutionAvailability()
        {
            if (_resolution == null) return;
            FullScreenMode mode = ModeFor(_displayMode != null ? _displayMode.value : 0);
            _resolution.interactable = mode != FullScreenMode.FullScreenWindow;
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
            if (_displayMode != null)
                _displayMode.onValueChanged.AddListener(_ =>
                {
                    RefreshResolutionAvailability();
                    MarkUnsaved();
                });
            if (_resolution != null) _resolution.onValueChanged.AddListener(_ => MarkUnsaved());
            if (_quality != null) _quality.onValueChanged.AddListener(_ => MarkUnsaved());
            if (_vSync != null) _vSync.onValueChanged.AddListener(_ => MarkUnsaved());
            if (_masterVolume != null) _masterVolume.onValueChanged.AddListener(_ => MarkUnsaved());
            if (_fieldOfView != null) _fieldOfView.onValueChanged.AddListener(_ => MarkUnsaved());
            if (_sensitivity != null) _sensitivity.onValueChanged.AddListener(_ => MarkUnsaved());
            for (int i = 0; i < _categoryButtons.Length; i++)
            {
                int category = i;
                Button button = _categoryButtons[i];
                if (button != null) button.onClick.AddListener(() => ShowCategory(category));
            }
            foreach (Button button in _unsupportedButtons)
                if (button != null) button.onClick.AddListener(ShowDevelopment);
        }

        private void ShowCategory(int category)
        {
            for (int i = 0; i < _categoryGroups.Length; i++)
                if (_categoryGroups[i] != null) _categoryGroups[i].SetActive(i == category);

            for (int i = 0; i < _categoryButtons.Length; i++)
                if (_categoryButtons[i] != null) _categoryButtons[i].interactable = i != category;
        }

        private void ShowDevelopment() => _toast?.ShowDevelopment();

        private void MarkUnsaved() => SetStatus("UNSAVED CHANGES");

        private void SetStatus(string value)
        {
            if (_statusText != null) _statusText.text = value;
        }

        private static IEnumerable<DisplayResolutionOption> DetectedResolutions()
        {
            foreach (Resolution resolution in Screen.resolutions)
                yield return new DisplayResolutionOption(resolution.width, resolution.height);
            yield return new DisplayResolutionOption(Screen.width, Screen.height);
        }

        /// <summary>
        /// The width, height and mode that <see cref="Screen.SetResolution"/> should be given for a
        /// settings row, given the display's current size.
        /// </summary>
        /// <remarks>
        /// A value rather than three <c>out</c> parameters so the test can assert on one object.
        /// </remarks>
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
        /// Decides what <paramref name="data"/> should be applied as, without applying it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The chosen width and height only mean something in two of the three modes.</b>
        /// Passing them in the third is what made the resolution row behave like a quality slider.
        /// </para>
        /// <para>
        /// In <see cref="FullScreenMode.FullScreenWindow"/> Unity renders the app at the size the
        /// script asks for and SCALES it to fill the window. The window cannot become
        /// <c>w</c>×<c>h</c> because it is already the monitor, so a smaller resolution shrank the
        /// back buffer while the picture stayed monitor-sized and the compositor upscaled it —
        /// which is the reported symptom exactly, and this project OPENS in that mode
        /// (<c>fullscreenMode: 1</c> in <c>ProjectSettings.asset</c>). Unity's own
        /// <c>SetResolution</c> documentation draws the line the same way: only
        /// <see cref="FullScreenMode.ExclusiveFullScreen"/> changes the display's resolution, and
        /// every other mode changes the application's back buffer.
        /// </para>
        /// <para>
        /// So the player's numbers are honoured in the two modes where they are the numbers they
        /// look like — <see cref="FullScreenMode.Windowed"/> resizes the window, and
        /// <see cref="FullScreenMode.ExclusiveFullScreen"/> genuinely changes the display mode —
        /// and borderless takes the display's own size, so the back buffer stays 1:1 with the
        /// window and nothing is upscaled.
        /// </para>
        /// <para>
        /// The display size is a parameter rather than a read of <see cref="Screen"/> inside, for
        /// the reason the rest of this file takes its DTO as an argument: it is the only way to
        /// test the decision without a display attached.
        /// </para>
        /// </remarks>
        public static DisplayApplication ResolveApplication(
            MenuSettingsData data, int displayWidth, int displayHeight)
        {
            FullScreenMode mode = ModeFor(data.DisplayMode);
            return mode == FullScreenMode.FullScreenWindow
                ? new DisplayApplication(displayWidth, displayHeight, mode)
                : new DisplayApplication(data.ResolutionWidth, data.ResolutionHeight, mode);
        }

        /// <summary>The Unity mode behind the display-mode row's index.</summary>
        public static FullScreenMode ModeFor(int displayModeIndex) => displayModeIndex switch
        {
            2 => FullScreenMode.ExclusiveFullScreen,
            1 => FullScreenMode.FullScreenWindow,
            _ => FullScreenMode.Windowed,
        };

        /// <summary>
        /// The row's index for the mode the game is actually running in.
        /// </summary>
        /// <remarks>
        /// <b><see cref="Screen.fullScreen"/> cannot answer this.</b> It is <see langword="true"/>
        /// for BOTH fullscreen modes, so a build running in
        /// <see cref="FullScreenMode.ExclusiveFullScreen"/> was read back as BORDERLESS — and
        /// because the two modes now do different things with the resolution row, believing the
        /// wrong one would mean the player's next Save silently discards their resolution.
        /// <see cref="Screen.fullScreenMode"/> is the value that distinguishes them.
        /// </remarks>
        public static int DisplayModeIndexOf(FullScreenMode mode) => mode switch
        {
            FullScreenMode.ExclusiveFullScreen => 2,
            FullScreenMode.FullScreenWindow => 1,
            _ => 0,
        };

        private static void ApplyRuntime(MenuSettingsData data)
        {
            DisplayApplication application = ResolveApplication(
                data, Screen.currentResolution.width, Screen.currentResolution.height);
            Screen.SetResolution(application.Width, application.Height, application.Mode);

            QualitySettings.SetQualityLevel(data.Quality, applyExpensiveChanges: true);
            QualitySettings.vSyncCount = data.VSync;
            AudioListener.volume = data.MasterVolume;
        }
    }
}
