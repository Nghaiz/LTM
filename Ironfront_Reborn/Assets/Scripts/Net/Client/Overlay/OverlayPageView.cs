#nullable enable

using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>One page of the overlay: settings, the guide, achievements, the ranking.</summary>
    /// <remarks>
    /// The host draws the frame -- backdrop, panel, heading, the page switcher, the close key -- and
    /// hands each page its content rect and the footer's right-hand side for its own actions.
    /// </remarks>
    public abstract class OverlayPageView : MonoBehaviour
    {
        public abstract OverlayPage Page { get; }

        /// <summary>The page switcher's caption and the heading: "SETTINGS".</summary>
        public abstract string Title { get; }

        /// <summary>The small line over the heading: "SYSTEM // CONFIGURATION".</summary>
        public abstract string Kicker { get; }

        /// <summary>One line under the heading.</summary>
        public abstract string Subtitle { get; }

        /// <summary>The icon in the page switcher, from <c>Resources/IronfrontUi/Icons</c>.</summary>
        public abstract string IconName { get; }

        /// <summary>Builds the page into <paramref name="content"/>; actions go in <paramref name="actions"/>.</summary>
        public abstract void Build(RectTransform content, RectTransform actions);

        /// <summary>The page is about to be shown. Refresh what it shows.</summary>
        public virtual void OnShown() { }

        /// <summary>The page was hidden or the overlay closed.</summary>
        public virtual void OnHidden() { }

        /// <summary>
        /// Escape was pressed while this page is up. True when the page used it (a key being
        /// rebound, a confirmation bar); false lets the host close the overlay.
        /// </summary>
        public virtual bool HandleEscape() => false;

        /// <summary>
        /// Whether the overlay may close now, or switch away from this page. A page with unsaved
        /// changes asks first and answers false; the host leaves it to the page.
        /// </summary>
        public virtual bool TryLeave(System.Action leave) => true;
    }
}
