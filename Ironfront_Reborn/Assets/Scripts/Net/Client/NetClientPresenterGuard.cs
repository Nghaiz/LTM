using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The two questions every client presenter has to answer before it touches anything:
    /// "should I be running at all?" and "is this actor the human at this keyboard?".
    /// phase-V10 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="IsLocalActor(Actor)"/> replaces <c>!aiControlled</c>, everywhere V10 gates
    /// a client-only singleton.</b> That flag means "is this a bot" and coincided with "is this
    /// the local player" only while the local player was the only non-AI actor in the process.
    /// The moment a remote human has an <c>Actor</c>, the coincidence ends: a remote player
    /// taking damage writes the local health bar, and a remote player mounting a turret disables
    /// the local cameras (recorded finding A16). The policy itself is
    /// <see cref="LocalActorIdentity"/>, which is engine-free and therefore graded by CI; this
    /// type is the thin half that reads the two Unity facts it needs.
    /// </para>
    /// <para>
    /// <b>Offline is unchanged by construction.</b> At <see cref="NetRole.Offline"/> the policy
    /// answers with <c>!aiControlled</c> — literally the shipped test — so single-player
    /// behaviour is preserved rather than believed to be preserved.
    /// </para>
    /// <para>
    /// <b>On a headless server every actor answers false</b>, because there is no
    /// <c>FpsActorController</c>. That is correct and slightly more than V10 asked for: the HUD
    /// calls those gates gate were already pure cost on a server.
    /// </para>
    /// </remarks>
    public static class NetClientPresenterGuard
    {
        // Warnings that must fire once and then stay quiet. A presenter whose bootstrap was
        // missing would otherwise log every frame, and a log that repeats 60 times a second is
        // read as noise and filtered out — which is the same as not logging at all.
        private static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>
        /// Whether client-side presentation should run. False offline and false on a server, so
        /// a presenter left on a shared prefab is inert rather than wrong.
        /// </summary>
        public static bool IsPresentable => NetContext.IsClient;

        /// <summary>
        /// Whether this network actor id is this client's own actor. False before the welcome
        /// message assigns one.
        /// </summary>
        /// <remarks>
        /// <b>Check this before every registry lookup.</b> The local player is deliberately
        /// excluded from <see cref="RemoteActorRegistry"/> — it is predicted, not interpolated —
        /// so every lookup misses it, and "who fired" or "who died" is frequently the local
        /// player. A presenter that asks the registry first treats its own death as an unknown
        /// actor.
        /// </remarks>
        public static bool IsLocalActor(ushort actorId)
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            ushort localId = client != null
                ? client.LocalActorId
                : LocalActorIdentity.UnassignedActorId;

            return LocalActorIdentity.IsLocalActorId(localId, actorId);
        }

        /// <summary>
        /// Whether this actor is the local player — the predicate that replaces
        /// <c>!aiControlled</c> on every per-actor path that touches a client-only singleton.
        /// </summary>
        public static bool IsLocalActor(Actor actor)
        {
            if (actor == null) return false;

            FpsActorController local = FpsActorController.instance;
            bool isLocalPlayerRig = local != null && ReferenceEquals(local.actor, actor);

            return LocalActorIdentity.IsLocalActor(
                NetContext.IsOffline, actor.aiControlled, isLocalPlayerRig);
        }

        /// <summary>This client's own actor id, if the server has assigned one yet.</summary>
        public static bool TryResolveLocalActorId(out ushort id)
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            id = client != null ? client.LocalActorId : LocalActorIdentity.UnassignedActorId;

            return id != LocalActorIdentity.UnassignedActorId;
        }

        /// <summary>
        /// This client's team, read from the replicated snapshot entry for its own actor.
        /// </summary>
        /// <remarks>
        /// <b>Not <c>FpsActorController.playerTeam</c>.</b> That field stays -1 on a server, and
        /// the minimap needs a correct value at the client role before the first flag flip.
        /// <c>ActorSnapshotEntry.Team</c> for the local actor is authoritative and arrives at
        /// spawn, which always precedes a capture. When it has not arrived, this returns false
        /// and the caller leaves the UI disabled — it must never fall back to team 0, because
        /// defaulting to team 0 is the bug (V10 D17).
        /// </remarks>
        public static bool TryResolveLocalTeam(out byte team)
        {
            team = TeamId.None;

            if (!TryResolveLocalActorId(out ushort localId)) return false;

            NetClientBootstrap client = NetClientBootstrap.Current;
            if (client == null) return false;

            WorldSnapshot snapshot = client.Router.Decoder.Current;
            if (snapshot == null) return false;
            if (!snapshot.TryFind(localId, out ActorSnapshotEntry entry)) return false;

            team = entry.Team;
            return true;
        }

        /// <summary>
        /// Resolves the client bootstrap, logging once at warning when it is missing.
        /// </summary>
        /// <remarks>
        /// <c>RemoteActorRegistry</c> captures its bootstrap in <c>Awake</c> and silently
        /// no-ops for the object's whole life if it was null then — no error, no log. Every V10
        /// presenter goes through here instead, so the same scene-ordering mistake announces
        /// itself once rather than presenting as "the netcode does nothing".
        /// </remarks>
        public static bool TryResolveClient(string presenterName, out NetClientBootstrap client)
        {
            client = NetClientBootstrap.Current;
            if (client != null) return true;

            WarnOnce(
                "no-bootstrap:" + presenterName,
                $"[net] {presenterName} found no NetClientBootstrap in Awake. It will never "
                + "subscribe, so nothing it presents will appear. Put it on the client "
                + "bootstrap object, or after it in execution order.");
            return false;
        }

        /// <summary>Logs a warning the first time this key is seen, and never again.</summary>
        public static void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            Debug.LogWarning(message);
        }

        /// <summary>
        /// Clears the once-only warning set. Called at subsystem registration for the same
        /// reason <c>NetContext.ResetOnLoad</c> exists: with domain reload disabled, statics
        /// survive into the next Play session, and a warning already "spent" in the previous run
        /// would stay silent in the one where it matters.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => _warned.Clear();
    }
}
