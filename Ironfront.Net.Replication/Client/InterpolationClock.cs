using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// The one render time every remote body is drawn at: a continuous server tick, advanced by
    /// real time and held <see cref="DelayTicks"/> behind the snapshot stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a clock and not a formula over the newest snapshot.</b> The render tick
    /// used to be <c>newestServerTick + NetPredictionClock.Alpha - DelayTicks</c>. The newest tick
    /// only moves when a snapshot lands, 20 times a second, while <c>Alpha</c> is the fraction of
    /// the client's own 30 Hz tick and wraps to zero 30 times a second — so every wrap that fell
    /// between two arrivals threw the render time back a whole tick. Three wraps per two arrivals
    /// is at least one backward step of 33 ms every 100 ms: every remote body and vehicle walked
    /// back and forth at 10 Hz on top of its real motion. That is the "moves like it teleports
    /// frame by frame" of the 2026-09-27 report, and no interpolator downstream could undo it,
    /// because the time it was given was already wrong.
    /// </para>
    /// <para>
    /// <b>How it stays behind the stream without chasing it.</b> The tick advances at exactly the
    /// server's rate, scaled by at most <see cref="MaxRateAdjust"/> either way. Each arrival
    /// measures how far the new snapshot is ahead of the render time, and the average of those
    /// leads steers the scale: a lead above <see cref="DelayTicks"/> means the render time has
    /// fallen behind and runs slightly fast, one below means it runs slightly slow. The arrivals
    /// themselves never move the render time, so their jitter reaches the picture only as a
    /// few percent of playback speed, never as a jump.
    /// </para>
    /// <para>
    /// <b>It never runs past the newest snapshot.</b> Past it there is nothing to draw, and a
    /// clock that kept going would leave the stream behind by however long the stall lasted and
    /// then run slow for seconds to recover. Held at the newest tick instead, it resumes from
    /// where the picture actually stopped.
    /// </para>
    /// <para>
    /// <b>The delay is <see cref="ProtocolConstants.INTERP_BUFFER_MS"/>, not a number of its own.</b>
    /// <c>LagCompensator.RewindTicks</c> rewinds a shot by exactly that much on the assumption
    /// that the shooter was looking that far into the past, so a render delay of any other value
    /// puts every shot at a moving target consistently to one side of it. It is also two snapshot
    /// intervals at 20 Hz, which is what lets a Mid-band body — sent every second snapshot, three
    /// ticks apart — still find the snapshot after the one it is drawn at.
    /// </para>
    /// <para>
    /// Engine-free and allocation-free. The caller supplies the time; <see cref="AdvanceTo"/> is
    /// idempotent for a repeated time, so every reader in a frame may call it and the first one
    /// does the advancing.
    /// </para>
    /// </remarks>
    public sealed class InterpolationClock
    {
        /// <summary>
        /// How far behind the snapshot stream remote bodies are drawn, in simulation ticks.
        /// <see cref="ProtocolConstants.INTERP_BUFFER_MS"/> at <see cref="ProtocolConstants.SIM_TICK_RATE"/>.
        /// </summary>
        public const int DelayTicks =
            ProtocolConstants.INTERP_BUFFER_MS * ProtocolConstants.SIM_TICK_RATE / 1000;

        /// <summary>The largest share by which playback may run fast or slow while it re-centres.</summary>
        /// <remarks>
        /// Ten percent is invisible as speed — a body sprinting at 6.5 m/s reads as 7.1 — and still
        /// recovers three ticks a second.
        /// </remarks>
        public const double MaxRateAdjust = 0.1;

        /// <summary>Playback-rate change per tick of average lead error.</summary>
        /// <remarks>
        /// Chosen together with <see cref="LeadSmoothing"/> for a damped return: at 20 arrivals a
        /// second the lead average has a time constant of about half a second, and this gain gives
        /// the loop a damping ratio near 0.65 — it settles in about two seconds and overshoots by a
        /// few percent of a tick, rather than oscillating around the delay.
        /// </remarks>
        public const double RateGainPerTick = 0.04;

        /// <summary>Weight of each new arrival in the lead average.</summary>
        public const double LeadSmoothing = 0.1;

        /// <summary>
        /// An arrival further than this from <see cref="DelayTicks"/> ahead of the render time
        /// re-anchors the clock instead of steering it.
        /// </summary>
        /// <remarks>
        /// Steering recovers at most three ticks a second, so an error this large would take
        /// seconds of visibly wrong timing — and of lag compensation resolving against a past the
        /// player was not looking at — to work off. One jump is the better trade.
        /// </remarks>
        public const double SnapThresholdTicks = 5.0;

        private readonly double _ticksPerSecond;

        private bool _anchored;
        private bool _timed;
        private double _lastNowSeconds;
        private double _renderTick;
        private uint _newestTick;
        private double _leadAverage;

        /// <summary>A clock at the protocol's simulation rate.</summary>
        public InterpolationClock()
            : this(ProtocolConstants.SIM_TICK_RATE)
        {
        }

        /// <summary>A clock at an explicit rate. For tests that want round numbers.</summary>
        public InterpolationClock(int ticksPerSecond)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            _ticksPerSecond = ticksPerSecond;
            Reset();
        }

        /// <summary>Whether a snapshot has arrived since the last <see cref="Reset"/>.</summary>
        public bool IsRunning => _anchored;

        /// <summary>The tick being drawn right now. Zero until the first snapshot.</summary>
        public double RenderTick => _renderTick;

        /// <summary>The playback rate currently applied, 1 ± <see cref="MaxRateAdjust"/>.</summary>
        public double TimeScale { get; private set; }

        /// <summary>The smoothed lead of arrivals over the render time, in ticks.</summary>
        public double AverageLeadTicks => _leadAverage;

        /// <summary>Times an arrival re-anchored the clock. The first snapshot does not count.</summary>
        public long Snaps { get; private set; }

        /// <summary>Forgets everything. Call with the interpolation buffers, on disconnect.</summary>
        public void Reset()
        {
            _anchored = false;
            _timed = false;
            _lastNowSeconds = 0.0;
            _renderTick = 0.0;
            _newestTick = 0;
            _leadAverage = DelayTicks;
            TimeScale = 1.0;
            Snaps = 0;
        }

        /// <summary>
        /// Records that the snapshot for <paramref name="serverTick"/> has been applied.
        /// </summary>
        /// <remarks>
        /// Call <see cref="AdvanceTo"/> for the current frame BEFORE the transport is polled, so
        /// the lead is measured against the render time of the frame the snapshot arrived in.
        /// A tick not newer than one already seen is ignored, as the buffers ignore it.
        /// </remarks>
        public void OnSnapshot(uint serverTick)
        {
            if (!_anchored)
            {
                _anchored = true;
                _newestTick = serverTick;
                Anchor(serverTick);
                return;
            }

            if (!SequenceMath.IsNewer32(serverTick, _newestTick)) return;
            _newestTick = serverTick;

            double lead = serverTick - _renderTick;
            if (Math.Abs(lead - DelayTicks) > SnapThresholdTicks)
            {
                Snaps++;
                Anchor(serverTick);
                return;
            }

            _leadAverage += (lead - _leadAverage) * LeadSmoothing;

            double correction = (_leadAverage - DelayTicks) * RateGainPerTick;
            if (correction > MaxRateAdjust) correction = MaxRateAdjust;
            else if (correction < -MaxRateAdjust) correction = -MaxRateAdjust;
            TimeScale = 1.0 + correction;
        }

        /// <summary>
        /// Moves the render time to <paramref name="nowSeconds"/> and returns it.
        /// </summary>
        /// <param name="nowSeconds">
        /// A monotonic clock in seconds — the frame's unscaled time. The remote world does not
        /// slow down for a local pause or a slow-motion key, so neither may its clock.
        /// </param>
        /// <remarks>
        /// Idempotent for a repeated time, and a time that went backwards advances nothing.
        /// </remarks>
        public double AdvanceTo(double nowSeconds)
        {
            if (!_timed)
            {
                _timed = true;
                _lastNowSeconds = nowSeconds;
                return _renderTick;
            }

            double elapsed = nowSeconds - _lastNowSeconds;
            if (!(elapsed > 0.0)) return _renderTick;
            _lastNowSeconds = nowSeconds;

            if (!_anchored) return _renderTick;

            _renderTick += elapsed * _ticksPerSecond * TimeScale;
            if (_renderTick > _newestTick) _renderTick = _newestTick;
            return _renderTick;
        }

        private void Anchor(uint serverTick)
        {
            _renderTick = (double)serverTick - DelayTicks;
            _leadAverage = DelayTicks;
            TimeScale = 1.0;
        }
    }
}
