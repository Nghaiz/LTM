// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Prices each heavy rendering feature in a live match (P31): with <c>IRONFRONT_GPU_PROBE=1</c>
    /// it turns one feature off for <see cref="StateSeconds"/>, puts it back, turns off the next,
    /// and logs <c>[ab] &lt;state&gt; at t=Ns</c> at every change, so the <c>[frames]</c> windows
    /// (GPU, main and render thread time) and <c>[render]</c> windows (batches, SetPass, draws)
    /// can be grouped by what was off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Besides the fixed features, it prices the CONTENT that draws (P33, owner 2026-10-05: the
    /// client's CPU goes to submitting draws). Found in the match itself rather than named here:
    /// every layer holding at least <see cref="MinGroupRenderers"/> renderers is culled from the
    /// camera (<c>nolayer:</c>, restored exactly), and the biggest groups of renderers sharing a
    /// root object -- a rock field, the outposts, every soldier -- are switched off (<c>off:</c>).
    /// The groups are listed once with their renderer, shadow-caster and material-slot counts.
    /// </para>
    /// <para>
    /// Measuring only: every state restores what it changed before the next begins, and the
    /// player sees the match change look while it runs. Never ship a build with it switched on.
    /// </para>
    /// </remarks>
    public sealed class GpuCostProbe : MonoBehaviour
    {
        private const float StateSeconds = 20f;

        /// <summary>The fewest renderers a layer or root group needs to be priced on its own.</summary>
        private const int MinGroupRenderers = 30;

        /// <summary>The most root groups priced, largest first.</summary>
        private const int MaxRootGroups = 10;

        private const string LayerPrefix = "nolayer:";
        private const string GroupPrefix = "off:";

        private static readonly string[] Features =
        {
            "base", "nodetails", "noshadows", "nomsaa", "nodepth", "notrees", "shadow150", "pixelerr8", "nopost",
        };

        private List<string> _states;
        private float _nextAt;
        private int _state = -1;
        private Action _undo;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            if (Environment.GetEnvironmentVariable("IRONFRONT_GPU_PROBE") != "1") return;
            // Built in code like FrameTimeLog's host: a measuring tool with no scene of its own.
            var host = new GameObject("[GpuCostProbe]");
            DontDestroyOnLoad(host);
            host.AddComponent<GpuCostProbe>();
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextAt) return;
            Terrain terrain = Terrain.activeTerrain;
            Camera camera = Camera.main;
            if (terrain == null || camera == null || Time.timeScale == 0f) return;

            _undo?.Invoke();
            _undo = null;
            _states ??= BuildStates();
            _state = (_state + 1) % _states.Count;
            _nextAt = Time.realtimeSinceStartup + StateSeconds;
            Apply(_states[_state], terrain, camera);
            Debug.Log($"[ab] {_states[_state]} at t={Time.realtimeSinceStartup:F0}s");
        }

        /// <summary>
        /// The fixed features, then every populous layer, then the largest root groups, as they
        /// stand the first time a match is up.
        /// </summary>
        private static List<string> BuildStates()
        {
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            var states = new List<string>(Features);

            foreach (IGrouping<int, Renderer> layer in renderers.GroupBy(r => r.gameObject.layer)
                         .Where(g => g.Count() >= MinGroupRenderers).OrderByDescending(g => g.Count()))
            {
                states.Add(LayerPrefix + LayerMask.LayerToName(layer.Key));
                Debug.Log($"[ab] layer {LayerMask.LayerToName(layer.Key)}: {Describe(layer)}");
            }

            foreach (IGrouping<string, Renderer> group in renderers.GroupBy(GroupOf)
                         .Where(g => g.Count() >= MinGroupRenderers).OrderByDescending(g => g.Count()).Take(MaxRootGroups))
            {
                states.Add(GroupPrefix + group.Key);
                Debug.Log($"[ab] group {group.Key}: {Describe(group)}");
            }
            return states;
        }

        private static string Describe(IEnumerable<Renderer> renderers)
        {
            Renderer[] all = renderers.ToArray();
            return $"{all.Length} renderers, {all.Count(r => r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)} shadow casters, "
                   + $"{all.Sum(r => r.sharedMaterials.Length)} material slots, "
                   + $"{all.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().Count()} materials";
        }

        /// <summary>
        /// The group a renderer belongs to: its root object's name without Unity's clone and
        /// duplicate suffixes, so every spawned soldier or vehicle of one kind lands together.
        /// </summary>
        internal static string GroupOf(Renderer renderer) => GroupName(renderer.transform.root.name);

        internal static string GroupName(string rootName)
        {
            string name = rootName.Replace("(Clone)", string.Empty).TrimEnd();
            if (name.EndsWith(")"))
            {
                int open = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (open > 0 && name.Substring(open + 2, name.Length - open - 3).All(char.IsDigit)) name = name.Substring(0, open);
            }
            return name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '_', '-');
        }

        private void Apply(string state, Terrain terrain, Camera camera)
        {
            if (state.StartsWith(LayerPrefix, StringComparison.Ordinal))
            {
                int mask = camera.cullingMask;
                camera.cullingMask = mask & ~(1 << LayerMask.NameToLayer(state.Substring(LayerPrefix.Length)));
                _undo = () => { if (camera != null) camera.cullingMask = mask; };
                return;
            }
            if (state.StartsWith(GroupPrefix, StringComparison.Ordinal))
            {
                string group = state.Substring(GroupPrefix.Length);
                var hidden = new List<Renderer>();
                foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (!renderer.enabled || GroupOf(renderer) != group) continue;
                    renderer.enabled = false;
                    hidden.Add(renderer);
                }
                _undo = () => { foreach (Renderer renderer in hidden) if (renderer != null) renderer.enabled = true; };
                return;
            }

            switch (state)
            {
                case "nodetails":
                {
                    float distance = terrain.detailObjectDistance;
                    terrain.detailObjectDistance = 0f;
                    _undo = () => { if (terrain != null) terrain.detailObjectDistance = distance; };
                    break;
                }
                case "noshadows":
                {
                    ShadowQuality shadows = QualitySettings.shadows;
                    QualitySettings.shadows = ShadowQuality.Disable;
                    _undo = () => QualitySettings.shadows = shadows;
                    break;
                }
                case "nomsaa":
                {
                    int msaa = QualitySettings.antiAliasing;
                    QualitySettings.antiAliasing = 0;
                    _undo = () => QualitySettings.antiAliasing = msaa;
                    break;
                }
                case "nodepth":
                {
                    DepthTextureMode mode = camera.depthTextureMode;
                    bool soft = QualitySettings.softParticles;
                    camera.depthTextureMode = DepthTextureMode.None;
                    QualitySettings.softParticles = false;
                    _undo = () => { if (camera != null) camera.depthTextureMode = mode; QualitySettings.softParticles = soft; };
                    break;
                }
                case "notrees":
                {
                    Behaviour gpuTrees = terrain.GetComponent("InstancedTreeRenderer") as Behaviour;
                    float treeDistance = terrain.treeDistance;
                    if (gpuTrees != null) gpuTrees.enabled = false;
                    terrain.treeDistance = 0f;
                    _undo = () =>
                    {
                        if (terrain != null) terrain.treeDistance = treeDistance;
                        if (gpuTrees != null) gpuTrees.enabled = true;
                    };
                    break;
                }
                case "shadow150":
                {
                    float distance = QualitySettings.shadowDistance;
                    QualitySettings.shadowDistance = Mathf.Min(distance, 150f);
                    _undo = () => QualitySettings.shadowDistance = distance;
                    break;
                }
                case "pixelerr8":
                {
                    float error = terrain.heightmapPixelError;
                    terrain.heightmapPixelError = Mathf.Max(error, 8f);
                    _undo = () => { if (terrain != null) terrain.heightmapPixelError = error; };
                    break;
                }
                case "nopost":
                {
                    Behaviour post = camera.GetComponent("PostProcessLayer") as Behaviour;
                    if (post != null && post.enabled)
                    {
                        post.enabled = false;
                        _undo = () => { if (post != null) post.enabled = true; };
                    }
                    break;
                }
            }
        }
    }
}
#endif
