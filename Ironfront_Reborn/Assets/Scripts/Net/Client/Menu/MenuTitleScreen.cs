#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The first screen a player reaches. Multiplayer is the primary action. P15 3.5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Multiplayer leads and Practice is secondary, per the owner's decision.</b> That
    /// ordering is the whole subject of this phase's F1 finding: before it, the only route into
    /// multiplayer was <c>Shift+F2</c> and an <c>OnGUI</c> overlay, which is not a route a player
    /// has.
    /// </para>
    /// <para>
    /// <b>Practice is offered only when there is something to offer.</b>
    /// <c>IPracticeLauncher.IsAvailable</c> is false on a build whose Menu scene carries no
    /// legacy menu, and the button is then non-interactable rather than present-and-dead. A
    /// button that does nothing is worse than an absent one: the player retries it.
    /// </para>
    /// <para>
    /// <b>The buttons are wired in <c>Awake</c>, not by an authored persistent call.</b> A
    /// serialized <c>Button</c> reference plus <c>AddListener</c> survives a method rename, which
    /// an authored <c>m_OnClick</c> entry does not — it stores the method name and the assembly-
    /// qualified type name as strings and fails silently when either moves. It is also the shape
    /// the authoring detector can grade completely: everything that decides whether this button
    /// works is a field, and <c>MenuScreenWiringDetectors</c> reads exactly those fields.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuTitleScreen : MenuFormScreen
    {
        [SerializeField] private MenuScreenController? _controller;
        [SerializeField] private Button? _multiplayerButton;
        [SerializeField] private Button? _practiceButton;

        private void Awake()
        {
            if (_multiplayerButton != null)
                _multiplayerButton.onClick.AddListener(OnMultiplayer);

            if (_practiceButton != null)
                _practiceButton.onClick.AddListener(OnPractice);
        }

        private void OnMultiplayer() => _controller?.GoToMultiplayer();

        private void OnPractice() => _controller?.OpenPractice();

        /// <inheritdoc />
        public override void OnControllerStateChanged(MenuScreenController controller)
        {
            if (_multiplayerButton != null) _multiplayerButton.interactable = !controller.IsBusy;
            if (_practiceButton != null) _practiceButton.interactable = controller.IsPracticeAvailable;
        }
    }
}
