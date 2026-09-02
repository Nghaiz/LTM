#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Creates an account, on a master that has never seen it. P15 3.1, criterion 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This screen is the reason the phase exists at all, in miniature.</b>
    /// <c>RegisterAsync</c> has been implemented and tested on the master since phase 02 with
    /// zero Unity callers — which is why <c>run-e2e.ps1</c> has to open a second account through
    /// a harness to make a room. Nothing here is new protocol; it is a form on top of a message
    /// that was already there.
    /// </para>
    /// <para>
    /// <b>The confirm field never leaves the machine.</b> The master has no second password to
    /// compare against, so the mismatch check is local by necessity —
    /// <c>MenuScreenController.SubmitRegister</c> does it before anything is hashed. Every
    /// failure the master itself reports still arrives through <c>MasterErrorText</c>, so
    /// "that username is already taken" and "usernames are 3-16 characters" are its words, not
    /// this screen's.
    /// </para>
    /// <para>
    /// <b>Display name is optional and is not defaulted here.</b> Left blank, the master applies
    /// its own rule. Substituting the username client-side would mean a master that decided
    /// otherwise and this client disagreeing about the player's own name, with the client's
    /// version being the one on screen.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuRegisterScreen : MenuFormScreen
    {
        [SerializeField] private MenuScreenController? _controller;
        [SerializeField] private InputField? _usernameField;
        [SerializeField] private InputField? _passwordField;
        [SerializeField] private InputField? _confirmPasswordField;
        [SerializeField] private InputField? _displayNameField;
        [SerializeField] private Button? _createButton;
        [SerializeField] private Button? _backButton;
        [SerializeField] private Text? _errorText;

        private void Awake()
        {
            if (_createButton != null) _createButton.onClick.AddListener(OnCreate);
            if (_backButton != null) _backButton.onClick.AddListener(OnBack);
        }

        private void OnCreate()
        {
            if (_controller == null) return;

            string username = _usernameField != null ? _usernameField.text : string.Empty;
            string password = _passwordField != null ? _passwordField.text : string.Empty;
            string confirm = _confirmPasswordField != null ? _confirmPasswordField.text : string.Empty;
            string displayName = _displayNameField != null ? _displayNameField.text : string.Empty;

            if (username.Length == 0 || password.Length == 0)
            {
                SetError("Enter a username and a password.");
                return;
            }

            _controller.SubmitRegister(username, password, confirm, displayName);
        }

        private void OnBack() => _controller?.ShowLogin();

        /// <inheritdoc />
        public override void SetError(string message)
        {
            if (_errorText != null) _errorText.text = message;
        }

        /// <inheritdoc />
        public override void OnControllerStateChanged(MenuScreenController controller)
        {
            if (_createButton != null) _createButton.interactable = !controller.IsBusy;
            if (_backButton != null) _backButton.interactable = !controller.IsBusy;
        }

        /// <summary>
        /// Clears both password fields once the account exists.
        /// </summary>
        /// <remarks>
        /// The controller has already switched back to the login form by the time this arrives,
        /// so this screen is down — which is exactly when it should be emptied, rather than on
        /// the frame it next comes up.
        /// </remarks>
        public override void OnAccountCreated(string username)
        {
            if (_passwordField != null) _passwordField.text = string.Empty;
            if (_confirmPasswordField != null) _confirmPasswordField.text = string.Empty;
        }
    }
}
