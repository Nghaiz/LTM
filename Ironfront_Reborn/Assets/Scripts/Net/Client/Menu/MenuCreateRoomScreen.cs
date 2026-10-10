#nullable enable

using System.Collections.Generic;
using Ironfront.MasterClient;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The form that makes a room. P16 3.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first caller <c>RoomCreate</c> has ever had from the game.</b> Its fields are
    /// exactly <c>CreateRoomRequest</c>'s, so nothing is invented here and nothing is left
    /// unsendable -- the game mode, victory rule and points included since protocol 14.
    /// </para>
    /// <para>
    /// <b>The map list is <c>MapCatalog</c>, not a typed id.</b> The maps that ship are its rows,
    /// and a free-text id would let a player advertise a map no game server declares,
    /// which surfaces much later as <c>NoGameServerAvailable</c> and reads as the master being
    /// down.
    /// </para>
    /// <para>
    /// <b>Bots are a slider of the match's total, bounded by the servers</b> (protocol 13). The
    /// ceiling comes with the room list the browser holds, re-fetched when this form opens
    /// (<c>MenuScreenController.ShowCreateRoom</c>), and <see cref="MenuBotSlider"/> will not
    /// go past it; <see cref="MenuHostCapacityCard"/> says why.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuCreateRoomScreen : MenuFormScreen
    {
        /// <summary>
        /// The seat count a fresh form offers.
        /// </summary>
        /// <remarks>
        /// Even, and small enough that two people can fill it: the two-machine run in criterion
        /// 2 has to reach P14's start rule, and a default of sixteen would leave the pair
        /// staring at a room that never counts down.
        /// </remarks>
        public const byte DefaultMaxPlayers = 8;

        [SerializeField] private MenuScreenController? _controller;

        [Header("Fields")]
        [SerializeField] private InputField? _nameField;
        [SerializeField] private Dropdown? _mapDropdown;
        [SerializeField] private InputField? _maxPlayersField;
        [SerializeField] private MenuBotSlider? _botSlider;
        [SerializeField] private MenuHostCapacityCard? _capacityCard;
        [SerializeField] private Toggle? _privateToggle;
        [SerializeField] private InputField? _passwordField;

        /// <summary>The game mode, victory rule and points (phase P32): <see cref="RoomSettingsChoice"/>.</summary>
        [SerializeField] private Dropdown? _modeDropdown;
        [SerializeField] private Dropdown? _ruleDropdown;
        [SerializeField] private InputField? _pointsField;

        /// <summary>The night-vision battery in seconds; open only while Night Mode is chosen.</summary>
        [SerializeField] private InputField? _visionField;

        [Header("Controls")]
        [SerializeField] private Button? _createButton;
        [SerializeField] private Button? _backButton;
        [SerializeField] private Text? _errorText;

        /// <summary>
        /// The map-preview card's title, which follows the map dropdown.
        /// </summary>
        /// <remarks>
        /// The prototype hard-codes a map name in mock data. A card naming a map the player had not
        /// chosen would be worse than no card at all, so this is read from the selected option
        /// rather than written once by the authoring tool. Optional: a scene without the card
        /// simply leaves it null.
        /// </remarks>
        [SerializeField] private Text? _mapPreviewTitle;
        [SerializeField] private Text? _mapPreviewCapacity;
        [SerializeField] private Text? _mapPreviewBots;
        [SerializeField] private Text? _mapPreviewSecurity;

        /// <summary>The preview card's victory line: "LEAD BY 200", "FIRST TO 500".</summary>
        [SerializeField] private Text? _rulePreview;

        /// <summary>
        /// The map ids behind the dropdown, in its own option order.
        /// </summary>
        /// <remarks>
        /// Held rather than re-derived from the option LABEL, which would make the wire value
        /// depend on a display string and break the moment a map is renamed for players.
        /// </remarks>
        private readonly List<ushort> _mapIds = new List<ushort>();

        /// <summary>The capacity last handed to the slider, so an unchanged answer is not re-applied every frame.</summary>
        private RoomCapacity? _shownCapacity;
        private bool _hasShownCapacity;

        private void Awake()
        {
            PopulateMaps();
            PopulateModesAndRules();

            if (_createButton != null) _createButton.onClick.AddListener(OnCreate);
            if (_backButton != null) _backButton.onClick.AddListener(OnBack);
            if (_privateToggle != null) _privateToggle.onValueChanged.AddListener(OnPrivateChanged);
            if (_mapDropdown != null) _mapDropdown.onValueChanged.AddListener(_ => OnMapChanged());
            if (_maxPlayersField != null) _maxPlayersField.onValueChanged.AddListener(_ => RefreshPreviewStats());
            if (_botSlider != null) _botSlider.ValueChanged += _ => RefreshPreviewStats();
            if (_ruleDropdown != null) _ruleDropdown.onValueChanged.AddListener(_ => OnRuleChanged());
            if (_modeDropdown != null) _modeDropdown.onValueChanged.AddListener(_ => OnModeChanged());
            if (_visionField != null) _visionField.onValueChanged.AddListener(_ => RefreshPreviewStats());
            if (_pointsField != null) _pointsField.onValueChanged.AddListener(_ => RefreshPreviewStats());

            if (_maxPlayersField != null && _maxPlayersField.text.Length == 0)
                _maxPlayersField.text = DefaultMaxPlayers.ToString();

            OnPrivateChanged(_privateToggle != null && _privateToggle.isOn);
            OnModeChanged();
            RefreshMapPreview();
        }

        /// <summary>
        /// Night Mode opens the battery field with the default in it. Every map has a night, so
        /// the map stays where it is.
        /// </summary>
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
            RefreshMapPreview();
            RefreshPreviewStats();
        }

        /// <summary>A map without Night Mode takes the form back to Point Match.</summary>
        private void OnMapChanged()
        {
            if (SelectedMode() == GameMode.Night
                && !RoomRules.ModeAllowedOn(GameMode.Night, SelectedMapId()) && _modeDropdown != null)
            {
                _modeDropdown.value = System.Array.IndexOf(RoomSettingsChoice.Modes, GameMode.PointMatch);
            }
            RefreshMapPreview();
        }

        /// <summary>Points the map-preview card at the map the dropdown is showing.</summary>
        private void RefreshMapPreview()
        {
            if (_mapPreviewTitle == null || _mapDropdown == null) return;
            if (_mapDropdown.options.Count == 0)
            {
                _mapPreviewTitle.text = string.Empty;
                return;
            }

            int index = Mathf.Clamp(_mapDropdown.value, 0, _mapDropdown.options.Count - 1);
            _mapPreviewTitle.text = RoomSettingsChoice.MapTitle(_mapDropdown.options[index].text, SelectedMode());
            RefreshPreviewStats();
        }

        /// <summary>Fills the dropdown from <see cref="MapCatalog"/>, in catalogue order.</summary>
        private void PopulateMaps()
        {
            _mapIds.Clear();

            if (_mapDropdown == null) return;

            var labels = new List<string>();
            for (int i = 0; i < MapCatalog.All.Count; i++)
            {
                MapCatalog.MapEntry entry = MapCatalog.All[i];
                _mapIds.Add(entry.Id);
                labels.Add(entry.DisplayName);
            }

            _mapDropdown.ClearOptions();
            _mapDropdown.AddOptions(labels);
            _mapDropdown.value = 0;
        }

        /// <summary>Fills the mode and rule dropdowns from <see cref="RoomSettingsChoice"/>, defaults first.</summary>
        private void PopulateModesAndRules()
        {
            if (_modeDropdown != null)
            {
                var modes = new List<string>();
                foreach (GameMode mode in RoomSettingsChoice.Modes) modes.Add(RoomSettingsChoice.ModeOption(mode));
                _modeDropdown.ClearOptions();
                _modeDropdown.AddOptions(modes);
                _modeDropdown.value = 0;
            }

            if (_ruleDropdown != null)
            {
                var rules = new List<string>();
                foreach (VictoryRule rule in RoomSettingsChoice.Rules) rules.Add(RoomSettingsChoice.RuleOption(rule));
                _ruleDropdown.ClearOptions();
                _ruleDropdown.AddOptions(rules);
                _ruleDropdown.value = 0;
            }

            OnRuleChanged();
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
            RefreshPreviewStats();
        }

        private VictoryRule SelectedRule()
        {
            int index = _ruleDropdown != null ? _ruleDropdown.value : 0;
            return index >= 0 && index < RoomSettingsChoice.Rules.Length ? RoomSettingsChoice.Rules[index] : VictoryRule.Margin;
        }

        private GameMode SelectedMode()
        {
            int index = _modeDropdown != null ? _modeDropdown.value : 0;
            return index >= 0 && index < RoomSettingsChoice.Modes.Length ? RoomSettingsChoice.Modes[index] : GameMode.PointMatch;
        }

        private void OnPrivateChanged(bool isPrivate)
        {
            if (_passwordField != null) _passwordField.interactable = isPrivate;

            // Cleared when the toggle goes off, so a password typed and then un-ticked is not
            // sent with a room the player has just decided should be public.
            if (!isPrivate && _passwordField != null) _passwordField.text = string.Empty;
            RefreshPreviewStats();
        }

        private void RefreshPreviewStats()
        {
            if (_mapPreviewCapacity != null)
                _mapPreviewCapacity.text = PreviewValue(_maxPlayersField, DefaultMaxPlayers.ToString());
            if (_mapPreviewBots != null)
                _mapPreviewBots.text = RoomBotChoice.Preview(ChosenBots());
            if (_mapPreviewSecurity != null)
                _mapPreviewSecurity.text = _privateToggle != null && _privateToggle.isOn
                    ? "PRIVATE"
                    : "PUBLIC";
            if (_capacityCard != null)
                _capacityCard.Show(_shownCapacity, ChosenBots(), SelectedMapId(), SelectedMapName());
            if (_rulePreview != null)
                _rulePreview.text = RoomSettingsChoice.TryRead(
                        SelectedMode(), SelectedRule(), _pointsField != null ? _pointsField.text : string.Empty,
                        _visionField != null ? _visionField.text : string.Empty,
                        SelectedMapId(), out RoomSettings settings, out _)
                    ? RoomSettingsChoice.DescribeRule(in settings)
                    : "--";
        }

        // Not `?.`: a MenuBotSlider is a UnityEngine.Object, whose null test is the overloaded one.
        private int ChosenBots() => _botSlider != null ? _botSlider.Value : ProtocolConstants.DEFAULT_ROOM_BOTS;

        private string SelectedMapName()
        {
            if (_mapDropdown == null || _mapDropdown.options.Count == 0) return "This map";
            int index = Mathf.Clamp(_mapDropdown.value, 0, _mapDropdown.options.Count - 1);
            return _mapDropdown.options[index].text;
        }

        private static string PreviewValue(InputField? field, string fallback)
        {
            string value = field != null ? field.text.Trim() : string.Empty;
            return value.Length == 0 ? fallback : value;
        }

        private void OnBack() => _controller?.HideCreateRoom();

        /// <summary>
        /// Validates the form and submits it, or says exactly what is wrong. P16 3.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><c>MaxPlayers</c> must be EVEN, and this is where that is enforced</b> (P16 3.3).
        /// P14 sizes the game server's slot pool from it and P13's team-keyed claim splits it in
        /// half, so an odd value gives one side an extra slot. <c>LobbyService.EvenSeats</c>
        /// rounds one down on arrival — which is correct as a last defence and wrong as the only
        /// one, because the room would then advertise a number the player chose and the server
        /// does not honour. Refusing here means the lobby never advertises a number it will not
        /// keep, and the player is told why rather than watching a 7 become a 6.
        /// </para>
        /// <para>
        /// The floor of 2 and the ceiling of <c>MAX_PLAYERS</c> mirror
        /// <c>LobbyService.CreateRoom</c>'s own bounds, which answer an out-of-range request with
        /// <c>InternalServerError</c> — a code that tells the player nothing they can act on.
        /// </para>
        /// </remarks>
        private void OnCreate()
        {
            if (_controller == null) return;

            string name = _nameField != null ? _nameField.text.Trim() : string.Empty;
            if (name.Length == 0)
            {
                SetError("Give the room a name.");
                return;
            }

            if (name.Length > 48)
            {
                SetError("Room names are 48 characters or fewer.");
                return;
            }

            if (!TryReadCount(_maxPlayersField, out int maxPlayers))
            {
                SetError("Players must be a number.");
                return;
            }

            if (maxPlayers < 2 || maxPlayers > ProtocolConstants.MAX_PLAYERS)
            {
                SetError($"Players must be between 2 and {ProtocolConstants.MAX_PLAYERS}.");
                return;
            }

            if (maxPlayers % 2 != 0)
            {
                SetError("Players must be an even number, so the two sides get the same "
                         + "number of slots.");
                return;
            }

            // The match's total, which the slider has already held under the servers' ceiling;
            // asked again here because the ceiling can have moved since it was drawn. Not capped
            // by the seat count: bots do not take a player's seat.
            int botCount = ChosenBots();
            if (!RoomBotChoice.IsAllowed(botCount, _shownCapacity))
            {
                SetError(RoomBotChoice.CeilingText(_shownCapacity));
                return;
            }

            bool isPrivate = _privateToggle != null && _privateToggle.isOn;
            string password = _passwordField != null ? _passwordField.text : string.Empty;

            if (isPrivate && password.Length == 0)
            {
                SetError("A private room needs a password, or nobody can join it.");
                return;
            }

            ushort mapId = SelectedMapId();

            if (!RoomSettingsChoice.TryRead(
                    SelectedMode(), SelectedRule(), _pointsField != null ? _pointsField.text : string.Empty,
                    _visionField != null ? _visionField.text : string.Empty,
                    mapId, out RoomSettings settings, out string settingsError))
            {
                SetError(settingsError);
                return;
            }

            _controller.SubmitCreateRoom(
                name, mapId, (byte)maxPlayers, (byte)botCount, isPrivate ? password : null, settings);
        }

        private ushort SelectedMapId()
        {
            if (_mapDropdown == null) return MapCatalog.DefaultMapId;

            int index = _mapDropdown.value;
            return index >= 0 && index < _mapIds.Count ? _mapIds[index] : MapCatalog.DefaultMapId;
        }

        /// <summary>An empty field reads as 0, not as an error; a non-number is an error.</summary>
        private static bool TryReadCount(InputField? field, out int value)
        {
            string text = field != null ? field.text.Trim() : string.Empty;
            if (text.Length == 0)
            {
                value = 0;
                return true;
            }

            return int.TryParse(text, out value);
        }

        public override void SetError(string message)
        {
            if (_errorText != null) _errorText.text = message;
        }

        public override void OnControllerStateChanged(MenuScreenController controller)
        {
            RoomCapacity? capacity = controller.Capacity;
            if (!_hasShownCapacity || !ReferenceEquals(capacity, _shownCapacity))
            {
                _shownCapacity = capacity;
                _hasShownCapacity = true;
                if (_botSlider != null) _botSlider.SetCapacity(capacity);
                RefreshPreviewStats();
            }

            if (_botSlider != null) _botSlider.SetInteractable(!controller.IsBusy);
            if (_createButton != null)
                _createButton.interactable = !controller.IsBusy && RoomBotChoice.CanCreate(capacity);
            if (_backButton != null) _backButton.interactable = !controller.IsBusy;
        }
    }
}
