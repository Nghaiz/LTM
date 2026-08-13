using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Remembers which clients have been told about which actors. phase-02 task 6, trap 8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem.</b> <c>S_SPAWN_ACTOR</c> goes out reliable-ordered on channel 2 while
    /// snapshots go out unreliable-sequenced on channel 1. The channels have no ordering
    /// relationship, so a snapshot naming actor 17 can and will overtake the spawn that
    /// introduced actor 17. The client skips ids it does not know, so nothing crashes — the
    /// actor simply does not appear until the next snapshot that happens to include it, which
    /// at <see cref="Interest.InterestLevel.Far"/> can be a quarter of a second later.
    /// </para>
    /// <para>
    /// <b>The fix is ordering on the send side, not tolerance on the receive side.</b> An
    /// actor is withheld from a client's snapshot until that client's spawn message has been
    /// handed to the transport. The reliable channel then guarantees the spawn arrives, and
    /// arrives first, because it was sent first.
    /// </para>
    /// <para>
    /// The name is a slight overstatement and worth being precise about: this tracks that the
    /// spawn was <i>sent</i>, not that the client acked it. Waiting for a real ack would add a
    /// round trip before an actor could appear, and the reliable channel already guarantees
    /// eventual in-order delivery — so "sent on the reliable channel" is exactly the property
    /// the ordering argument needs.
    /// </para>
    /// </remarks>
    public sealed class SpawnAckTracker
    {
        private readonly HashSet<uint> _sent =
            new HashSet<uint>(ProtocolConstants.MAX_PLAYERS * ProtocolConstants.MAX_ACTORS);

        /// <summary>Live (viewer, target) pairs. Watch for leaks the same way as interest pairs.</summary>
        public int TrackedPairCount => _sent.Count;

        /// <summary>Whether <paramref name="viewerActorId"/> has been sent a spawn for this actor.</summary>
        public bool HasSpawnBeenSent(ushort viewerActorId, ushort targetActorId)
            => _sent.Contains(PackPair(viewerActorId, targetActorId));

        /// <summary>
        /// Records that the spawn has gone out.
        /// </summary>
        /// <returns>False when it had already been sent, so callers can avoid a duplicate.</returns>
        public bool MarkSpawnSent(ushort viewerActorId, ushort targetActorId)
            => _sent.Add(PackPair(viewerActorId, targetActorId));

        /// <summary>
        /// Forgets this actor in both roles — as a viewer that left and as a target that
        /// despawned.
        /// </summary>
        /// <remarks>
        /// Both directions, deliberately. Forgetting only the target role leaves a departed
        /// player's viewer rows behind forever; forgetting only the viewer role means a
        /// despawned-then-respawned id is never re-announced, and the actor stays invisible to
        /// everyone who knew its previous incarnation.
        /// </remarks>
        public void Forget(ushort actorId)
        {
            List<uint>? doomed = null;

            foreach (uint key in _sent)
            {
                ushort viewer = (ushort)(key >> 16);
                ushort target = (ushort)(key & 0xFFFF);
                if (viewer != actorId && target != actorId) continue;

                doomed ??= new List<uint>();
                doomed.Add(key);
            }

            if (doomed == null) return;
            for (int i = 0; i < doomed.Count; i++) _sent.Remove(doomed[i]);
        }

        /// <summary>Forgets everything. Used between matches.</summary>
        public void Clear() => _sent.Clear();

        private static uint PackPair(ushort viewer, ushort target)
            => ((uint)viewer << 16) | target;
    }
}
