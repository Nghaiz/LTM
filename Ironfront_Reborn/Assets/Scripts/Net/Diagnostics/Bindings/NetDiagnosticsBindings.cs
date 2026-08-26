using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands this assembly the legacy state its recorder observes.
    /// The diagnostics counterpart to <c>NetClientBindings</c>. Phase C4d.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One member, because <see cref="IDiagnosticsProbe"/> is one seam — see its remark for why
    /// three reads share an interface.
    /// </para>
    /// <para>
    /// <b>Unset means the recorder writes <c>"absent"</c>, which is a state it already had.</b>
    /// A headless run has no HUD; a netcode match has no offline scoreboard. Neither was ever an
    /// error, and neither becomes one here.
    /// </para>
    /// </remarks>
    public static class NetDiagnosticsBindings
    {
        /// <summary>The legacy probe, or null when nothing has registered one.</summary>
        public static IDiagnosticsProbe Probe { get; set; }

        /// <summary>
        /// Produces the original first-person controller on a GameObject, or
        /// <see langword="null"/> when it carries none.
        /// </summary>
        /// <remarks>
        /// A resolver rather than a registered instance, because this one is per-object: the
        /// shadow comparison lives on the player prefab and reads the controller beside it. See
        /// <see cref="ILegacyMovementProbe"/> for why a second predefined assembly needed its own
        /// seam, and how it was found.
        /// </remarks>
        public static Func<GameObject, ILegacyMovementProbe> LegacyMovementResolver { get; set; }

        /// <summary>
        /// Resolves the original controller on <paramref name="gameObject"/>, or
        /// <see langword="null"/> when nothing is registered or it carries none.
        /// </summary>
        public static ILegacyMovementProbe ResolveLegacyMovement(GameObject gameObject)
            => LegacyMovementResolver?.Invoke(gameObject);

        /// <summary>
        /// Clears the registration, for the reason <c>NetClientBindings.ResetOnLoad</c> gives:
        /// with domain reload disabled a probe registered by the previous Play session would
        /// otherwise be handed to the next one, holding destroyed components.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Probe = null;
            LegacyMovementResolver = null;
        }
    }
}
