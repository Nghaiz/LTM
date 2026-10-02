// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// One <c>[physics]</c> line per <see cref="FrameTimeLog.WindowSeconds"/> window counting what
    /// the physics step has to simulate, when <c>IRONFRONT_LOG_FRAMES=1</c>. Silent otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> In a 100-bot Forest Lake match the client's physics grew from about
    /// 3.5 ms a frame early on to 8-12 ms mid-match (<c>[loop]</c> lines, release player,
    /// 2026-10-02), and the profiler said only that the time went into <c>PxScene.simulate</c>
    /// on the main thread. Corpses were the obvious suspect and settling them did not move the
    /// number. What the step simulates is a fact the scene can state directly: how many bodies,
    /// how many awake, and whose.
    /// </para>
    /// <para>
    /// <b>Grouped by root object</b>, with digits and <c>(Clone)</c> stripped, so "Corpse 17" and
    /// "Corpse 18" count together; each group prints as <c>name=bodies/awake/kinematic</c>, the
    /// most awake first.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Three scene-wide searches once per window -- under a millisecond every five
    /// seconds, and never in a shipping build.
    /// </para>
    /// </remarks>
    public sealed class PhysicsCensus : MonoBehaviour
    {
        private const int GroupsPerLine = 6;

        private readonly Dictionary<string, int[]> _groups = new Dictionary<string, int[]>();
        private readonly StringBuilder _name = new StringBuilder(64);
        private float _nextAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            if (Environment.GetEnvironmentVariable("IRONFRONT_LOG_FRAMES") != "1") return;

            // Built in code like FrameTimeLog's host: a diagnostics tool that exists only when the
            // env var asks, with no scene or prefab of its own to be authored on.
            var host = new GameObject("[PhysicsCensus]");
            DontDestroyOnLoad(host);
            host.AddComponent<PhysicsCensus>();
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextAt) return;
            _nextAt = Time.realtimeSinceStartup + FrameTimeLog.WindowSeconds;
            Debug.Log(Describe());
        }

        private string Describe()
        {
            Rigidbody[] bodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            int awake = 0, kinematic = 0, interpolated = 0;
            _groups.Clear();
            foreach (Rigidbody body in bodies)
            {
                bool isKinematic = body.isKinematic;
                bool isAwake = !isKinematic && !body.IsSleeping();
                if (isKinematic) kinematic++;
                if (isAwake) awake++;
                if (body.interpolation != RigidbodyInterpolation.None) interpolated++;

                string group = GroupOf(body.transform.root.name);
                if (!_groups.TryGetValue(group, out int[] counts)) _groups[group] = counts = new int[3];
                counts[0]++;
                if (isAwake) counts[1]++;
                if (isKinematic) counts[2]++;
            }

            int wheels = FindObjectsByType<WheelCollider>(FindObjectsSortMode.None).Length;
            int cloth = FindObjectsByType<Cloth>(FindObjectsSortMode.None).Length;

            var line = new StringBuilder(256);
            line.Append("[physics] bodies ").Append(bodies.Length).Append(" awake ").Append(awake)
                .Append(" kinematic ").Append(kinematic).Append(" interpolated ").Append(interpolated)
                .Append(" wheels ").Append(wheels).Append(" cloth ").Append(cloth).Append(':');

            // Selection of the heaviest few, not a sort: the same as HitchAttribution.
            var taken = new HashSet<string>();
            for (int n = 0; n < GroupsPerLine; n++)
            {
                string best = null;
                int[] bestCounts = null;
                foreach (KeyValuePair<string, int[]> pair in _groups)
                {
                    if (taken.Contains(pair.Key)) continue;
                    int[] c = pair.Value;
                    if (bestCounts == null || c[1] > bestCounts[1] || (c[1] == bestCounts[1] && c[0] > bestCounts[0]))
                    {
                        best = pair.Key;
                        bestCounts = c;
                    }
                }
                if (best == null) break;
                taken.Add(best);
                line.Append(' ').Append(best).Append('=').Append(bestCounts[0]).Append('/')
                    .Append(bestCounts[1]).Append('/').Append(bestCounts[2]);
            }

            return line.ToString();
        }

        // "Corpse 17" and "Corpse 18" are one group; so are "Jeep (Clone)" and "Jeep (3)".
        private string GroupOf(string rootName)
        {
            _name.Clear();
            foreach (char c in rootName.Replace("(Clone)", string.Empty))
            {
                if (char.IsDigit(c) || c == '(' || c == ')') continue;
                _name.Append(c == ' ' ? '_' : c);
            }
            return _name.ToString().Trim('_');
        }
    }
}
#endif
