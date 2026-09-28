// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// One line of frame-time statistics every <see cref="WindowSeconds"/>, when
    /// <c>IRONFRONT_LOG_FRAMES=1</c>. Silent otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The 2026-09-23 report was "everything stutters for about the first
    /// five minutes of a match, then it is fine": bots, the local player, animations, vehicles.
    /// Neither the server nor the client logged a single number about its own frame time, so an
    /// overload could be neither confirmed nor excluded, and a stutter that heals itself cannot be
    /// diagnosed from one screenshot. This makes both halves answer the same question on the same
    /// clock: how long were the frames in this window, and did the server keep its tick rate.
    /// </para>
    /// <para>
    /// <b>Both roles, one component.</b> A client line and a server line differ only in the
    /// <c>ticks</c> column, which reads the server's own tick counter; a client prints the
    /// prediction clock's instead. Comparing minute 1 with minute 6 of the same run, on both
    /// sides, is what separates "the server fell behind" from "this client's frames got long".
    /// </para>
    /// <para>
    /// <b>By env var, not a define</b>, like <c>IRONFRONT_LOG_SHOTS</c> and
    /// <c>IRONFRONT_LOG_MOVE</c>: a built player can be asked without a rebuild.
    /// </para>
    /// </remarks>
    public sealed class FrameTimeLog : MonoBehaviour
    {
        /// <summary>Seconds per printed window.</summary>
        public const float WindowSeconds = 5f;

        /// <summary>Frames longer than this are counted as hitches.</summary>
        public const float HitchMilliseconds = 50f;

        private const int MaxFramesPerWindow = 4096;

        private readonly float[] _frameMs = new float[MaxFramesPerWindow];
        private int _frames;
        private float _windowStart;
        private int _gcAtWindowStart;
        private long _ticksAtWindowStart;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            if (Environment.GetEnvironmentVariable("IRONFRONT_LOG_FRAMES") != "1") return;

            var host = new GameObject("[FrameTimeLog]");
            DontDestroyOnLoad(host);
            host.AddComponent<FrameTimeLog>();
        }

        private void OnEnable()
        {
            StartWindow();
            Debug.Log($"[frames] logging every {WindowSeconds:F0}s; hitch = frame over "
                      + $"{HitchMilliseconds:F0} ms; ticks target {ProtocolConstants.SIM_TICK_RATE}/s");
        }

        private void Update()
        {
            if (_frames < MaxFramesPerWindow) _frameMs[_frames++] = Time.unscaledDeltaTime * 1000f;

            float elapsed = Time.realtimeSinceStartup - _windowStart;
            if (elapsed < WindowSeconds) return;

            Print(elapsed);
            StartWindow();
        }

        private void StartWindow()
        {
            _frames = 0;
            _windowStart = Time.realtimeSinceStartup;
            _gcAtWindowStart = GC.CollectionCount(0);
            _ticksAtWindowStart = CurrentTicks();
        }

        private void Print(float elapsed)
        {
            if (_frames == 0) return;

            Array.Sort(_frameMs, 0, _frames);
            float sum = 0f;
            int hitches = 0;
            for (int i = 0; i < _frames; i++)
            {
                sum += _frameMs[i];
                if (_frameMs[i] > HitchMilliseconds) hitches++;
            }

            float p99 = _frameMs[Math.Min(_frames - 1, (int)(_frames * 0.99f))];
            float max = _frameMs[_frames - 1];
            long ticks = CurrentTicks() - _ticksAtWindowStart;

            Debug.Log(
                $"[frames] t={Time.realtimeSinceStartup:F0}s role={Role()} fps={_frames / elapsed:F1} "
                + $"mean={sum / _frames:F1}ms p99={p99:F1}ms max={max:F1}ms hitches={hitches} "
                + $"gc0={GC.CollectionCount(0) - _gcAtWindowStart} "
                + $"ticks/s={ticks / elapsed:F1} mem={GC.GetTotalMemory(false) / (1024 * 1024)}MB");
        }

        private static long CurrentTicks()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop != null) return loop.CurrentTick;

            NetPredictionClock clock = NetPredictionClock.Current;
            return clock != null ? clock.TickCount : 0;
        }

        private static string Role()
            => NetContext.IsServer ? "server" : NetContext.IsClient ? "client" : "offline";
    }
}
#endif
