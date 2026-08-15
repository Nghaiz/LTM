#nullable enable

using System;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Routes one payload batch. Matches <c>ClientMessageRouter.Route</c>.
    /// </summary>
    /// <remarks>
    /// A custom delegate rather than a <c>Func&lt;,&gt;</c> because <c>ReadOnlySpan&lt;byte&gt;</c>
    /// is a ref struct and cannot be a generic type argument.
    /// </remarks>
    public delegate int GamePayloadRoute(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Holds inbound payloads while the match scene loads, then replays them. phase-03 trap 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Written by the lead's assist track
    /// (plans/assist-dev-a/step-06-master-connection.md).
    /// </para>
    /// <para>
    /// The problem is phase-03 trap 3: scene loading takes 2-5 seconds and the server is
    /// already sending, so anything routed before the scene is ready spawns actors into a world
    /// that does not exist yet — a flood of <c>NullReferenceException</c>.
    /// </para>
    /// <para>
    /// <b>Everything held is replayed, in arrival order, and that is a correction to the
    /// plan.</b> phase-03 and step 06 both say to process the newest and discard the stale ones,
    /// on the reasoning that a snapshot from four seconds ago describes a world that no longer
    /// exists. That reasoning is right about the *world* and wrong about the *encoding*:
    /// snapshots are delta-encoded against a baseline the client must already hold, so skipping
    /// the middle of the sequence breaks the chain. The decoder answers the next delta with
    /// <c>UnknownBaseline</c> and keeps doing so until the server gives up and sends a full
    /// snapshot — trading a few milliseconds of decode for a visible stall at the exact moment
    /// the player is entering the match.
    /// </para>
    /// <para>
    /// Replaying in order costs almost nothing and needs no discard rule, because the discard
    /// already exists one layer down and is already correct: <c>DeltaDecoder</c> answers a
    /// snapshot older than the one applied with <c>SnapshotReadResult.Stale</c> and drops it,
    /// and <c>SnapshotInterpolator</c> keeps only its buffer window. Deciding staleness here
    /// would be a second opinion about it, and the two would disagree the first time either
    /// changed. It also preserves the reliable events — spawns, despawns, match state — which a
    /// keep-the-newest rule would throw away along with the snapshots, leaving a client in a
    /// match it never learned had started.
    /// </para>
    /// <para>
    /// <b>Payloads are copied on the way in, and they have to be.</b>
    /// <c>ITransportClient.OnMessage</c> hands out a pooled buffer that is returned the moment
    /// the handler returns; keeping the reference and reading it after the scene loads would
    /// read whatever the pool handed out next.
    /// </para>
    /// </remarks>
    public sealed class SnapshotHoldingQueue
    {
        /// <summary>
        /// Payloads held before the oldest is dropped.
        /// </summary>
        /// <remarks>
        /// A 5-second scene load at the 20 Hz snapshot rate is ~100 payloads, plus the events
        /// beside them. 256 clears that with room to spare, at 256 x 1184 B = ~300 KB of
        /// buffers in the worst case — paid once, and only while a scene is loading.
        /// </remarks>
        public const int DefaultCapacity = 256;

        private readonly byte[][] _payloads;
        private readonly int[] _lengths;
        private int _head;
        private int _count;

        public SnapshotHoldingQueue(int capacity = DefaultCapacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));

            _payloads = new byte[capacity][];
            _lengths = new int[capacity];
        }

        /// <summary>True between <see cref="Hold"/> and <see cref="Release"/>.</summary>
        public bool IsHolding { get; private set; }

        /// <summary>Payloads waiting to be replayed.</summary>
        public int Count => _count;

        /// <summary>Payloads this queue can hold at once.</summary>
        public int Capacity => _payloads.Length;

        /// <summary>Payloads held across every cycle. Diagnostic.</summary>
        public long TotalHeld { get; private set; }

        /// <summary>
        /// Payloads dropped because the queue was full.
        /// </summary>
        /// <remarks>
        /// Non-zero means the delta chain has a hole in it and the decoder will say so with
        /// <c>UnknownBaselines</c> — which is the signal to ack the newest tick actually held
        /// and let the server fall back to a full snapshot. It is recoverable, but it should
        /// never happen: a scene load long enough to overflow this is its own problem.
        /// </remarks>
        public long DroppedForOverflow { get; private set; }

        /// <summary>Starts holding. Call before dialling the game server, not after.</summary>
        /// <remarks>
        /// Snapshots cannot arrive before the connection is accepted, but arming this at the
        /// junction rather than on the connected callback removes the question of whether
        /// anything can land in between.
        /// </remarks>
        public void Hold() => IsHolding = true;

        /// <summary>
        /// Buffers <paramref name="payload"/> if holding, and reports whether it did.
        /// </summary>
        /// <remarks>
        /// Written to be the whole of the call site's decision:
        /// <c>if (!queue.TryHold(payload)) router.Route(payload);</c>
        /// </remarks>
        public bool TryHold(ReadOnlySpan<byte> payload)
        {
            if (!IsHolding) return false;

            if (_count == _payloads.Length)
            {
                // Drop the oldest. The chain is broken either way once the queue is full, and
                // keeping the newest at least leaves the client close to the live world.
                _head = (_head + 1) % _payloads.Length;
                _count--;
                DroppedForOverflow++;
            }

            int slot = (_head + _count) % _payloads.Length;

            byte[]? buffer = _payloads[slot];
            if (buffer == null || buffer.Length < payload.Length)
            {
                buffer = new byte[payload.Length];
                _payloads[slot] = buffer;
            }

            payload.CopyTo(buffer);
            _lengths[slot] = payload.Length;

            _count++;
            TotalHeld++;
            return true;
        }

        /// <summary>
        /// Stops holding and replays everything through <paramref name="route"/>, oldest first.
        /// </summary>
        /// <remarks>
        /// The buffers are kept for the next cycle rather than released — a client that plays
        /// three matches back to back allocates them once. Holding is cleared before the replay
        /// so a handler that re-enters cannot buffer what it is being handed.
        /// </remarks>
        /// <returns>Payloads replayed.</returns>
        public int Release(GamePayloadRoute route)
        {
            if (route == null) throw new ArgumentNullException(nameof(route));

            IsHolding = false;

            int replayed = _count;
            for (int i = 0; i < replayed; i++)
            {
                int slot = (_head + i) % _payloads.Length;
                route(new ReadOnlySpan<byte>(_payloads[slot], 0, _lengths[slot]));
            }

            _head = 0;
            _count = 0;
            return replayed;
        }

        /// <summary>Stops holding and throws the buffered payloads away. Use on a failed join.</summary>
        public void Clear()
        {
            IsHolding = false;
            _head = 0;
            _count = 0;
        }

        /// <summary>Zeroes the diagnostic counters. Leaves the buffers alone.</summary>
        public void ResetStatistics()
        {
            TotalHeld = 0;
            DroppedForOverflow = 0;
        }
    }
}
