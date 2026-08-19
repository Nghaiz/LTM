using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// The client's copy of every capture point's authoritative state, indexed by point id, with
    /// a dirty bit per point so a repaint costs only what changed. phase-V10 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A point can be fully owned and contested at the same time.</b> <c>Contested</c> means
    /// both teams have somebody inside the radius; ownership is where the bar sits. They are
    /// independent, which is why the flag is the one genuinely new bit in the message — and why
    /// this type exposes them as two properties rather than folding them into one state enum.
    /// </para>
    /// <para>
    /// <b>Neutral is <see cref="TeamId.None"/> (255), never team 0.</b> Casting an ownership
    /// value to a team id and letting the neutral case fall out as zero is the mistake the
    /// wire format was shaped to prevent — 255 is used precisely so a client that switches on
    /// 0/1 and forgets the third case falls through rather than rendering neutral as a team.
    /// </para>
    /// <para>
    /// Fixed array, no dictionary, no allocation after construction: capture-point messages
    /// arrive on every flip during a contested round.
    /// </para>
    /// </remarks>
    public sealed class CapturePointView
    {
        /// <summary>
        /// Points a map may carry. The id is a byte on the wire; 64 is far past any map in
        /// scope and keeps the backing arrays trivial.
        /// </summary>
        public const int DefaultCapacity = 64;

        private readonly CapturePointMessage[] _points;
        private readonly bool[] _known;
        private readonly bool[] _dirty;

        public CapturePointView(int capacity = DefaultCapacity)
        {
            if (capacity < 1) capacity = 1;
            if (capacity > byte.MaxValue + 1) capacity = byte.MaxValue + 1;

            _points = new CapturePointMessage[capacity];
            _known  = new bool[capacity];
            _dirty  = new bool[capacity];
        }

        /// <summary>How many point ids this view can hold.</summary>
        public int Capacity => _points.Length;

        /// <summary>Messages applied this connection, including no-op repeats.</summary>
        public long AppliedCount { get; private set; }

        /// <summary>
        /// Latches one point's authoritative state. Returns false for a point id past
        /// <see cref="Capacity"/> — rejected rather than clamped, because clamping would write
        /// one point's state onto another's marker.
        /// </summary>
        public bool Apply(in CapturePointMessage message)
        {
            int id = message.PointId;
            if (id >= _points.Length) return false;

            bool changed =
                !_known[id]
                || _points[id].OwnerQ != message.OwnerQ
                || _points[id].Flags  != message.Flags;

            _points[id] = message;
            _known[id]  = true;
            if (changed) _dirty[id] = true;

            AppliedCount++;
            return true;
        }

        /// <summary>Whether a message has ever arrived for this point.</summary>
        public bool IsKnown(int pointId)
            => pointId >= 0 && pointId < _points.Length && _known[pointId];

        /// <summary>Ownership x100, -100 (team 0) .. +100 (team 1). Zero for an unknown point.</summary>
        public sbyte OwnerQ(int pointId) => IsKnown(pointId) ? _points[pointId].OwnerQ : (sbyte)0;

        /// <summary>
        /// Capture progress as 0..1, the magnitude of ownership regardless of which team holds
        /// it — the same <c>Abs</c> mapping the server's own capture-point slave applies, so the
        /// two sides cannot disagree about what "half captured" means.
        /// </summary>
        public float Control(int pointId)
        {
            sbyte q = OwnerQ(pointId);
            int magnitude = q < 0 ? -q : q;
            return magnitude / 100f;
        }

        /// <summary>
        /// Which team holds the point, or <see cref="TeamId.None"/> while it is still being
        /// fought over or has never been reported.
        /// </summary>
        public byte OwningTeam(int pointId)
            => IsKnown(pointId) ? _points[pointId].OwningTeam : TeamId.None;

        /// <summary>Whether both teams are inside the radius. Independent of ownership.</summary>
        public bool IsContested(int pointId) => IsKnown(pointId) && _points[pointId].IsContested;

        /// <summary>
        /// Whether this point changed since the last call, clearing the flag. A repeated
        /// message with identical ownership and flags does not mark it dirty, so a 1 Hz
        /// broadcast of an unchanging point costs no repaints.
        /// </summary>
        public bool DirtySinceLastRead(int pointId)
        {
            if (pointId < 0 || pointId >= _dirty.Length || !_dirty[pointId]) return false;

            _dirty[pointId] = false;
            return true;
        }

        /// <summary>Forgets every point. Call when leaving a match.</summary>
        public void Reset()
        {
            for (int i = 0; i < _points.Length; i++)
            {
                _points[i] = default;
                _known[i]  = false;
                _dirty[i]  = false;
            }

            AppliedCount = 0;
        }
    }
}
