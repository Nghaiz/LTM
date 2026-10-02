using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Keeps a rendering client to a fair share of the player's machine: a small job-worker pool,
    /// and a frame cap at the display's refresh rate while v-sync is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The numbers and their measurements are <see cref="CpuBudgetRules"/>'s. This only installs
    /// them, before the first scene, so the pool is sized before any system schedules a job.
    /// </para>
    /// <para>
    /// <b>The frame cap is a standing value, not a mode.</b> Unity ignores
    /// <see cref="Application.targetFrameRate"/> while v-sync is on, so it needs no updating when
    /// Settings toggles v-sync, and <see cref="BackgroundFrameCap"/> saves and restores it around
    /// its own focus and loading caps.
    /// </para>
    /// <para>
    /// Not in the Editor, whose job workers are the Editor's own, nor in a headless process
    /// (<c>NetServerBootstrap</c> sizes its own pool), nor under the lane-B harness, whose timing
    /// <see cref="BackgroundFrameCap"/> also leaves alone.
    /// </para>
    /// </remarks>
    public static class ClientCpuBudget
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyAtStartup()
        {
            if (Application.isEditor || Application.isBatchMode) return;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            JobWorkerCap.Apply(CpuBudgetRules.ClientJobWorkers);

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BackgroundFrameCap.HarnessRoleVariable))) return;

            double refreshHz = Screen.currentResolution.refreshRateRatio.value;
            Application.targetFrameRate = CpuBudgetRules.ForegroundFrameCap(refreshHz);
            Debug.Log($"[graphics] frame cap {Application.targetFrameRate} fps while v-sync is off "
                      + $"(display {refreshHz:F0} Hz).");
        }
    }
}
