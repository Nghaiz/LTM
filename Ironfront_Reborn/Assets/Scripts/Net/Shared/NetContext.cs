using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>Which half of the netcode this process is running.</summary>
    public enum NetRole : byte
    {
        /// <summary>No netcode active — the original single-player game.</summary>
        Offline = 0,

        /// <summary>Predicting locally and reconciling against a remote authority.</summary>
        Client = 1,

        /// <summary>The authority. Its simulation is the truth by definition.</summary>
        Server = 2,
    }

    /// <summary>
    /// The one place a script may ask "am I the server?" and "what tick is it?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The role exists so that shared components can be attached to the same prefab on both
    /// sides and decide at runtime whether to run. <see cref="MovementSimulation"/> itself must
    /// never consult it — a branch on the role inside the shared simulation is exactly the
    /// divergence prediction cannot survive (C-AD-4). The role governs <i>who drives</i> the
    /// simulation, never <i>what it computes</i>.
    /// </para>
    /// <para>
    /// <b>Static state and domain reload.</b> With Enter Play Mode Options enabled and domain
    /// reload disabled — a common setting on a project this size, because the reload costs
    /// seconds on every Play — statics survive from one Play session into the next. A server
    /// role left over from the previous run would have the next client-side session silently
    /// behave as an authority. <see cref="ResetOnLoad"/> clears it at subsystem registration,
    /// before any <c>Awake</c>, so the default is always Offline.
    /// </para>
    /// </remarks>
    public static class NetContext
    {
        /// <summary>The active role. Offline until something calls <see cref="SetRole"/>.</summary>
        public static NetRole Role { get; private set; }

        /// <summary>
        /// The authoritative tick, published by the server tick loop and by the client's
        /// reconciliation so that logs from both sides can be lined up.
        /// </summary>
        public static uint CurrentTick { get; set; }

        public static bool IsServer => Role == NetRole.Server;
        public static bool IsClient => Role == NetRole.Client;
        public static bool IsOffline => Role == NetRole.Offline;

        /// <summary>
        /// Declares the role. Called from a bootstrap at execution order -1000, so that it is
        /// set before any component's <c>Awake</c> can read it.
        /// </summary>
        public static void SetRole(NetRole role)
        {
            if (Role == role) return;

            Role = role;
            Debug.Log($"[net] role = {role}");
        }

        /// <summary>Returns to Offline and rewinds the tick. Called on teardown.</summary>
        public static void Clear()
        {
            Role = NetRole.Offline;
            CurrentTick = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Role = NetRole.Offline;
            CurrentTick = 0;
        }
    }
}
