using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// One second of past hitbox poses per actor, so the server can ask where somebody was
    /// rather than only where they are. protocol-spec.md section 7.3, phase-02 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The stored boxes are in world space, and that is what makes rewinding free.</b>
    /// Task 2 as sketched stores a position plus a rotation plus local bounds, which forces
    /// task 3 to move the live actor into the past, raycast, and move it back — the sequence
    /// trap 3 exists to warn about. Storing the boxes already transformed means the rewound
    /// pose is data the raycast reads, so nothing is ever mutated and there is nothing to
    /// restore. See <see cref="LagCompensator"/>.
    /// </para>
    /// <para>
    /// <b>Nothing here decides which actors are worth storing.</b> The relevance filter
    /// (protocol-spec.md section 7.3's mandatory optimization for risk R6) lives at the call
    /// site, which is the only place that knows the interest set. Pulling it in here would
    /// mean this class needed a reference to <see cref="Interest.InterestManager"/> purely to
    /// answer a question its caller already knew the answer to.
    /// </para>
    /// <para>
    /// Allocation happens once per actor, on its first capture, and never again — the ring is
    /// fixed at <see cref="ProtocolConstants.HITBOX_HISTORY_TICKS"/> frames.
    /// </para>
    /// </remarks>
    public sealed class HitboxHistory
    {
        /// <summary>Frames retained per actor. 30 ticks = 1 second at 30 Hz.</summary>
        public const int Ticks = ProtocolConstants.HITBOX_HISTORY_TICKS;

        /// <summary>One stored pose.</summary>
        public struct Frame
        {
            /// <summary>The tick this pose was captured at.</summary>
            public uint Tick;

            /// <summary>False until the slot has ever been written.</summary>
            public bool Valid;

            /// <summary>World-space hitboxes at <see cref="Tick"/>.</summary>
            public HitboxSet Boxes;
        }

        private readonly Dictionary<ushort, Frame[]> _byActor =
            new Dictionary<ushort, Frame[]>(ProtocolConstants.MAX_ACTORS);

        /// <summary>Actors currently holding a ring.</summary>
        public int TrackedActorCount => _byActor.Count;

        /// <summary>Frames written since construction. The R6 filter is graded on this.</summary>
        public long FramesCaptured { get; private set; }

        /// <summary>
        /// Stores one actor's pose for <paramref name="tick"/>.
        /// </summary>
        /// <remarks>
        /// The caller applies the relevance filter before calling — see the type remarks.
        /// </remarks>
        public void Capture(uint tick, ushort actorId, in HitboxSet boxes)
        {
            if (!_byActor.TryGetValue(actorId, out Frame[]? frames))
            {
                frames = new Frame[Ticks];
                _byActor[actorId] = frames;
            }

            ref Frame frame = ref frames[tick % Ticks];
            frame.Tick = tick;
            frame.Valid = true;
            frame.Boxes = boxes;

            FramesCaptured++;
        }

        /// <summary>
        /// The pose this actor held at <paramref name="tick"/>, if it is still in the ring.
        /// </summary>
        /// <remarks>
        /// The tick is verified, not just the slot. Ticks 100 and 130 share a slot, so a ring
        /// index that happens to be populated proves nothing — returning the wrong second's
        /// pose would rewind a target to a position it held a full second ago and hand the
        /// shooter a hit on empty air.
        /// </remarks>
        public bool TryGetFrame(ushort actorId, uint tick, out Frame frame)
        {
            frame = default;

            if (!_byActor.TryGetValue(actorId, out Frame[]? frames)) return false;

            ref Frame stored = ref frames[tick % Ticks];
            if (!stored.Valid || stored.Tick != tick) return false;

            frame = stored;
            return true;
        }

        /// <summary>
        /// Drops this actor's ring.
        /// </summary>
        /// <remarks>
        /// Must be called on despawn. Without it the dictionary keeps a 30-frame ring alive for
        /// every id ever allocated, which over a long match is a slow leak with no symptom
        /// until the server's working set stops fitting.
        /// </remarks>
        public void Forget(ushort actorId) => _byActor.Remove(actorId);

        /// <summary>Drops every ring. Used between matches.</summary>
        public void Clear()
        {
            _byActor.Clear();
            FramesCaptured = 0;
        }
    }
}
