using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How long the last gap between two frames was: from the end of one frame's player loop to
    /// the start of the next, the time Unity spends outside the game (window messages, input,
    /// anything hooked into the window). In every build, for <see cref="TickDropReport"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase P35, finding 3.</b> A focused client once showed frames of 170 to 640 ms with only
    /// 20 to 60 ms inside the timed player loop, so most of the frame was spent between frames.
    /// It never reproduced (2026-10-09: a 20-minute focused match, no long frame after the map
    /// loaded), and the measuring tool that saw it exists only in diagnostics builds. Two markers
    /// in the loop -- first system of the first phase, last system of the last -- cost two clock
    /// reads a frame, so the release player can say where a long frame went the next time a
    /// player meets one.
    /// </para>
    /// <para>Not installed in batch mode: a headless server has no window to wait on.</para>
    /// </remarks>
    public static class PlayerLoopClock
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static long _loopEndedAt;

        /// <summary>Whether the markers are in the player loop.</summary>
        public static bool Installed { get; private set; }

        /// <summary>
        /// Milliseconds between the end of the previous frame's player loop and the start of this
        /// one, or -1 before the second frame (or when not installed).
        /// </summary>
        public static float LastGapMs { get; private set; } = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (Installed || Application.isBatchMode) return;

            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            if (phases == null || phases.Length == 0) return;

            phases[0].subSystemList = Wrap(phases[0].subSystemList, FrameStarted, atStart: true);
            int last = phases.Length - 1;
            phases[last].subSystemList = Wrap(phases[last].subSystemList, FrameEnded, atStart: false);

            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            Installed = true;
        }

        private static PlayerLoopSystem[] Wrap(
            PlayerLoopSystem[] systems, PlayerLoopSystem.UpdateFunction marker, bool atStart)
        {
            systems = systems ?? new PlayerLoopSystem[0];
            var wrapped = new PlayerLoopSystem[systems.Length + 1];
            var entry = new PlayerLoopSystem { type = typeof(PlayerLoopClock), updateDelegate = marker };
            if (atStart)
            {
                wrapped[0] = entry;
                System.Array.Copy(systems, 0, wrapped, 1, systems.Length);
            }
            else
            {
                System.Array.Copy(systems, wrapped, systems.Length);
                wrapped[systems.Length] = entry;
            }
            return wrapped;
        }

        private static void FrameStarted()
        {
            if (_loopEndedAt != 0) LastGapMs = (float)((Stopwatch.GetTimestamp() - _loopEndedAt) * TicksToMs);
        }

        private static void FrameEnded() => _loopEndedAt = Stopwatch.GetTimestamp();
    }
}
