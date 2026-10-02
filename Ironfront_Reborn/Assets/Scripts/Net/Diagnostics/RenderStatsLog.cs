// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// One <c>[render]</c> line per <see cref="FrameTimeLog.WindowSeconds"/> window with what an
    /// average frame drew, when <c>IRONFRONT_LOG_FRAMES=1</c>. Silent otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> Rendering held 10-15 ms of a 30-40 ms frame in a 100-bot Forest
    /// Lake match (<c>[loop]</c> lines, release player, 2026-10-02), almost all of it main-thread
    /// work that grows with what is drawn -- culling, preparing and submitting renderers. Which
    /// of draw calls, shadow casters or skinned bodies drives it is a count, and the release
    /// player can report it: Unity's render counters are recorded in non-development builds too.
    /// </para>
    /// <para>
    /// <b>Averaged over the window</b>, each counter read once a frame from its recorder's last
    /// value.
    /// </para>
    /// </remarks>
    public sealed class RenderStatsLog : MonoBehaviour
    {
        // Unity's own counter names, as the Profiler's Rendering module shows them.
        private static readonly string[] Counters =
        {
            "Batches Count",
            "SetPass Calls Count",
            "Draw Calls Count",
            "Shadow Casters Count",
            "Visible Skinned Meshes Count",
            "Triangles Count",
        };

        private static readonly string[] Labels = { "batches", "setpass", "draws", "shadowCasters", "skinned", "tris" };

        private readonly ProfilerRecorder[] _recorders = new ProfilerRecorder[Counters.Length];
        private readonly double[] _sums = new double[Counters.Length];
        private int _frames;
        private float _windowStart;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            if (Environment.GetEnvironmentVariable("IRONFRONT_LOG_FRAMES") != "1") return;

            // Built in code like FrameTimeLog's host: a diagnostics tool that exists only when the
            // env var asks, with no scene or prefab of its own to be authored on.
            var host = new GameObject("[RenderStatsLog]");
            DontDestroyOnLoad(host);
            host.AddComponent<RenderStatsLog>();
        }

        private void OnEnable()
        {
            for (int i = 0; i < Counters.Length; i++)
                _recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Render, Counters[i]);
            _windowStart = Time.realtimeSinceStartup;
        }

        private void OnDisable()
        {
            for (int i = 0; i < _recorders.Length; i++) _recorders[i].Dispose();
        }

        private void LateUpdate()
        {
            for (int i = 0; i < _recorders.Length; i++) _sums[i] += _recorders[i].LastValue;
            _frames++;

            if (Time.realtimeSinceStartup - _windowStart < FrameTimeLog.WindowSeconds) return;

            var line = new StringBuilder(160);
            line.Append("[render] ").Append(_frames).Append(" frames, per frame:");
            for (int i = 0; i < _sums.Length; i++)
            {
                line.Append(' ').Append(Labels[i]).Append('=');
                line.Append(_recorders[i].Valid ? (_sums[i] / Math.Max(1, _frames)).ToString("F0") : "n/a");
            }
            Debug.Log(line.ToString());

            Array.Clear(_sums, 0, _sums.Length);
            _frames = 0;
            _windowStart = Time.realtimeSinceStartup;
        }
    }
}
#endif
