using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands the diagnostics assembly the legacy state its recorder
    /// observes. Phase C4d; moved into <c>Ironfront.Net.Unity.Shared</c> by phase C5a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this lives in Shared and not beside the recorder that reads it.</b> C5a set
    /// <c>autoReferenced: false</c> on <c>Ironfront.Net.Unity.Diagnostics</c>, so
    /// <c>Assembly-CSharp</c> can no longer name a type declared there. The binding that
    /// implements <see cref="IDiagnosticsProbe"/> reads <c>ScoreUi</c>, <c>MatchScoreboard</c>
    /// and <c>CapturePoint</c>, so it CANNOT move the other way either — no asmdef sees a
    /// predefined assembly. Both halves are pinned, and the only thing that can move is the
    /// interface between them. <c>Ironfront.Net.Unity.Shared</c> stays <c>autoReferenced: true</c>
    /// deliberately: it is the one declared channel, and this is the same move
    /// <c>ICapturePointDirectory</c> made in the commit that added
    /// <c>tools/check-net-layering.ps1</c>.
    /// </para>
    /// <para>
    /// <b>The <c>!IRONFRONT_NO_DIAGNOSTICS</c> constraint deliberately did NOT come with it, and
    /// that fixes a latent break.</b> <c>IronfrontNetBindings.Install</c> registers here
    /// unconditionally, from <c>Assembly-CSharp</c>, which carries no such constraint — so while
    /// this type lived in the constrained assembly, a <c>-noDiagnostics</c> player build could not
    /// compile at all. Nothing had run that configuration since C4d landed it. Interfaces and a
    /// static holder ship in every build now; the harness they front still does not.
    /// </para>
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
