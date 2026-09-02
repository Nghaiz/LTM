#nullable enable

using UnityEngine;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// What <see cref="MenuScreenController"/> can say to any screen without knowing which one
    /// it is. P15 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An abstract base rather than an interface, and the reason is the null check.</b>
    /// <c>GetComponentsInChildren&lt;T&gt;</c> works with either, but a screen is a
    /// <c>MonoBehaviour</c> and Unity's overloaded <c>==</c> — the one that reports a destroyed
    /// object as null — is only honoured through a <c>UnityEngine.Object</c>-typed reference.
    /// Held as an interface, a destroyed screen compares non-null and then throws on first use,
    /// which is a bug that only appears when a scene unloads mid-frame.
    /// </para>
    /// <para>
    /// <b>Three members, all of them things the controller genuinely has to say.</b> The error
    /// line (constraint 4's single surface), the enable/disable pass that
    /// <see cref="MenuScreenController.IsBusy"/> drives, and the one message with a payload —
    /// an account was created, here is the username to pre-fill. A screen that cares about none
    /// of them overrides nothing.
    /// </para>
    /// </remarks>
    public abstract class MenuFormScreen : MonoBehaviour
    {
        /// <summary>
        /// Renders <paramref name="message"/>, or clears the line when it is empty.
        /// </summary>
        /// <remarks>
        /// Called on every screen rather than only the visible one. A screen that is down still
        /// clears its label, so a stale error from the previous visit is not what the player sees
        /// on the frame it comes back up.
        /// </remarks>
        public virtual void SetError(string message) { }

        /// <summary>
        /// The controller's busy flag or bindings changed; re-evaluate interactability.
        /// </summary>
        public virtual void OnControllerStateChanged(MenuScreenController controller) { }

        /// <summary>
        /// An account was just created. The login form pre-fills the username; everything else
        /// ignores it.
        /// </summary>
        public virtual void OnAccountCreated(string username) { }
    }
}
