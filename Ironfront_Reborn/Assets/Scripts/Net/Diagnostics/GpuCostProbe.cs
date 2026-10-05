// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Prices each heavy rendering feature in a live match (P31): with <c>IRONFRONT_GPU_PROBE=1</c>
    /// it turns one feature off for <see cref="StateSeconds"/>, puts it back, turns off the next,
    /// and logs <c>[ab] &lt;state&gt; at t=Ns</c> at every change, so the <c>[frames]</c> windows
    /// (GPU, main and render thread time) can be grouped by what was off.
    /// </summary>
    /// <remarks>
    /// Measuring only: every state restores exactly what it changed before the next begins, and the
    /// player sees the match change look while it runs. Never ship a build with it switched on.
    /// </remarks>
    public sealed class GpuCostProbe : MonoBehaviour
    {
        private const float StateSeconds = 20f;

        private static readonly string[] States =
        {
            "base", "nodetails", "noshadows", "nomsaa", "nodepth", "notrees", "shadow150", "pixelerr8", "nopost",
        };

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
            _state = (_state + 1) % States.Length;
            _nextAt = Time.realtimeSinceStartup + StateSeconds;
            Apply(States[_state], terrain, camera);
            Debug.Log($"[ab] {States[_state]} at t={Time.realtimeSinceStartup:F0}s");
        }

        private void Apply(string state, Terrain terrain, Camera camera)
        {
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
