using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[assembly: InternalsVisibleTo("Ironfront.Rendering.Tests")]

namespace Ironfront.Rendering
{
    /// <summary>
    /// Puts an <see cref="InstancedTreeRenderer"/> on every terrain with trees, and an
    /// <see cref="InstancedDetailRenderer"/> on every terrain with details, in every scene that
    /// loads, in a process that renders: a headless server draws neither.
    /// </summary>
    /// <remarks>
    /// Installed from code rather than placed in the map scenes, so a map added later is covered
    /// without anyone remembering to; a terrain a renderer cannot take keeps its trees or details and
    /// says so once (<see cref="InstancedTreeRenderer.Build"/>, <see cref="InstancedDetailRenderer.Build"/>).
    /// </remarks>
    internal static class InstancedTreeBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Attach(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Attach(scene);

        internal static void Attach(Scene scene)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !scene.isLoaded) return;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                {
                    TerrainData data = terrain.terrainData;
                    if (data == null) continue;
                    // Added in code on purpose, not authored: see the class remark.
                    if (data.treeInstanceCount > 0 && terrain.GetComponent<InstancedTreeRenderer>() == null)
                        terrain.gameObject.AddComponent<InstancedTreeRenderer>();
                    if (data.detailPrototypes.Length > 0 && terrain.GetComponent<InstancedDetailRenderer>() == null)
                        terrain.gameObject.AddComponent<InstancedDetailRenderer>();
                }
            }
        }
    }
}
