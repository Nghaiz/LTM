// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using Debug = UnityEngine.Debug;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Names the PlayerLoop systems that spent a long frame, one <c>[hitch]</c> line per frame
    /// over <see cref="FrameTimeLog.HitchMilliseconds"/>, when <c>IRONFRONT_LOG_FRAMES=1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> <c>[frames]</c> says a window held long frames, and a 2026-09-27
    /// playtest logged 91 and 120 frames of 170-860 ms across two clients -- with nothing to say
    /// whether a frame went to scripts, physics, animation, asset loading, the garbage collector
    /// or the GPU. Each of those has a different fix, and guessing between them is how a hitch
    /// gets "fixed" three times. This times every second-level PlayerLoop system (the ones the
    /// Profiler shows as <c>Update.ScriptRunBehaviourUpdate</c>,
    /// <c>FixedUpdate.PhysicsFixedUpdate</c>, <c>PostLateUpdate.FinishFrameRendering</c> and
    /// the rest) and prints the heaviest of them for any frame over the line.
    /// </para>
    /// <para>
    /// <b>Works in a release player.</b> It is two <see cref="Stopwatch.GetTimestamp"/> calls
    /// around each system, so it needs neither a development build nor the Profiler -- which a
    /// playtester's build does not have and a 240 fps capture cannot afford (a four-minute
    /// capture was 10 GB).
    /// </para>
    /// <para>
    /// <b>Rate-limited per window</b>, so a machine that is hitching every frame writes a
    /// bounded number of lines rather than drowning the log it is trying to explain.
    /// </para>
    /// </remarks>
    public static class HitchAttribution
    {
        private const int MaxLinesPerWindow = 12;
        private const int SystemsPerLine = 5;
        private const float WindowSeconds = FrameTimeLog.WindowSeconds;

        private static readonly List<string> Names = new List<string>();
        private static double[] _elapsedMs = Array.Empty<double>();
        private static int[] _calls = Array.Empty<int>();
        private static long[] _startedAt = Array.Empty<long>();

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static long _frameStartedAt;
        private static int _gcAtFrameStart;
        private static int _linesThisWindow;
        private static float _windowStartedAt;
        private static int _suppressedThisWindow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            if (Environment.GetEnvironmentVariable("IRONFRONT_LOG_FRAMES") != "1") return;
            if (Names.Count > 0) return;

            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            if (phases == null) return;

            for (int p = 0; p < phases.Length; p++)
            {
                PlayerLoopSystem phase = phases[p];
                PlayerLoopSystem[] systems = phase.subSystemList;
                if (systems == null) continue;

                var wrapped = new List<PlayerLoopSystem>(systems.Length * 3 + 1);

                // The very first system of the loop closes the previous frame and opens this one.
                if (p == 0) wrapped.Add(Marker(typeof(HitchAttribution), FrameBoundary));

                foreach (PlayerLoopSystem system in systems)
                {
                    int index = Names.Count;
                    Names.Add($"{phase.type?.Name}.{system.type?.Name}");
                    wrapped.Add(Marker(typeof(HitchAttribution), () => Begin(index)));
                    wrapped.Add(system);
                    wrapped.Add(Marker(typeof(HitchAttribution), () => End(index)));
                }

                phases[p].subSystemList = wrapped.ToArray();
            }

            _elapsedMs = new double[Names.Count];
            _calls = new int[Names.Count];
            _startedAt = new long[Names.Count];

            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);

            _frameStartedAt = Stopwatch.GetTimestamp();
            _gcAtFrameStart = GC.CollectionCount(0);
            _windowStartedAt = Time.realtimeSinceStartup;

            Debug.Log($"[hitch] timing {Names.Count} PlayerLoop systems; a frame over "
                      + $"{FrameTimeLog.HitchMilliseconds:F0} ms prints its {SystemsPerLine} heaviest");
        }

        private static PlayerLoopSystem Marker(Type type, PlayerLoopSystem.UpdateFunction update)
            => new PlayerLoopSystem { type = type, updateDelegate = update };

        private static void Begin(int index) => _startedAt[index] = Stopwatch.GetTimestamp();

        private static void End(int index)
        {
            _elapsedMs[index] += (Stopwatch.GetTimestamp() - _startedAt[index]) * TicksToMs;
            _calls[index]++;
        }

        private static void FrameBoundary()
        {
            long now = Stopwatch.GetTimestamp();
            double frameMs = (now - _frameStartedAt) * TicksToMs;
            int gc = GC.CollectionCount(0);

            if (Time.realtimeSinceStartup - _windowStartedAt >= WindowSeconds)
            {
                if (_suppressedThisWindow > 0)
                    Debug.Log($"[hitch] {_suppressedThisWindow} more long frame(s) in the last "
                              + $"{WindowSeconds:F0} s were not itemised");
                _windowStartedAt = Time.realtimeSinceStartup;
                _linesThisWindow = 0;
                _suppressedThisWindow = 0;
            }

            if (frameMs > FrameTimeLog.HitchMilliseconds)
            {
                if (_linesThisWindow < MaxLinesPerWindow)
                {
                    _linesThisWindow++;
                    Debug.Log(Describe(frameMs, gc - _gcAtFrameStart));
                }
                else
                {
                    _suppressedThisWindow++;
                }
            }

            Array.Clear(_elapsedMs, 0, _elapsedMs.Length);
            Array.Clear(_calls, 0, _calls.Length);
            _frameStartedAt = now;
            _gcAtFrameStart = gc;
        }

        private static string Describe(double frameMs, int collections)
        {
            double accounted = 0;
            for (int i = 0; i < _elapsedMs.Length; i++) accounted += _elapsedMs[i];

            var line = new StringBuilder(256);
            line.Append("[hitch] ").Append(frameMs.ToString("F1")).Append(" ms at t=")
                .Append(Time.realtimeSinceStartup.ToString("F1")).Append("s gc0+").Append(collections)
                .Append(" loop=").Append(accounted.ToString("F1")).Append(" ms:");

            // Selection of the heaviest few, not a sort: this runs on the frame that is already late.
            var taken = new bool[_elapsedMs.Length];
            for (int n = 0; n < SystemsPerLine; n++)
            {
                int best = -1;
                for (int i = 0; i < _elapsedMs.Length; i++)
                {
                    if (taken[i]) continue;
                    if (best < 0 || _elapsedMs[i] > _elapsedMs[best]) best = i;
                }

                if (best < 0 || _elapsedMs[best] < 1.0) break;
                taken[best] = true;

                line.Append(' ').Append(Names[best]).Append('=').Append(_elapsedMs[best].ToString("F1"));
                if (_calls[best] > 1) line.Append('x').Append(_calls[best]);
            }

            return line.ToString();
        }
    }
}
#endif
