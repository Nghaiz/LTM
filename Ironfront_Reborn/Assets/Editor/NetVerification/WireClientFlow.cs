using System.Text;
using Ironfront.Net.Unity.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Puts a <see cref="ClientFlowBootstrap"/> on the object that carries the lobby shell in
    /// <c>Menu.unity</c>, and saves the scene. P8 task 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wiring is a command rather than a drag, on purpose.</b> A component somebody has
    /// to add by hand is a component that is missing in one branch, in one clone, and in
    /// whatever a build machine checked out — and the symptom of it missing is precisely the
    /// state P8 found: a login screen drawing "Lobby shell: unbound", which reads as a broken
    /// master server rather than as an unauthored scene. Running this is verifiable and
    /// re-runnable; a drag is neither.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> Re-running on an already-wired scene reports what it found and saves
    /// nothing, so it is safe from CI, from the menu, and from a second run by somebody who was
    /// not sure whether the first one took.
    /// </para>
    /// <para>
    /// <b>It finds the host by component type, not by name.</b> The object matters, not what it
    /// is called; a scene where the shell was moved onto a differently-named object should still
    /// wire correctly. Same reason <c>tools/unity-wire-dustbowl-netclient.cs</c> does it.
    /// </para>
    /// <para>
    /// <b>Run headlessly:</b>
    /// <c>Unity -batchmode -nographics -quit -projectPath Ironfront_Reborn
    /// -executeMethod Ironfront.Net.Unity.EditorTools.WireClientFlow.Run</c>. Under
    /// <c>-batchmode</c> it exits non-zero on failure, because Unity does not fail a run just
    /// because an <c>-executeMethod</c> did.
    /// </para>
    /// </remarks>
    public static class WireClientFlow
    {
        private const string ScenePath = "Assets/Scenes/Menu.unity";
        private const string ReportFile = "wire-client-flow.txt";

        [MenuItem("Ironfront/Net/Wire client flow into Menu")]
        public static void RunFromMenu() => Execute(exitOnFailure: false);

        /// <summary>The <c>-executeMethod</c> entry point.</summary>
        public static void Run() => Execute(exitOnFailure: Application.isBatchMode);

        private static void Execute(bool exitOnFailure)
        {
            var log = new StringBuilder();
            bool ok = Wire(log);

            string report = log.ToString();
            System.IO.File.WriteAllText(ReportFile, report);

            if (ok) Debug.Log("[wire-client-flow]\n" + report);
            else Debug.LogError("[wire-client-flow] FAILED\n" + report);

            if (!ok && exitOnFailure) EditorApplication.Exit(1);
        }

        private static bool Wire(StringBuilder log)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                log.AppendLine("opened: " + scene.path);
            }
            else
            {
                log.AppendLine("already open: " + scene.path);
            }

            LobbyShellOverlay shell = FindShell(scene);
            if (shell == null)
            {
                log.AppendLine("FAILED: no LobbyShellOverlay anywhere in " + ScenePath + ".");
                log.AppendLine("        Without one the flow has nothing drawing it, so a player");
                log.AppendLine("        cannot log in. Add the component before re-running.");
                return false;
            }

            GameObject host = shell.gameObject;
            log.AppendLine("shell host: " + host.name + " (activeSelf=" + host.activeSelf + ")");

            // ClientFlowBootstrap calls DontDestroyOnLoad on itself, which only accepts a root
            // object -- it moves the ROOT of a child's hierarchy instead, dragging whatever else
            // that root carries into the persistent scene. Reported rather than reparented here:
            // moving somebody's scene hierarchy is not this script's call to make.
            if (host.transform.parent != null)
            {
                log.AppendLine("NOTE: the shell is a child of '" + host.transform.parent.name
                               + "'. ClientFlowBootstrap detaches itself at runtime so");
                log.AppendLine("      DontDestroyOnLoad takes this object alone.");
            }

            if (!host.activeSelf)
            {
                host.SetActive(true);
                log.AppendLine("activated the host object; a disabled shell never wakes.");
            }

            ClientFlowBootstrap existing = host.GetComponent<ClientFlowBootstrap>();
            if (existing != null)
            {
                log.AppendLine("already wired: ClientFlowBootstrap is on " + host.name + ".");
            }
            else
            {
                Undo.AddComponent<ClientFlowBootstrap>(host);
                log.AppendLine("added ClientFlowBootstrap to " + host.name + ".");
            }

            // The bootstrap takes ownership of MasterSession.Tick at bind time, but the
            // serialized value is what a reader of the scene sees -- and two tickers age the
            // connect timeout twice as fast, reported to the player as a game server that did
            // not answer. Settling it here keeps the asset honest about who ticks.
            var shellObject = new SerializedObject(shell);
            SerializedProperty ticks = shellObject.FindProperty("_tickSession");
            if (ticks != null && ticks.boolValue)
            {
                ticks.boolValue = false;
                shellObject.ApplyModifiedProperties();
                log.AppendLine("cleared LobbyShellOverlay._tickSession; the bootstrap ticks now.");
            }

            if (!scene.isDirty)
            {
                log.AppendLine("scene unchanged; nothing saved.");
                return true;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                log.AppendLine("FAILED: could not save " + ScenePath + ".");
                return false;
            }

            log.AppendLine("saved " + ScenePath + ".");
            return true;
        }

        private static LobbyShellOverlay FindShell(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                LobbyShellOverlay found = root.GetComponentInChildren<LobbyShellOverlay>(true);
                if (found != null) return found;
            }

            return null;
        }
    }
}
