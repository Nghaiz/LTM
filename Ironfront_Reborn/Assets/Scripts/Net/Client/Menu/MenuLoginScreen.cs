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

        /// <summary>
        /// The colour the label was authored with. Every failure goes back to it.
        /// </summary>
        /// <remarks>
        /// Captured rather than hardcoded, so the error colour is decided once — where the label
        /// is authored — instead of in two places that can disagree about what "error" looks like.
        /// </remarks>
        private Color _errorColour = Color.red;

        /// <summary>
        /// What a confirmation is drawn in, as opposed to a refusal.
        /// </summary>
        /// <remarks>
        /// <b>The same label, a different colour — not a second surface.</b> 3.2 constraint 4
        /// forbids a rival ERROR surface, and this is not one: there is still exactly one line
        /// that speaks to the player and exactly one error vocabulary behind it
        /// (<c>MasterErrorText</c>). What it forbids instead is the thing observed on the first
        /// run of this screen — "Account 'p15pilot' created" rendered in the failure colour,
        /// which tells the player in red that the thing that just worked did not.
        /// </remarks>
        private static readonly Color NoticeColour = new Color(0.62f, 0.82f, 0.66f);

        private void Awake()
        {
            if (_errorText != null) _errorColour = _errorText.color;

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
            if (_errorText == null) return;

            _errorText.color = _errorColour;
            _errorText.text = message;
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

            if (_errorText == null) return;

            _errorText.color = NoticeColour;
            _errorText.text = $"Account '{username}' created. Log in with it.";
        }
    }
}
