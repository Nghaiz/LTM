#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Username, password, and the line that says why the master said no. P15 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The error label is criterion 3.</b> "A wrong password renders a clear error on screen"
    /// is graded on the pixels, and the sentence it renders comes from <c>MasterErrorText</c> by
    /// way of <c>MasterSession.OnError</c> — this screen phrases nothing itself. That is
    /// constraint 4: one error vocabulary, already written, already covering every code in
    /// protocol-spec.md § 13.
    /// </para>
    /// <para>
    /// <b>The password is dropped as soon as the request is made</b>, the same way
    /// <c>LobbyShellOverlay.LoginAsync</c> drops it. A managed string cannot be wiped, but
    /// clearing the field drops the only reference this screen holds and stops the value being
    /// live in a memory dump, in a crash report, and in a text field that redraws it for as long
    /// as the login screen is reachable.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuLoginScreen : MenuFormScreen
    {
        [SerializeField] private MenuScreenController? _controller;
        [SerializeField] private InputField? _usernameField;
        [SerializeField] private InputField? _passwordField;
        [SerializeField] private Button? _logInButton;
        [SerializeField] private Button? _createAccountButton;
        [SerializeField] private Text? _errorText;

        private void Awake()
        {
            if (_logInButton != null) _logInButton.onClick.AddListener(OnLogIn);
            if (_createAccountButton != null) _createAccountButton.onClick.AddListener(OnCreateAccount);
        }

        private void OnLogIn()
        {
            if (_controller == null) return;

            string username = _usernameField != null ? _usernameField.text : string.Empty;
            string password = _passwordField != null ? _passwordField.text : string.Empty;

            if (username.Length == 0 || password.Length == 0)
            {
                SetError("Enter a username and a password.");
                return;
            }

            _controller.SubmitLogin(username, password);

            // Dropped now rather than on success: a failed attempt is exactly the case where the
            // value would otherwise sit in the field for the rest of the session.
            if (_passwordField != null) _passwordField.text = string.Empty;
        }

        private void OnCreateAccount() => _controller?.ShowRegister();

        /// <inheritdoc />
        public override void SetError(string message)
        {
            if (_errorText != null) _errorText.text = message;
        }

        /// <inheritdoc />
        public override void OnControllerStateChanged(MenuScreenController controller)
        {
            if (_logInButton != null) _logInButton.interactable = !controller.IsBusy;
            if (_createAccountButton != null) _createAccountButton.interactable = !controller.IsBusy;
        }

        /// <summary>
        /// Pre-fills the username the player just registered. 3.1's recorded answer.
        /// </summary>
        /// <remarks>
        /// The password is deliberately NOT carried over from the register form. Coming back to a
        /// form that is one keystroke from submitting is the point — coming back to one that is
        /// already filled in would make the register screen a login screen with extra steps, and
        /// would keep the password alive in a second field.
        /// </remarks>
        public override void OnAccountCreated(string username)
        {
            if (_usernameField != null) _usernameField.text = username;
            SetError($"Account '{username}' created. Log in with it.");
        }
    }
}
