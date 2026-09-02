#nullable enable

using System.Collections.Generic;
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
    /// <b>The first caller <c>RoomCreate</c> has ever had from the game.</b> Its six fields are
    /// exactly <c>CreateRoomRequest</c>'s, so nothing is invented here and nothing is left
    /// unsendable.
    /// </para>
    /// <para>
    /// <b>The map list is <c>MapCatalog</c>, not a typed id.</b> Two maps ship — Dustbowl and
    /// Island — and a free-text id would let a player advertise a map no game server declares,
    /// which surfaces much later as <c>NoGameServerAvailable</c> and reads as the master being
    /// down.
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
        [SerializeField] private InputField? _botCountField;
        [SerializeField] private Toggle? _privateToggle;
        [SerializeField] private InputField? _passwordField;

        [Header("Controls")]
        [SerializeField] private Button? _createButton;
        [SerializeField] private Button? _backButton;
        [SerializeField] private Text? _errorText;

        /// <summary>
        /// The map ids behind the dropdown, in its own option order.
        /// </summary>
        /// <remarks>
        /// Held rather than re-derived from the option LABEL, which would make the wire value
        /// depend on a display string and break the moment a map is renamed for players.
        /// </remarks>
        private readonly List<ushort> _mapIds = new List<ushort>();

        private void Awake()
        {
            PopulateMaps();

            if (_createButton != null) _createButton.onClick.AddListener(OnCreate);
            if (_backButton != null) _backButton.onClick.AddListener(OnBack);
            if (_privateToggle != null) _privateToggle.onValueChanged.AddListener(OnPrivateChanged);

            if (_maxPlayersField != null && _maxPlayersField.text.Length == 0)
                _maxPlayersField.text = DefaultMaxPlayers.ToString();

            OnPrivateChanged(_privateToggle != null && _privateToggle.isOn);
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

        private void OnPrivateChanged(bool isPrivate)
        {
            if (_passwordField == null) return;

            _passwordField.interactable = isPrivate;

            // Cleared when the toggle goes off, so a password typed and then un-ticked is not
            // sent with a room the player has just decided should be public.
            if (!isPrivate) _passwordField.text = string.Empty;
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

            if (!TryReadCount(_botCountField, out int botCount) || botCount < 0 || botCount > maxPlayers)
            {
                SetError($"Bots must be a number between 0 and {maxPlayers}.");
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

            _controller.SubmitCreateRoom(
                name, mapId, (byte)maxPlayers, (byte)botCount, isPrivate ? password : null);
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
            if (_createButton != null) _createButton.interactable = !controller.IsBusy;
            if (_backButton != null) _backButton.interactable = !controller.IsBusy;
        }
    }
}
