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
    /// <b>The terrain draws and does nothing else.</b> Bringing a <see cref="Terrain"/> up
    /// (<c>Terrain::AddToManager</c>) sets up its splat material, and with no shader in the build
    /// that logged "Trying to access a shader but no shaders were included in the build because
    /// Dedicated Server Optimizations is enabled" three times per map load: gdb on the server
    /// binary put all three in <c>SplatShaderSet</c>, <c>FindBaseMapGenShader</c> and
    /// <c>SplatMaterials::Update</c>. What holds bodies up is the <see cref="TerrainCollider"/>,
    /// which stays on, and the ground bots read is that collider's
    /// (<c>Ironfront.Net.Unity.TerrainSurface</c>), so the server loses nothing by the renderer
    /// being off.
    /// </para>
    /// <para>
    /// <b>Server only.</b> Client builds and Play Mode keep every probe and terrain as authored:
    /// <c>ReflectionProber</c> renders the probes on a machine that has a screen.
    /// </para>
    /// <para>
    /// <b>The version.</b> Unity's incremental player build caches processed scenes, and reuses a
    /// cached scene without calling a processor that carries no version: the first server build
    /// after this file was added still shipped both probes on until Library/PlayerDataCache was
    /// deleted. Raise it whenever what this does changes.
    /// </para>
    /// </remarks>
    [BuildCallbackVersion(2)]
    public sealed class ServerBuildSceneStrip : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Null in Play Mode, which also calls scene processors.
            if (report == null) return;
            if (report.summary.GetSubtarget<StandaloneBuildSubtarget>() != StandaloneBuildSubtarget.Server) return;

            int probes = 0;
            int terrains = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    probe.hdr = false;
                    probe.enabled = false;
                    probes++;
                }

                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                {
                    terrain.enabled = false;
                    terrains++;
                }
            }

            if (probes + terrains > 0)
            {
                Debug.Log($"[build] server scene '{scene.name}': {probes} reflection probe(s) and {terrains} terrain renderer(s) off");
            }
        }
    }
}
