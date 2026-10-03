// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Records the Unity profiler to a <c>.raw</c> file for a few hundred frames, at a set time,
    /// with no Editor attached: <c>IRONFRONT_PROFILE_AT</c> seconds after start-up, for
    /// <c>IRONFRONT_PROFILE_FRAMES</c> frames (default 300), into <c>IRONFRONT_PROFILE_FILE</c>
    /// (default <c>Logs/profile-&lt;pid&gt;.raw</c> beside the player's logs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Attaching the Editor's profiler to a player mid-match stalls the player while it
    /// streams, and the Editor itself competes for the CPU being measured. A capture written by the
    /// player from a set moment is the same data without either: open it later in the Profiler
    /// window, or <c>ProfilerDriver.LoadProfile</c> it.
    /// </para>
    /// <para>
    /// <b>Development players only.</b> A release player has no profiler; there this says so once
    /// and does nothing.
    /// </para>
    /// </remarks>
    public sealed class ProfileCapture : MonoBehaviour
    {
        private const int DefaultFrames = 300;

        private float _startAt;
        private int _frames;
        private string _file;
        private int _recorded = -1;

        /// <summary>
        /// With <c>IRONFRONT_PROFILE_WHEN_BODIES</c>, the capture waits past its start time until the
        /// scene holds at least this many rigidbodies (phase P32): a 100-bot match's physics peaks
        /// when the corpses of a fight pile up, which no fixed time can aim at.
        /// </summary>
        private int _whenBodies;
        private float _nextBodyCheck;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            string at = Environment.GetEnvironmentVariable("IRONFRONT_PROFILE_AT");
            if (string.IsNullOrEmpty(at)) return;
            if (!float.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out float startAt))
            {
                Debug.LogError($"[profile] IRONFRONT_PROFILE_AT is '{at}', not a number of seconds; nothing will be recorded.");
                return;
            }
            if (!Profiler.supported)
            {
                Debug.LogWarning("[profile] this player has no profiler (not a development build); nothing will be recorded.");
                return;
            }

            string frames = Environment.GetEnvironmentVariable("IRONFRONT_PROFILE_FRAMES");
            string file = Environment.GetEnvironmentVariable("IRONFRONT_PROFILE_FILE");

            // Built in code like FrameTimeLog's host: a diagnostics tool that exists only when the
            // env var asks, with no scene or prefab of its own to be authored on.
            var host = new GameObject("[ProfileCapture]");
            DontDestroyOnLoad(host);
            ProfileCapture capture = host.AddComponent<ProfileCapture>();
            capture._startAt = startAt;
            capture._whenBodies = int.TryParse(Environment.GetEnvironmentVariable("IRONFRONT_PROFILE_WHEN_BODIES"), out int bodies) ? bodies : 0;
            capture._frames = int.TryParse(frames, out int count) && count > 0 ? count : DefaultFrames;
            capture._file = string.IsNullOrEmpty(file)
                ? Path.Combine(Application.persistentDataPath, "Logs", $"profile-{System.Diagnostics.Process.GetCurrentProcess().Id}.raw")
                : file;
            Debug.Log($"[profile] will record {capture._frames} frames at t={startAt:F0}s into {capture._file}");
        }

        private void Update()
        {
            if (_recorded < 0)
            {
                if (Time.realtimeSinceStartup < _startAt) return;
                if (_whenBodies > 0)
                {
                    if (Time.realtimeSinceStartup < _nextBodyCheck) return;
                    _nextBodyCheck = Time.realtimeSinceStartup + 1f;
                    int bodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length;
                    if (bodies < _whenBodies) return;
                    Debug.Log($"[profile] {bodies} rigidbodies in the scene, at least {_whenBodies}: recording");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                Profiler.logFile = _file;
                Profiler.enableBinaryLog = true;
                Profiler.enabled = true;
                _recorded = 0;
                Debug.Log($"[profile] recording from t={Time.realtimeSinceStartup:F1}s");
                return;
            }

            if (++_recorded < _frames) return;
            Profiler.enabled = false;
            Profiler.enableBinaryLog = false;
            Profiler.logFile = string.Empty;
            Debug.Log($"[profile] wrote {_recorded} frames to {_file}");
            Destroy(gameObject);
        }
    }
}
#endif
