using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The between-rounds teardown signal, reachable from <c>Assembly-CSharp</c>. Phase-V8 task 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> <see cref="MatchController.WorldResetRequested"/> is an
    /// instance event whose doc comment says "the spawner subscribes". Nothing did — verified by
    /// grep across every <c>*.cs</c> in the repository outside <c>obj/</c>: it was declared,
    /// invoked, and had zero subscribers. So match two inherited match one's vehicles and its
    /// wrecks, and phase-V9's "five clean matches back to back" could not pass.
    /// </para>
    /// <para>
    /// <b>Static, rather than a serialized reference on every spawner.</b> Vehicle spawners are
    /// authored assets scattered across a map, placed by whoever built the level, and there may
    /// be dozens. Wiring each one to a scene controller is a per-map manual step that will be
    /// forgotten on exactly the map nobody re-opened — which is the failure mode this class is
    /// here to remove, not to reproduce one level up.
    /// </para>
    /// <para>
    /// <b>Cleared at subsystem registration</b>, for the same reason
    /// <c>NetContext.ResetOnLoad</c> is: with domain reload disabled, a static event keeps its
    /// subscriber list across entries into play mode, and the second run would deliver the reset
    /// to destroyed objects from the first.
    /// </para>
    /// </remarks>
    public static class NetWorldLifecycle
    {
        /// <summary>
        /// Raised once per match reset, before the netcode tables are cleared. Subscribers
        /// despawn what they own.
        /// </summary>
        public static event Action ResetRequested;

        /// <summary>
        /// Raises the signal. Every subscriber is called even when one of them throws — a
        /// spawner that NREs must not take the rest of the map's teardown down with it, and the
        /// leak that would cause is invisible until the third round.
        /// </summary>
        public static void RaiseReset()
        {
            Delegate[] subscribers = ResetRequested?.GetInvocationList();
            if (subscribers == null) return;

            for (int i = 0; i < subscribers.Length; i++)
            {
                try
                {
                    ((Action)subscribers[i])();
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[net] a world-reset subscriber threw: {exception}");
                }
            }
        }

        /// <summary>Drops every subscriber. For teardown and for tests.</summary>
        public static void Clear() => ResetRequested = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => ResetRequested = null;
    }
}
