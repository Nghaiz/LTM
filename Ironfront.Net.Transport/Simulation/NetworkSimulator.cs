using System;
using System.Collections.Generic;

namespace Ironfront.Net.Transport.Simulation
{
    /// <summary>
    /// Sits between a peer and whatever actually moves bytes, and applies the five
    /// impairments in <see cref="SimulatorConfig"/>: loss, latency, jitter, duplication and
    /// reordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the most important piece of the transport, and it exists before the transport
    /// does on purpose (risk B1/R1): a reliability bug that only appears under 15% loss is
    /// found in a unit test at seed 12345, not during integration week.
    /// </para>
    /// <para>
    /// <b>Deviation from dev-b-transport/phase-00 § Task 5, deliberate.</b> The sketch there
    /// types the destination as <c>System.Net.EndPoint</c>. This class is generic over the
    /// destination instead, so the same simulator serves both the UDP peer
    /// (<c>NetworkSimulator&lt;EndPoint&gt;</c>) and <see cref="Loopback.LoopbackTransport"/>
    /// (<c>NetworkSimulator&lt;int&gt;</c>) with no boxing on the hot path. The
    /// <see cref="BufferPool"/> is a constructor dependency rather than a per-call argument
    /// for the same reason: a packet is rented in <see cref="ShouldSend"/> and returned in
    /// <see cref="Flush"/>, so both must be the same pool and making that a caller obligation
    /// invites a mismatch.
    /// </para>
    /// <para>
    /// <b>Trap — reordering needs a non-zero latency</b>, see <see cref="SimulatorConfig"/>.
    /// </para>
    /// </remarks>
    public sealed class NetworkSimulator<TDestination>
    {
        private struct DelayedPacket
        {
            public double DeliverAtMs;
            public byte[] Data;
            public int Length;
            public TDestination Destination;

            /// <summary>
            /// Tie-break for packets that come due on the same <see cref="Flush"/>. Without
            /// it, delivery order among equal timestamps would depend on list order, which
            /// is not the same thing as arrival order.
            /// </summary>
            public long Sequence;
        }

        private readonly List<DelayedPacket> _inFlight = new List<DelayedPacket>();
        private readonly Random _rng;
        private readonly SimulatorConfig _cfg;
        private readonly BufferPool _pool;
        private long _sequence;

        public NetworkSimulator(SimulatorConfig config, BufferPool pool)
        {
            _cfg  = config ?? throw new ArgumentNullException(nameof(config));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _rng  = new Random(config.RandomSeed);
        }

        /// <summary>Packets currently held back, waiting for their delivery time.</summary>
        public int InFlightCount => _inFlight.Count;

        /// <summary>Packets dropped by <see cref="SimulatorConfig.PacketLossPercent"/>.</summary>
        public long DroppedCount { get; private set; }

        /// <summary>Extra copies produced by <see cref="SimulatorConfig.DuplicatePercent"/>.</summary>
        public long DuplicatedCount { get; private set; }

        /// <summary>Packets given the extra <see cref="SimulatorConfig.ReorderPercent"/> delay.</summary>
        public long ReorderedCount { get; private set; }

        /// <summary>
        /// Offers a packet to the simulator.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the caller should send it immediately and unchanged — which
        /// happens only when the simulator is disabled. <c>false</c> means the simulator has
        /// taken ownership: the packet was either dropped, or copied into the in-flight queue
        /// and will come back out of <see cref="Flush"/> later. A caller that sends anyway on
        /// <c>false</c> defeats the whole simulation.
        /// </returns>
        public bool ShouldSend(ReadOnlySpan<byte> data, TDestination destination, double nowMs)
        {
            if (!_cfg.Enabled) return true;

            if (data.Length > _pool.BufferSize)
                throw new ArgumentException(
                    $"Packet is {data.Length} bytes, pool buffers are {_pool.BufferSize}.",
                    nameof(data));

            if (Roll() < _cfg.PacketLossPercent)
            {
                DroppedCount++;
                return false;
            }

            int copies = 1;
            if (Roll() < _cfg.DuplicatePercent)
            {
                copies = 2;
                DuplicatedCount++;
            }

            for (int i = 0; i < copies; i++)
            {
                double delay = _cfg.LatencyMs + (_rng.NextDouble() * 2.0 - 1.0) * _cfg.JitterMs;

                if (Roll() < _cfg.ReorderPercent)
                {
                    delay += _cfg.LatencyMs;
                    ReorderedCount++;
                }

                if (delay < 0.0) delay = 0.0;

                byte[] buf = _pool.Rent();
                data.CopyTo(buf);

                _inFlight.Add(new DelayedPacket
                {
                    DeliverAtMs = nowMs + delay,
                    Data        = buf,
                    Length      = data.Length,
                    Destination = destination,
                    Sequence    = _sequence++,
                });
            }

            return false;
        }

        /// <summary>
        /// Releases every packet whose delivery time has arrived, oldest first. Call once per
        /// <c>Poll()</c>. The buffer handed to <paramref name="deliver"/> is returned to the
        /// pool as soon as the callback returns — copy it if it must outlive the call.
        /// </summary>
        public void Flush(double nowMs, Action<byte[], int, TDestination> deliver)
        {
            if (deliver == null) throw new ArgumentNullException(nameof(deliver));
            if (_inFlight.Count == 0) return;

            // Deliver in due-time order. The list is small (tens of packets at most for a
            // single connection at 150 ms and 30 Hz), so repeatedly picking the minimum is
            // cheaper than keeping a heap and allocates nothing, which the tick loop cares
            // about far more than the asymptotics.
            while (true)
            {
                int best = -1;
                for (int i = 0; i < _inFlight.Count; i++)
                {
                    DelayedPacket p = _inFlight[i];
                    if (p.DeliverAtMs > nowMs) continue;

                    if (best < 0
                        || p.DeliverAtMs < _inFlight[best].DeliverAtMs
                        || (p.DeliverAtMs == _inFlight[best].DeliverAtMs
                            && p.Sequence < _inFlight[best].Sequence))
                    {
                        best = i;
                    }
                }

                if (best < 0) return;

                DelayedPacket due = _inFlight[best];
                _inFlight.RemoveAt(best);

                deliver(due.Data, due.Length, due.Destination);
                _pool.Return(due.Data);
            }
        }

        /// <summary>
        /// Drops everything still in flight and returns the buffers. Used when a connection
        /// closes, so a dead peer's queued packets do not leak pool buffers.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _inFlight.Count; i++) _pool.Return(_inFlight[i].Data);
            _inFlight.Clear();
        }

        private double Roll() => _rng.NextDouble() * 100.0;
    }
}
