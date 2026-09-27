using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The render clock every remote body is drawn at. See <see cref="InterpolationClock"/>.
    /// </summary>
    /// <remarks>
    /// The stream is the real one: <see cref="ServerTickScheduler"/> decides which ticks carry a
    /// snapshot, so the 2,1,2,1 stride these tests run against is the one the server produces,
    /// not a copy of it. Client frames call <see cref="InterpolationClock.AdvanceTo"/> and then
    /// deliver what has arrived, which is the order <c>NetClientBootstrap.Update</c> uses.
    /// </remarks>
    public sealed class InterpolationClockTests
    {
        /// <summary>
        /// The render tick never steps backwards, at any frame rate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The 2026-09-27 report: bots and vehicles moved "like they teleport frame by frame". The
        /// render tick was <c>newestTick + NetPredictionClock.Alpha - DelayTicks</c>; Alpha wraps at
        /// 30 Hz while the newest tick moves at 20, so it threw every remote body back a tick at
        /// least every 100 ms.
        /// </para>
        /// <para>
        /// <b>Mutation to run before trusting this test:</b> in <c>AdvanceTo</c>, replace the
        /// integration with <c>_renderTick = _newestTick + (nowSeconds * _ticksPerSecond) % 1.0 -
        /// DelayTicks</c> — the old formula. This must go RED. Observed red on 2026-09-27.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(45.0)]
        [InlineData(60.0)]
        [InlineData(144.0)]
        public void TheRenderTickNeverStepsBackwards(double clientHz)
        {
            var clock = new InterpolationClock();
            List<Arrival> arrivals = ServerStream(seconds: 5.0, serverHz: 60.0, latency: Steady(0.030));

            Playback run = Play(clock, arrivals, seconds: 5.0, clientHz);

            for (int i = 1; i < run.Frames.Count; i++)
            {
                double step = run.Frames[i].Render - run.Frames[i - 1].Render;
                Assert.True(step >= -1e-9,
                    $"at {clientHz} fps the render tick went from {run.Frames[i - 1].Render:0.000} to "
                    + $"{run.Frames[i].Render:0.000} at t={run.Frames[i].Time:0.000}s");
            }
        }

        /// <summary>
        /// In a steady stream the render time settles <see cref="InterpolationClock.DelayTicks"/>
        /// behind the arrivals, and the buffer in front of it never runs dry.
        /// </summary>
        [Fact]
        public void ItSettlesDelayTicksBehindTheArrivals()
        {
            var clock = new InterpolationClock();
            List<Arrival> arrivals = ServerStream(seconds: 8.0, serverHz: 60.0, latency: Jittered(0.040, 0.010));

            Playback run = Play(clock, arrivals, seconds: 8.0, clientHz: 144.0);

            double sum = 0.0;
            int count = 0;
            foreach (Measured arrival in run.Arrivals)
            {
                if (arrival.Time < 4.0) continue;

                Assert.InRange(arrival.Lead, InterpolationClock.DelayTicks - 1.0, InterpolationClock.DelayTicks + 1.0);
                sum += arrival.Lead;
                count++;
            }

            Assert.True(count > 50, $"only {count} arrivals were measured");
            Assert.InRange(sum / count, InterpolationClock.DelayTicks - 0.25, InterpolationClock.DelayTicks + 0.25);

            foreach (Frame frame in run.Frames)
            {
                if (frame.Time < 4.0) continue;
                Assert.True(frame.Render < frame.Newest,
                    $"the render tick reached the newest snapshot ({frame.Newest}) at t={frame.Time:0.000}s: "
                    + "the buffer ran dry in a steady stream");
            }
        }

        /// <summary>
        /// When the arrivals shift, the clock returns to the delay by changing speed a little, not
        /// by jumping.
        /// </summary>
        [Fact]
        public void ALatencyStepIsAbsorbedAtABoundedRate()
        {
            var clock = new InterpolationClock();

            // Two ticks of extra latency from t = 3 s: every later snapshot lands 67 ms later.
            List<Arrival> arrivals = ServerStream(
                seconds: 9.0, serverHz: 60.0,
                latency: (sentAt, _) => sentAt < 3.0 ? 0.030 : 0.030 + 2.0 / ProtocolConstants.SIM_TICK_RATE);

            Playback run = Play(clock, arrivals, seconds: 9.0, clientHz: 60.0);

            Assert.Equal(0, clock.Snaps);

            for (int i = 1; i < run.Frames.Count; i++)
            {
                Assert.InRange(run.Frames[i].TimeScale, 1.0 - InterpolationClock.MaxRateAdjust - 1e-9,
                    1.0 + InterpolationClock.MaxRateAdjust + 1e-9);
                Assert.True(run.Frames[i].Render >= run.Frames[i - 1].Render - 1e-9,
                    $"the render tick stepped back at t={run.Frames[i].Time:0.000}s");
            }

            double sum = 0.0;
            int count = 0;
            foreach (Measured arrival in run.Arrivals)
            {
                if (arrival.Time < 8.0) continue;
                sum += arrival.Lead;
                count++;
            }

            Assert.True(count > 10);
            Assert.InRange(sum / count, InterpolationClock.DelayTicks - 0.3, InterpolationClock.DelayTicks + 0.3);
        }

        [Fact]
        public void AnArrivalFarOffTheDelayReanchorsTheClock()
        {
            var clock = new InterpolationClock();
            clock.OnSnapshot(100);
            clock.AdvanceTo(0.0);
            clock.AdvanceTo(0.1);

            clock.OnSnapshot(120);

            Assert.Equal(1, clock.Snaps);
            Assert.Equal(120.0 - InterpolationClock.DelayTicks, clock.RenderTick, 9);
        }

        /// <summary>
        /// Past the newest snapshot there is nothing to draw, so the clock waits there instead of
        /// running on and leaving the stream behind by however long the stall lasted.
        /// </summary>
        [Fact]
        public void ItNeverRunsPastTheNewestSnapshot()
        {
            var clock = new InterpolationClock();
            clock.OnSnapshot(100);
            clock.AdvanceTo(0.0);

            Assert.Equal(100.0, clock.AdvanceTo(10.0), 9);

            clock.OnSnapshot(101);
            double resumed = clock.AdvanceTo(10.1);

            Assert.Equal(0, clock.Snaps);
            Assert.InRange(resumed, 100.0, 101.0);
        }

        [Fact]
        public void AdvanceToIsIdempotentAndIgnoresTimeGoingBackwards()
        {
            var clock = new InterpolationClock();
            clock.OnSnapshot(100);
            clock.AdvanceTo(1.0);

            // 50 ms is a tick and a half: short of the newest snapshot, so the cap is not in play.
            double first = clock.AdvanceTo(1.05);
            Assert.Equal(first, clock.AdvanceTo(1.05));
            Assert.Equal(first, clock.AdvanceTo(1.02));
            Assert.Equal(100.0 - InterpolationClock.DelayTicks + 0.05 * ProtocolConstants.SIM_TICK_RATE, first, 9);
        }

        [Fact]
        public void NothingAdvancesBeforeTheFirstSnapshot()
        {
            var clock = new InterpolationClock();
            clock.AdvanceTo(0.0);

            Assert.Equal(0.0, clock.AdvanceTo(5.0));
            Assert.False(clock.IsRunning);
        }

        /// <summary>
        /// The render delay is the lag compensator's, to the millisecond.
        /// </summary>
        /// <remarks>
        /// <c>LagCompensator.RewindTicks</c> resolves every shot against the world as it was
        /// <see cref="ProtocolConstants.INTERP_BUFFER_MS"/> before the server's present (plus half
        /// the RTT). A client drawing any other distance into the past sees every moving target
        /// somewhere the server does not, consistently to one side.
        /// </remarks>
        [Fact]
        public void TheDelayIsTheLagCompensatorsInterpolationBuffer()
        {
            Assert.Equal(
                ProtocolConstants.INTERP_BUFFER_MS,
                InterpolationClock.DelayTicks * 1000.0 / ProtocolConstants.SIM_TICK_RATE,
                6);
        }

        [Fact]
        public void ResetForgetsEverything()
        {
            var clock = new InterpolationClock();
            clock.OnSnapshot(100);
            clock.AdvanceTo(0.0);
            clock.AdvanceTo(0.5);
            clock.OnSnapshot(200);

            clock.Reset();

            Assert.False(clock.IsRunning);
            Assert.Equal(0.0, clock.RenderTick);
            Assert.Equal(0, clock.Snaps);
            Assert.Equal(1.0, clock.TimeScale);
            Assert.Equal(InterpolationClock.DelayTicks, clock.AverageLeadTicks);
        }

        // ------------------------------------------------------------------ harness

        private readonly struct Arrival
        {
            public readonly uint Tick;
            public readonly double At;

            public Arrival(uint tick, double at)
            {
                Tick = tick;
                At = at;
            }
        }

        private readonly struct Frame
        {
            public readonly double Time;
            public readonly double Render;
            public readonly uint Newest;
            public readonly double TimeScale;

            public Frame(double time, double render, uint newest, double timeScale)
            {
                Time = time;
                Render = render;
                Newest = newest;
                TimeScale = timeScale;
            }
        }

        private readonly struct Measured
        {
            public readonly double Time;
            public readonly double Lead;

            public Measured(double time, double lead)
            {
                Time = time;
                Lead = lead;
            }
        }

        private sealed class Playback
        {
            public readonly List<Frame> Frames = new List<Frame>();
            public readonly List<Measured> Arrivals = new List<Measured>();
        }

        /// <summary>
        /// The server's snapshot stream: ticks and send times from the real scheduler, driven by
        /// server frames at <paramref name="serverHz"/>, each landing after <paramref name="latency"/>.
        /// </summary>
        private static List<Arrival> ServerStream(
            double seconds, double serverHz, Func<double, int, double> latency)
        {
            var scheduler = new ServerTickScheduler();
            var arrivals = new List<Arrival>();
            int sent = 0;
            int frames = (int)(seconds * serverHz);

            for (int f = 0; f <= frames; f++)
            {
                double now = f / serverHz;
                int owed = scheduler.Advance(now * 1000.0);
                for (int k = 0; k < owed; k++)
                {
                    uint tick = scheduler.BeginTick();
                    if (scheduler.ShouldSendSnapshot())
                        arrivals.Add(new Arrival(tick, now + latency(now, sent++)));
                }
            }

            arrivals.Sort((a, b) => a.At.CompareTo(b.At));
            return arrivals;
        }

        private static Func<double, int, double> Steady(double seconds) => (_, _) => seconds;

        /// <summary>A base latency plus a deterministic 0..<paramref name="spread"/> jitter.</summary>
        private static Func<double, int, double> Jittered(double baseSeconds, double spread)
        {
            uint state = 0x9E3779B9u;
            return (_, _) =>
            {
                state = state * 1664525u + 1013904223u;
                return baseSeconds + spread * ((state >> 8) / 16777216.0);
            };
        }

        /// <summary>
        /// Client frames at <paramref name="clientHz"/>: advance the clock, deliver what has
        /// arrived, read the render tick — <c>NetClientBootstrap.Update</c>, then the readers.
        /// </summary>
        private static Playback Play(
            InterpolationClock clock, List<Arrival> arrivals, double seconds, double clientHz)
        {
            var run = new Playback();
            int next = 0;
            uint newest = 0;
            bool any = false;
            int frames = (int)(seconds * clientHz);

            for (int f = 0; f <= frames; f++)
            {
                double now = f / clientHz;
                clock.AdvanceTo(now);

                while (next < arrivals.Count && arrivals[next].At <= now)
                {
                    uint tick = arrivals[next].Tick;
                    if (any && clock.IsRunning) run.Arrivals.Add(new Measured(now, tick - clock.RenderTick));

                    clock.OnSnapshot(tick);
                    if (!any || SequenceMath.IsNewer32(tick, newest)) newest = tick;
                    any = true;
                    next++;
                }

                if (any) run.Frames.Add(new Frame(now, clock.AdvanceTo(now), newest, clock.TimeScale));
            }

            return run;
        }
    }
}
