using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.EditorTools
{
    /// <summary>
    /// Turns off, in a dedicated server build only, the scene components that exist to be seen
    /// and complain at load on a process with no graphics device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why at build time.</b> Every game server opened its map with "HDR Render Texture not
    /// supported, disabling HDR on reflection probe" twice: each shipping map has two reflection
    /// probes. Run with -stackTraceLogType Full, the line has no managed frame: the engine logs it
    /// while bringing the scene's components up, before any script runs, so no runtime check can
    /// stop it. A server build of 2026-10-01 with the probes off logged it on none of the three
    /// maps.
    /// </para>
    /// <para>
    /// <b>Server only.</b> Client builds and Play Mode keep every probe as authored:
    /// <c>ReflectionProber</c> renders them on a machine that has a screen.
    /// </para>
    /// <para>
    /// <b>The version.</b> Unity's incremental player build caches processed scenes, and reuses a
    /// cached scene without calling a processor that carries no version: the first server build
    /// after this file was added still shipped both probes on until Library/PlayerDataCache was
    /// deleted. Raise it whenever what this does changes.
    /// </para>
    /// </remarks>
    [BuildCallbackVersion(1)]
    public sealed class ServerBuildSceneStrip : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Null in Play Mode, which also calls scene processors.
            if (report == null) return;
            if (report.summary.GetSubtarget<StandaloneBuildSubtarget>() != StandaloneBuildSubtarget.Server) return;

            int probes = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    probe.hdr = false;
                    probe.enabled = false;
                    probes++;
                }
            }

            if (probes > 0)
            {
                Debug.Log($"[build] server scene '{scene.name}': {probes} reflection probe(s) off");
            }
        }
    }
}
