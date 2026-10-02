#nullable enable

using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How much of the player's machine the game client takes: its job workers, and its frame rate
    /// when v-sync is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Job workers (owner report 2026-10-03: "the game takes so much CPU the machine lags").</b>
    /// Unity sizes its worker pool to the machine, 31 threads on a 32-thread CPU, and idle workers
    /// spin. The release v3.2.0 client on a Ryzen 9 7945HX, Forest Lake practice, 130 s in a match
    /// per run, measured with <c>IRONFRONT_LOG_FRAMES=1</c>: default pool 6.2 and 5.7 cores busy,
    /// 32-33 threads above 5% of a core, PlayerLoop 25.2 and 27.5 ms a frame; with 4 workers 3.3
    /// cores, 8 threads, PlayerLoop 15.9 ms; with 8 workers 2.7 cores, 12 threads, 19.2 ms.
    /// Physics went from 5.2-6.6 to 3.1 ms, animation from 3.1-3.5 to 1.8 ms and rendering from
    /// 5.8-6.3 to 4.3 ms: the work the workers do got faster with fewer of them. The headless
    /// server learnt the same lesson on the VM in 2026-09 (<c>NetServerBootstrap.HeadlessJobWorkers</c>).
    /// </para>
    /// <para>
    /// <b>Frame rate.</b> With v-sync off the title screen ran at 819 fps, a CPU core and the GPU
    /// spent drawing frames the display never shows. The cap is the display's refresh rate, never
    /// below <see cref="MinimumFrameCap"/>; with v-sync on Unity ignores it.
    /// </para>
    /// </remarks>
    public static class CpuBudgetRules
    {
        /// <summary>The job workers a rendering client keeps when the command line names none.</summary>
        public const int ClientJobWorkers = 4;

        /// <summary>The lowest frame cap, for a display that reports no refresh rate or a slow one.</summary>
        public const int MinimumFrameCap = 60;

        /// <summary>
        /// The worker count to run with: <paramref name="limit"/> at most, and never more than the
        /// <paramref name="current"/> pool, which on a small CPU is already below it.
        /// </summary>
        public static int CappedJobWorkers(int current, int limit) => current > limit ? limit : current;

        /// <summary>The frame cap while v-sync is off, for a display refreshing at <paramref name="refreshHz"/>.</summary>
        public static int ForegroundFrameCap(double refreshHz)
        {
            if (double.IsNaN(refreshHz) || refreshHz <= MinimumFrameCap) return MinimumFrameCap;
            return (int)Math.Round(refreshHz);
        }
    }
}
