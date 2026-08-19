// Wires Assets/Prefab/Remote Actor Proxy.prefab into Dustbowl's RemoteActorRegistry and
// activates the NetClient GameObject, then saves the scene. Idempotent — safe to re-run.
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class WireDustbowlNetClient
{
    private const string ScenePath = "Assets/Scenes/Dustbowl.unity";
    private const string ProxyPath = "Assets/Prefab/Remote Actor Proxy.prefab";

    public static void Run()
    {
        var log = new StringBuilder();

        Scene scene = SceneManager.GetActiveScene();
        log.AppendLine("active scene: " + scene.path + " (dirty=" + scene.isDirty + ")");
        if (scene.path != ScenePath)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            log.AppendLine("opened: " + scene.path);
        }

        GameObject proxy = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPath);
        if (proxy == null)
        {
            log.AppendLine("FAILED: proxy prefab not found at " + ProxyPath);
            System.IO.File.WriteAllText("wire-netclient.txt", log.ToString());
            return;
        }

        // Found by component type rather than by name: the object carrying the registry is what
        // matters, and a scene where somebody renamed "NetClient" should still wire correctly.
        MonoBehaviour registry = null;
        foreach (GameObject rootGo in scene.GetRootGameObjects())
        {
            foreach (MonoBehaviour m in rootGo.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (m == null || m.GetType().Name != "RemoteActorRegistry") continue;
                registry = m;
                break;
            }
            if (registry != null) break;
        }

        if (registry == null)
        {
            log.AppendLine("FAILED: no RemoteActorRegistry anywhere in " + ScenePath);
            System.IO.File.WriteAllText("wire-netclient.txt", log.ToString());
            return;
        }

        GameObject host = registry.gameObject;
        log.AppendLine("registry host: " + host.name + " (activeSelf=" + host.activeSelf + ")");

        // SerializedObject rather than reflection: _remoteActorPrefab is a private [SerializeField],
        // and only the serialization path marks the scene dirty and registers undo. A reflection
        // write lands in the live C# object and is discarded the moment the scene reloads.
        var so = new SerializedObject(registry);
        SerializedProperty prefabProp = so.FindProperty("_remoteActorPrefab");
        SerializedProperty prewarmProp = so.FindProperty("_prewarm");

        if (prefabProp == null)
        {
            log.AppendLine("FAILED: _remoteActorPrefab property not found");
            System.IO.File.WriteAllText("wire-netclient.txt", log.ToString());
            return;
        }

        log.AppendLine("before: _remoteActorPrefab="
                       + (prefabProp.objectReferenceValue == null
                           ? "<null>" : prefabProp.objectReferenceValue.name)
                       + " _prewarm=" + (prewarmProp == null ? "?" : prewarmProp.intValue.ToString()));

        prefabProp.objectReferenceValue = proxy;
        so.ApplyModifiedProperties();

        log.AppendLine("after:  _remoteActorPrefab="
                       + (prefabProp.objectReferenceValue == null
                           ? "<null>" : prefabProp.objectReferenceValue.name));

        if (!host.activeSelf)
        {
            Undo.RecordObject(host, "Activate NetClient");
            host.SetActive(true);
            log.AppendLine("activated: " + host.name);
        }
        else
        {
            log.AppendLine("already active: " + host.name);
        }

        // Parents matter as much as the object itself — an active child under an inactive parent is
        // still inactive, and RemoteActorRegistry.Awake would never run.
        log.AppendLine("activeInHierarchy=" + host.activeInHierarchy);
        for (Transform p = host.transform.parent; p != null; p = p.parent)
            log.AppendLine("  parent '" + p.name + "' activeSelf=" + p.gameObject.activeSelf);

        // Report the rest of the netcode objects in the scene, so the report can state what is and
        // is not wired without a second round trip.
        log.AppendLine("--- net components in scene ---");
        foreach (GameObject rootGo in scene.GetRootGameObjects())
        {
            foreach (MonoBehaviour m in rootGo.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (m == null) continue;
                string n = m.GetType().Name;
                if (!n.StartsWith("Net") && !n.StartsWith("Client") && !n.StartsWith("Remote")
                    && !n.StartsWith("Snapshot") && !n.StartsWith("Transport")
                    && !n.StartsWith("Prediction") && !n.StartsWith("Lobby")) continue;
                log.AppendLine("  " + m.gameObject.name + "." + n
                               + " goActive=" + m.gameObject.activeInHierarchy
                               + " enabled=" + m.enabled);
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        log.AppendLine("scene saved=" + saved);

        AssetDatabase.SaveAssets();
        System.IO.File.WriteAllText("wire-netclient.txt", log.ToString());
        Debug.Log("[proxy] wiring finished, see wire-netclient.txt");
    }
}
