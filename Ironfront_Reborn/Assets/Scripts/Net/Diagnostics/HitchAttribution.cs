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
    /// over <see cref="FrameTimeLog.HitchMilliseconds"/>, and where an ordinary frame's time
    /// went, one <c>[loop]</c> line per window, when <c>IRONFRONT_LOG_FRAMES=1</c>.
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
    /// <para>
    /// <b>The <c>[loop]</c> line</b> is the steady cost: each system's share of the window's
    /// average frame. A client held at 25 fps by 40 ms frames that never cross the hitch line
    /// prints no <c>[hitch]</c> at all, and the release player is exactly the build the Profiler
    /// cannot attach to.
    /// </para>
    /// </remarks>
    public static class HitchAttribution
    {
        private const int MaxLinesPerWindow = 12;
        private const int SystemsPerLine = 5;
        private const float WindowSeconds = FrameTimeLog.WindowSeconds;

        // How many systems a [loop] line names: enough to cover a frame's whole budget.
        private const int SystemsPerWindowLine = 8;

        private static readonly List<string> Names = new List<string>();
        private static double[] _elapsedMs = Array.Empty<double>();

        // The whole window's time per system, for the [loop] line: where an ORDINARY frame goes,
        // which the hitch lines never say. A release player cannot carry the Unity Profiler, so
        // this is how a release build is broken down (2026-10-02).
        private static double[] _windowMs = Array.Empty<double>();
        private static int _windowFrames;
        private static double _windowFrameMs;
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
                if (p == 0) wrapped.Add(Marker(FrameBoundary));

                foreach (PlayerLoopSystem system in systems)
                {
                    int index = Names.Count;
                    Names.Add($"{phase.type?.Name}.{system.type?.Name}");
                    wrapped.Add(Marker(() => Begin(index)));
                    wrapped.Add(system);
                    wrapped.Add(Marker(() => End(index)));
                }

                phases[p].subSystemList = wrapped.ToArray();
            }

            _elapsedMs = new double[Names.Count];
            _windowMs = new double[Names.Count];
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

        private static PlayerLoopSystem Marker(PlayerLoopSystem.UpdateFunction update)
            => new PlayerLoopSystem { type = typeof(HitchAttribution), updateDelegate = update };

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

            for (int i = 0; i < _elapsedMs.Length; i++) _windowMs[i] += _elapsedMs[i];
            _windowFrames++;
            _windowFrameMs += frameMs;

            if (Time.realtimeSinceStartup - _windowStartedAt >= WindowSeconds)
            {
                if (_suppressedThisWindow > 0)
                    Debug.Log($"[hitch] {_suppressedThisWindow} more long frame(s) in the last "
                              + $"{WindowSeconds:F0} s were not itemised");
                Debug.Log(DescribeWindow());
                Array.Clear(_windowMs, 0, _windowMs.Length);
                _windowFrames = 0;
                _windowFrameMs = 0;
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

        /// <summary>
        /// "[loop] 172 frames, frame 29.1 ms, loop 27.9 ms: Phase.System=avg ..." -- the heaviest
        /// systems of the window, as milliseconds per average frame.
        /// </summary>
        private static string DescribeWindow()
        {
            int frames = Math.Max(1, _windowFrames);
            double loop = 0;
            for (int i = 0; i < _windowMs.Length; i++) loop += _windowMs[i];

            var line = new StringBuilder(384);
            line.Append("[loop] ").Append(_windowFrames).Append(" frames, frame ")
                .Append((_windowFrameMs / frames).ToString("F1")).Append(" ms, loop ")
                .Append((loop / frames).ToString("F1")).Append(" ms:");

            var taken = new bool[_windowMs.Length];
            for (int n = 0; n < SystemsPerWindowLine; n++)
            {
                int best = -1;
                for (int i = 0; i < _windowMs.Length; i++)
                {
                    if (taken[i]) continue;
                    if (best < 0 || _windowMs[i] > _windowMs[best]) best = i;
                }

                if (best < 0 || _windowMs[best] / frames < 0.1) break;
                taken[best] = true;
                line.Append(' ').Append(Names[best]).Append('=').Append((_windowMs[best] / frames).ToString("F2"));
            }

            return line.ToString();
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
