using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Paces the server: how many simulation ticks are owed right now, and whether this tick
    /// should also emit a snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately engine-free and driven by a caller-supplied clock. In Unity this is
    /// driven from <c>FixedUpdate</c>; in a test it is driven by a loop that hands it exact
    /// millisecond values, which is what makes "does the server hold 30 Hz under load"
    /// answerable without a headless build.
    /// </para>
    /// <para>
    /// <b>The spiral of death, and why the clamp is not optional.</b> If one tick takes 40 ms
    /// at a 33.3 ms budget, the loop is owed two ticks next time; running both takes 80 ms,
    /// which owes three, and the server never catches up — it just falls further behind while
    /// working harder. <see cref="MaxCatchUpTicks"/> caps how much can be owed and
    /// <see cref="DroppedTicks"/> counts what was abandoned, so time is discarded visibly
    /// rather than accumulating into a death spiral. This is <c>Time.maximumDeltaTime</c>'s
    /// job in Unity, restated here so the pure server has it too.
    /// </para>
    /// </remarks>
    public sealed class ServerTickScheduler
    {
        private readonly double _msPerTick;
        private readonly double _msPerSnapshot;

        private double _tickAccumulatorMs;
        private double _snapshotAccumulatorMs;
        private double _lastNowMs;
        private bool _started;

        public ServerTickScheduler(
            int simTickRate = ProtocolConstants.SIM_TICK_RATE,
            int snapshotRate = ProtocolConstants.SNAPSHOT_RATE,
            int maxCatchUpTicks = 3,
            int tickTimeHistory = 256)
        {
            if (simTickRate <= 0) throw new ArgumentOutOfRangeException(nameof(simTickRate));
            if (snapshotRate <= 0) throw new ArgumentOutOfRangeException(nameof(snapshotRate));
            if (maxCatchUpTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks));

            _msPerTick      = 1000.0 / simTickRate;
            _msPerSnapshot  = 1000.0 / snapshotRate;
            MaxCatchUpTicks = maxCatchUpTicks;
            TickTimes       = new TickTimeStats(tickTimeHistory);
        }

        /// <summary>The tick about to run. Incremented by <see cref="BeginTick"/>.</summary>
        public uint CurrentTick { get; private set; }

        /// <summary>Ticks the scheduler will run back-to-back before discarding the backlog.</summary>
        public int MaxCatchUpTicks { get; }

        /// <summary>Ticks abandoned to avoid a spiral of death. Non-zero means the server is overloaded.</summary>
        public long DroppedTicks { get; private set; }

        /// <summary>Snapshots the accumulator has asked for.</summary>
        public long SnapshotsDue { get; private set; }

        /// <summary>Rolling tick-duration distribution. Phase-01 acceptance wants p99 &lt; 33 ms.</summary>
        public TickTimeStats TickTimes { get; }

        /// <summary>Milliseconds per simulation tick (33.33 at 30 Hz).</summary>
        public double MsPerTick => _msPerTick;

        /// <summary>Seconds per simulation tick — the <c>dt</c> for <c>MovementCore.Step</c>.</summary>
        public float FixedDeltaTime => (float)(_msPerTick / 1000.0);

        /// <summary>
        /// Feeds the clock forward and reports how many simulation ticks are owed.
        /// </summary>
        /// <remarks>
        /// The first call establishes the time base and returns 0 — otherwise a server whose
        /// first <paramref name="nowMs"/> is a large process-uptime value would be owed
        /// millions of ticks and immediately drop all but three of them.
        /// </remarks>
        public int Advance(double nowMs)
        {
            if (!_started)
            {
                _started   = true;
                _lastNowMs = nowMs;
                return 0;
            }

            double elapsed = nowMs - _lastNowMs;
            _lastNowMs = nowMs;

            // A clock that went backwards is a caller bug or an NTP step; either way,
            // treating it as negative elapsed time would rewind the accumulator.
            if (elapsed < 0.0) elapsed = 0.0;

            _tickAccumulatorMs += elapsed;

            int owed = (int)(_tickAccumulatorMs / _msPerTick);
            if (owed <= 0) return 0;

            _tickAccumulatorMs -= owed * _msPerTick;

            if (owed > MaxCatchUpTicks)
            {
                DroppedTicks += owed - MaxCatchUpTicks;
                owed = MaxCatchUpTicks;
            }

            return owed;
        }

        /// <summary>
        /// Opens a tick: advances <see cref="CurrentTick"/> and moves the snapshot
        /// accumulator. Call once per tick reported by <see cref="Advance"/>.
        /// </summary>
        public uint BeginTick()
        {
            CurrentTick++;
            _snapshotAccumulatorMs += _msPerTick;
            return CurrentTick;
        }

        /// <summary>
        /// Whether this tick should also build and send snapshots. Consumes the accumulator,
        /// so call it exactly once per tick.
        /// </summary>
        /// <remarks>
        /// 20 Hz snapshots against a 30 Hz simulation is 1.5 ticks per snapshot, so the
        /// pattern alternates 2,1,2,1 rather than landing on a fixed stride. Subtracting one
        /// interval instead of zeroing the accumulator is what keeps the long-run average at
        /// exactly 20 Hz instead of drifting to 15.
        /// </remarks>
        public bool ShouldSendSnapshot()
        {
            if (_snapshotAccumulatorMs < _msPerSnapshot) return false;

            _snapshotAccumulatorMs -= _msPerSnapshot;
            SnapshotsDue++;
            return true;
        }

        /// <summary>Records how long a tick took, for <see cref="TickTimes"/>.</summary>
        public void RecordTickTime(double milliseconds) => TickTimes.Record(milliseconds);

        /// <summary>
        /// True when the server has been over budget long enough to act on — the signal to
        /// log a warning and shed bots (phase-01 trap 2).
        /// </summary>
        public bool IsOverloaded(double budgetMs = -1.0, double p99Threshold = 0.9)
        {
            double budget = budgetMs > 0 ? budgetMs : _msPerTick;
            return TickTimes.Count >= TickTimes.Capacity
                && TickTimes.Percentile(99) > budget * p99Threshold;
        }
    }
}
