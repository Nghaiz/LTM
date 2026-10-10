#nullable enable

using System.Linq;
using System.Collections.Generic;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The practice screen: the map and every setting a multiplayer room is created with, plus the
    /// three only an offline match can choose (the player's side, vehicles, the respawn time).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner, 2026-10-08:</b> "bring the features multiplayer already has to practice; the
    /// settings a multiplayer room is created with are not in practice, everything there says IN
    /// DEVELOPMENT". The mode, victory rule, points, night-vision battery and bots behave exactly as
    /// on <see cref="MenuCreateRoomScreen"/>, through the same <see cref="RoomSettingsChoice"/> and
    /// <see cref="MenuBotSlider"/>; the placeholders with nothing behind them (AI difficulty, match
    /// time, weather, friendly fire) are gone rather than left promising something.
    /// </para>
    /// <para>
    /// <b>No server ceiling on the bots.</b> The slider is never handed a capacity, so it offers
    /// the protocol's 0 to <see cref="ProtocolConstants.MAX_BOTS"/>: this machine plays them all.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuPracticeScreen : MonoBehaviour
    {
        [SerializeField] private MenuScreenController? _controller;
        [SerializeField] private Dropdown? _mapDropdown;

        /// <summary>
        /// The map card's title, which follows the map dropdown as Create Room's does. Optional.
        /// </summary>
        [SerializeField] private Text? _mapPreviewTitle;

        /// <summary>The map card's picture, which follows the map and the mode (<see cref="MapPictureChoice"/>).</summary>
        [SerializeField] private Image? _mapPreviewArt;

        /// <summary>Every map by day and in Night Mode, in <see cref="MapPictureChoice"/>'s order. Filled by the menu builder.</summary>
        [SerializeField] private Sprite[]? _mapPictures;

        [Header("Room settings, as Create Room's")]
        [SerializeField] private Dropdown? _modeDropdown;
        [SerializeField] private Dropdown? _ruleDropdown;
        [SerializeField] private InputField? _pointsField;

        /// <summary>The night-vision battery in seconds; open only while Night Mode is chosen.</summary>
        [SerializeField] private InputField? _visionField;

        [SerializeField] private MenuBotSlider? _botSlider;

        [Header("Practice only")]
        [SerializeField] private Dropdown? _teamDropdown;
        [SerializeField] private Dropdown? _vehiclesDropdown;
        [SerializeField] private InputField? _respawnField;

        /// <summary>The deployment line: what START will play, in one sentence.</summary>
        [Header("Controls")]
        [SerializeField] private Text? _summaryText;
        [SerializeField] private Text? _errorText;
        [SerializeField] private Button? _startButton;

        private readonly List<string> _sceneNames = new List<string>();
        private readonly List<ushort> _mapIds = new List<ushort>();

        private void Awake()
        {
            _sceneNames.Clear();
            _sceneNames.AddRange(CurrentMapScenes());
            _mapIds.Clear();
            _mapIds.AddRange(MapCatalog.All.Select(entry => entry.Id));

            if (_mapDropdown != null)
            {
                _mapDropdown.ClearOptions();
                _mapDropdown.AddOptions(CurrentMapLabels().ToList());
                _mapDropdown.onValueChanged.AddListener(_ => OnMapChanged());
            }

            Fill(_modeDropdown, RoomSettingsChoice.Modes.Select(RoomSettingsChoice.ModeOption));
            Fill(_ruleDropdown, RoomSettingsChoice.Rules.Select(RoomSettingsChoice.RuleOption));
            Fill(_teamDropdown, PracticeChoice.TeamOptions);
            Fill(_vehiclesDropdown, PracticeChoice.VehicleOptions);

            if (_modeDropdown != null) _modeDropdown.onValueChanged.AddListener(_ => OnModeChanged());
            if (_ruleDropdown != null) _ruleDropdown.onValueChanged.AddListener(_ => OnRuleChanged());
            if (_pointsField != null) _pointsField.onValueChanged.AddListener(_ => Refresh());
            if (_visionField != null) _visionField.onValueChanged.AddListener(_ => Refresh());
            if (_teamDropdown != null) _teamDropdown.onValueChanged.AddListener(_ => Refresh());
            if (_vehiclesDropdown != null) _vehiclesDropdown.onValueChanged.AddListener(_ => Refresh());
            if (_respawnField != null)
            {
                _respawnField.onValueChanged.AddListener(_ => Refresh());
                if (_respawnField.text.Trim().Length == 0)
                    _respawnField.text = PracticeSettings.DefaultRespawnSeconds.ToString();
            }
            if (_botSlider != null) _botSlider.ValueChanged += _ => Refresh();

            if (_startButton != null)
                _startButton.onClick.AddListener(StartPracticeConfiguration);

            OnRuleChanged();
            OnModeChanged();
        }

        private static void Fill(Dropdown? dropdown, IEnumerable<string> options)
        {
            if (dropdown == null) return;
            dropdown.ClearOptions();
            dropdown.AddOptions(options.ToList());
            dropdown.value = 0;
        }

        internal static string[] CurrentMapLabels()
            => MapCatalog.All.Select(entry => entry.DisplayName).ToArray();

        internal static string[] CurrentMapScenes()
            => MapCatalog.All.Select(entry => entry.SceneName).ToArray();

        /// <summary>Night Mode opens the battery; every map has a night, so the map stays where it is.</summary>
        private void OnModeChanged()
        {
            bool nightMode = SelectedMode() == GameMode.Night;
            if (_visionField != null)
            {
                _visionField.interactable = nightMode;
                if (nightMode && _visionField.text.Trim().Length == 0)
                    _visionField.text = RoomRules.DefaultNightVisionSeconds.ToString();
                if (!nightMode) _visionField.text = string.Empty;
            }
            Refresh();
        }

        /// <summary>A map without Night Mode takes the screen back to Point Match.</summary>
        private void OnMapChanged()
        {
            if (SelectedMode() == GameMode.Night
                && !RoomRules.ModeAllowedOn(GameMode.Night, SelectedMapId()) && _modeDropdown != null)
            {
                _modeDropdown.value = System.Array.IndexOf(RoomSettingsChoice.Modes, GameMode.PointMatch);
            }
            Refresh();
        }

        private void OnRuleChanged()
        {
            VictoryRule rule = SelectedRule();
            if (_pointsField != null)
            {
                _pointsField.text = RoomSettingsChoice.PointsAfterRuleChange(_pointsField.text, rule);
                if (_pointsField.placeholder is Text placeholder)
                    placeholder.text = RoomSettingsChoice.PointsPlaceholder(rule);
            }
            Refresh();
        }

        /// <summary>Redraws the map title and the deployment line, and clears a stale error.</summary>
        private void Refresh()
        {
            MapPictureChoice.Show(_mapPreviewArt, _mapPictures, SelectedMapId(), SelectedMode());
            if (_mapPreviewTitle != null && _mapDropdown != null)
            {
                int count = _mapDropdown.options.Count;
                _mapPreviewTitle.text = count == 0
                    ? string.Empty
                    : RoomSettingsChoice.MapTitle(
                        _mapDropdown.options[Mathf.Clamp(_mapDropdown.value, 0, count - 1)].text, SelectedMode());
            }

            bool valid = TryRead(out PracticeSettings settings, out string error);
            if (_summaryText != null) _summaryText.text = valid ? PracticeChoice.Summary(in settings) : "--";
            if (_errorText != null) _errorText.text = valid ? string.Empty : error;
        }

        private bool TryRead(out PracticeSettings settings, out string error)
            => PracticeChoice.TryRead(
                SelectedMode(), SelectedRule(),
                _pointsField != null ? _pointsField.text : string.Empty,
                _visionField != null ? _visionField.text : string.Empty,
                SelectedMapId(),
                _botSlider != null ? _botSlider.Value : PracticeSettings.DefaultBots,
                _teamDropdown != null ? _teamDropdown.value : 0,
                _vehiclesDropdown != null ? _vehiclesDropdown.value : 0,
                _respawnField != null ? _respawnField.text : string.Empty,
                out settings, out error);

        private GameMode SelectedMode()
        {
            int index = _modeDropdown != null ? _modeDropdown.value : 0;
            return index >= 0 && index < RoomSettingsChoice.Modes.Length ? RoomSettingsChoice.Modes[index] : GameMode.PointMatch;
        }

        private VictoryRule SelectedRule()
        {
            int index = _ruleDropdown != null ? _ruleDropdown.value : 0;
            return index >= 0 && index < RoomSettingsChoice.Rules.Length ? RoomSettingsChoice.Rules[index] : VictoryRule.Margin;
        }

        private ushort SelectedMapId()
        {
            if (_mapDropdown == null) return MapCatalog.DefaultMapId;
            int index = _mapDropdown.value;
            return index >= 0 && index < _mapIds.Count ? _mapIds[index] : MapCatalog.DefaultMapId;
        }

        private void StartPracticeConfiguration()
        {
            if (_mapDropdown == null || _sceneNames.Count == 0) return;
            if (!TryRead(out PracticeSettings settings, out string error))
            {
                if (_errorText != null) _errorText.text = error;
                return;
            }

            int index = Mathf.Clamp(_mapDropdown.value, 0, _sceneNames.Count - 1);
            _controller?.LaunchPracticeMap(_sceneNames[index], in settings);
        }
    }
}
