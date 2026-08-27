using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The three questions <c>Assembly-CSharp</c> asks before it touches a client-only singleton:
    /// "is this actor the human at this keyboard?", "what team is that human on?", and "say this
    /// once and then be quiet". Phase C5b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> C5b set <c>autoReferenced: false</c> on
    /// <c>Ironfront.Net.Unity.Client</c>, so <c>Assembly-CSharp</c> can no longer name
    /// <c>NetClientPresenterGuard</c> — and eight legacy files did, at fourteen call sites, every
    /// one of them spelled out as <c>Ironfront.Net.Unity.Client.NetClientPresenterGuard.…</c> with
    /// no <c>using</c> line to find them by. This is the half of that guard the legacy tree
    /// actually asks for, in <c>Ironfront.Net.Unity.Shared</c>, which stays
    /// <c>autoReferenced: true</c> as the one declared channel.
    /// </para>
    /// <para>
    /// <b>It is not a second guard, and the split is not arbitrary.</b> The policy below is
    /// engine-light and needs nothing from the client assembly, so it MOVED here rather than being
    /// copied: <see cref="NetClientPresenterGuard"/> now forwards to it and the implementation
    /// exists once. What stayed behind is what genuinely cannot leave —
    /// <c>TryResolveClient</c> hands back a <c>NetClientBootstrap</c>, and
    /// <c>TryResolveLocalTeam</c> reads <c>client.Router.Decoder.Current</c>. That one arrives here
    /// through <see cref="NetClientBindings.LocalTeamResolver"/>, registered by the client at
    /// startup, so its implementation also exists exactly once.
    /// </para>
    /// <para>
    /// <b>The client's own 37 call sites were deliberately NOT re-spelled.</b> Leaving
    /// <see cref="NetClientPresenterGuard"/>'s surface intact is what kept this phase to a
    /// relocation of the seam rather than a rewrite of the presenters, inside a refactor whose
    /// acceptance criteria forbid behaviour change.
    /// </para>
    /// <para>
    /// <b>Absent means false, everywhere.</b> On a dedicated server nothing registers a team
    /// resolver and no actor is the local player; both answers are already the answers the shipped
    /// code gave, so a headless process is unchanged rather than believed to be unchanged.
    /// </para>
    /// </remarks>
    public static class NetPresenterGate
    {
        // Warnings that must fire once and then stay quiet. A presenter whose bootstrap was
        // missing would otherwise log every frame, and a log that repeats 60 times a second is
        // read as noise and filtered out — which is the same as not logging at all.
        //
        // This set is the ONLY copy. NetClientPresenterGuard.WarnOnce forwards here, so a
        // presenter warning and a legacy warning share one dedup key space, exactly as they did
        // when both lived in the client assembly.
        private static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>Key prefix marking a "presenter found no bootstrap" warning.</summary>
        public const string NoBootstrapPrefix = "no-bootstrap:";

        /// <summary>
        /// Whether this actor is the local player — the predicate that replaces
        /// <c>!aiControlled</c> on every per-actor path that touches a client-only singleton.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Moved here verbatim, and it needed nothing from the client assembly to come.</b>
        /// <c>NetContext</c> was already in this assembly, <c>LocalActorIdentity</c> is in the
        /// engine-free replication library that CI grades, and
        /// <see cref="IGameplayActorPresence"/> moved alongside it in the same phase. The offline
        /// answer is still literally <c>!aiControlled</c>, so single-player behaviour is preserved
        /// rather than assumed.
        /// </para>
        /// <para>
        /// <b>Both guard clauses, and the second one is load-bearing.</b> <c>actor == null</c> is a
        /// plain reference comparison — the interface has none of <c>UnityEngine.Object</c>'s
        /// overloaded equality — so a DESTROYED body arrives here non-null and is rejected by
        /// <c>Exists</c> on the far side instead. Dropping that clause while relocating this
        /// method would have made a destroyed local actor start answering true, which is a
        /// behaviour change wearing a refactor's clothes.
        /// </para>
        /// </remarks>
        public static bool IsLocalActor(IGameplayActorPresence actor)
        {
            if (actor == null || !actor.Exists) return false;

            return LocalActorIdentity.IsLocalActor(
                NetContext.IsOffline, actor.IsAiControlled, actor.IsLocalPlayerBody);
        }

        /// <summary>
        /// The local player's team, when a client is running and the snapshot carries the local
        /// actor. False on a server, offline, or before the welcome message assigns an id.
        /// </summary>
        /// <remarks>
        /// Registered by the client assembly rather than implemented here: the answer comes from
        /// <c>client.Router.Decoder.Current</c>, which is client-internal machinery this assembly
        /// deliberately cannot see. Unregistered is not an error — it is a dedicated server, and
        /// <c>false</c> is what the shipped code returned there too.
        /// </remarks>
        public static bool TryResolveLocalTeam(out byte team)
        {
            NetClientBindings.LocalTeamResolver resolver = NetClientBindings.LocalTeam;
            if (resolver != null) return resolver(out team);

            team = TeamId.None;
            return false;
        }

        /// <summary>Logs <paramref name="message"/> the first time <paramref name="key"/> is seen.</summary>
        public static void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            Debug.LogWarning(message);
        }

        /// <summary>
        /// Every presenter that reported no <c>NetClientBootstrap</c>, for the wiring gate to read.
        /// </summary>
        public static IEnumerable<string> PresentersThatFoundNoBootstrap
        {
            get
            {
                foreach (string key in _warned)
                {
                    if (key.StartsWith(NoBootstrapPrefix, StringComparison.Ordinal))
                        yield return key.Substring(NoBootstrapPrefix.Length);
                }
            }
        }

        /// <summary>
        /// Clears the once-only warnings between Play sessions. With domain reload disabled a key
        /// recorded by the previous session would otherwise suppress the same warning in the next
        /// one, which is how a real wiring mistake becomes invisible on the second run.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => _warned.Clear();
    }
}
