namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Answers "is this the actor the human at this keyboard is playing?" — the predicate
    /// phase-V10 D2 introduces to replace <c>!aiControlled</c> everywhere a client-only
    /// singleton is touched from a per-actor path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not simply <c>!aiControlled</c>.</b> That flag is frozen in
    /// <c>Actor.Awake</c> from <c>controller.GetType() == typeof(AiActorController)</c>, so it
    /// means "is this a bot". It coincided with "is this the local player" only while the local
    /// player was the only non-AI actor in the process. The moment a remote human is
    /// represented by an <c>Actor</c>, a remote player taking damage writes the local HUD and a
    /// remote player mounting a turret disables the local cameras (recorded finding A16).
    /// </para>
    /// <para>
    /// <b>Why the policy lives here and the dereference lives in Unity.</b> Everything on this
    /// type is a pure function of values the caller already holds, so the rule is graded by
    /// CI. The Unity-side <c>NetClientPresenterGuard</c> is the thin half that reads
    /// <c>NetContext.Role</c> and <c>FpsActorController.instance</c> and calls in here — the
    /// same split every other V10 model uses, and the reason a role branch never appears
    /// inside shared simulation (<c>NetContext</c> remarks).
    /// </para>
    /// </remarks>
    public static class LocalActorIdentity
    {
        /// <summary>
        /// The id <c>NetClientBootstrap.LocalActorId</c> holds before the server has told this
        /// client which actor is its own, and again after a disconnect. Zero is not a legal
        /// actor id, so it is unambiguous — but a naive <c>actorId == LocalActorId</c> would
        /// match every message about actor 0 during that window.
        /// </summary>
        public const ushort UnassignedActorId = 0;

        /// <summary>
        /// Whether <paramref name="actorId"/> is this client's own actor.
        /// </summary>
        /// <remarks>
        /// False while the local id is still <see cref="UnassignedActorId"/>. That is the
        /// conservative answer in both directions: before the welcome message arrives no event
        /// can legitimately be about "us", and treating one as local would route a remote
        /// player's death into the local respawn path.
        /// </remarks>
        public static bool IsLocalActorId(ushort localActorId, ushort actorId)
            => localActorId != UnassignedActorId && actorId == localActorId;

        /// <summary>
        /// Whether an <c>Actor</c> is the local player, from the two facts the engine side can
        /// cheaply supply.
        /// </summary>
        /// <param name="isOffline">
        /// <c>NetContext.IsOffline</c> — the original single-player game.
        /// </param>
        /// <param name="aiControlled">The actor's frozen <c>aiControlled</c> flag.</param>
        /// <param name="isLocalPlayerRig">
        /// Whether this actor is the one <c>FpsActorController.instance</c> drives. False on a
        /// headless server, where there is no local player at all.
        /// </param>
        /// <remarks>
        /// <b>Offline is answered with <paramref name="aiControlled"/> on purpose.</b> It is
        /// what the shipped code already tested, so single-player behaviour is unchanged
        /// byte-for-byte rather than merely believed to be — which is the whole reason the
        /// A16 gating is a safe mechanical edit. <c>OfflineLocalActorGatingMatchesAiControlled</c>
        /// pins the equivalence.
        /// </remarks>
        public static bool IsLocalActor(bool isOffline, bool aiControlled, bool isLocalPlayerRig)
            => isOffline ? !aiControlled : isLocalPlayerRig;
    }
}
