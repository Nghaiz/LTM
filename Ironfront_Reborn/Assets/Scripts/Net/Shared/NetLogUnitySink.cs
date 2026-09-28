using Ironfront.Net.Transport;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Points <see cref="NetLog"/> at Unity's console. Installed by every process that opens a
    /// connection — client, listen server and dedicated server alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <c>NetLog.Warning</c> and <c>NetLog.Error</c> are
    /// <c>Action&lt;string&gt;</c> fields the transport writes through, and until 2026-08-21 the
    /// only subscriber in the whole repository was <c>LaneBHarness</c>. So in a shipped player
    /// every transport warning was formatted and handed to a null delegate — including the two
    /// lines that are the only explanation a <c>TransportError</c> ever gets, "reliable sequence
    /// N abandoned after M resends" and "reliable sequence slot collision at N".
    /// <c>Connection.Update</c>'s own comment says it ends a connection "loudly instead of
    /// continuing quietly"; without a sink the loud half reached nobody and a dropped client
    /// presented as a bare reason code. That is defect 2 of the phase-3D lane-B report, and it
    /// is why the reliable-ack blocker took a day of measurement to name.
    /// </para>
    /// <para>
    /// <b>Why it is not in LaneBHarness.</b> That file is now behind
    /// <c>IRONFRONT_NO_DIAGNOSTICS</c>, so leaving the only sink there would mean a shipping
    /// client build has no transport logging at all — the same silence, arrived at a different
    /// way. Shared is the one assembly every net process already loads.
    /// </para>
    /// <para>
    /// <b>Never an exception.</b> A transport error is a connection ending, which the caller
    /// already handles; throwing here would turn a handled disconnect into an unhandled one
    /// inside the receive loop.
    /// </para>
    /// <para>
    /// <b>And never <c>Debug.LogError</c> under <c>-batchmode</c>.</b> That is
    /// <c>LaneBHarness</c>'s finding, kept rather than re-litigated: a logged error can end a
    /// batchmode run, and these are diagnostics about a connection that is already ending — so
    /// reporting one must not cost the rest of the log. The <c>[transport:error]</c> prefix is
    /// identical either way, so anything grepping for it is unaffected by which severity was
    /// used.
    /// </para>
    /// </remarks>
    public static class NetLogUnitySink
    {
        private static bool _installed;

        /// <summary>Installs the sinks once per domain. Safe to call from every bootstrap.</summary>
        /// <remarks>
        /// Idempotent because three bootstraps may run in one process — a listen server is a
        /// client and a server at once — and re-assigning would be harmless but re-reading this
        /// code to establish that is not free.
        /// </remarks>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            NetLog.Warning = message => Debug.LogWarning($"[transport] {message}");

            if (Application.isBatchMode)
            {
                NetLog.Error = message => Debug.LogWarning($"[transport:error] {message}");
            }
            else
            {
                NetLog.Error = message => Debug.LogError($"[transport:error] {message}");
            }
        }

        /// <summary>Removes the sinks and allows a re-install. For tests and domain reload.</summary>
        public static void Reset()
        {
            _installed = false;
            NetLog.Warning = null;
            NetLog.Error = null;
        }
    }
}
