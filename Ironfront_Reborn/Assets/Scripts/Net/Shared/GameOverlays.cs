using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>The full-screen pages a player opens over the menu or a match.</summary>
    public enum OverlayPage : byte
    {
        None,
        Settings,
        HowToPlay,
        Achievements,
        Ranking,
    }

    /// <summary>
    /// Opens and closes the overlay pages from anywhere: the main menu's buttons, the pause menu
    /// and the deploy screen in a match. The client's overlay host installs the handlers; this
    /// assembly cannot name it, and the legacy game code (<c>IngameMenuUi</c>) reaches it through here.
    /// </summary>
    public static class GameOverlays
    {
        /// <summary>Opens <see cref="OverlayPage"/>; installed by the overlay host.</summary>
        public static Action<OverlayPage> Opener;

        /// <summary>Closes whatever is open; installed by the overlay host.</summary>
        public static Action Closer;

        /// <summary>The page on screen, or <see cref="OverlayPage.None"/>; installed by the overlay host.</summary>
        public static Func<OverlayPage> Showing;

        /// <summary>Which pages exist in this build, so a menu offers no button for one that does not.</summary>
        public static Func<OverlayPage, bool> Available;

        /// <summary>The frame an overlay last used the Escape key, so a menu reading it that frame leaves it alone.</summary>
        public static int EscapeConsumedFrame = -1;

        public static OverlayPage Current => Showing != null ? Showing() : OverlayPage.None;

        public static bool IsOpen => Current != OverlayPage.None;

        public static bool IsAvailable(OverlayPage page) => Available != null && Available(page);

        /// <summary>
        /// Whether Escape belongs to an overlay this frame: one is open, or one just closed on it.
        /// The pause menu and the menu screens ask before acting on Escape.
        /// </summary>
        public static bool OwnsEscape => IsOpen || EscapeConsumedFrame == Time.frameCount;

        public static void Open(OverlayPage page) => Opener?.Invoke(page);

        public static void Close() => Closer?.Invoke();
    }
}
