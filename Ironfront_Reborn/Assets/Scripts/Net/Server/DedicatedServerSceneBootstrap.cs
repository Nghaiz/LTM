using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Loads the map scene a headless dedicated server is meant to host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes.</b> <see cref="NetServerBootstrap"/> is a MonoBehaviour that
    /// lives in a map scene, so it only ever runs if a map scene is loaded. Nothing in the
    /// shipped project loaded one: the dedicated-server build's scene list is Splash, Menu,
    /// Island, Dustbowl, and a <c>-batchmode</c> run walks Splash to Menu and stops. The
    /// process reports a clean start, Unity logs no error, the container is <c>Up</c>, the
    /// restart policy never fires — and the UDP port is never bound, so every client that
    /// dials it times out against a server that is, by every signal available from outside,
    /// perfectly healthy.
    /// </para>
    /// <para>
    /// It was invisible for a specific reason: the only thing that ever loaded a map headlessly
    /// was <c>LaneBHarness</c>, which does it for itself
    /// (<c>SceneManager.LoadScene(Read("IRONFRONT_LANEB_SCENE") ?? "Dustbowl")</c>). Every
    /// headless run anyone had ever done went through the harness, so the missing step was
    /// always supplied by the thing being tested rather than by the thing being shipped.
    /// <c>HeadlessLoadBootstrap</c> even names the sequence in its own remark — "the wait is
    /// what handles the Splash → Menu → map sequence" — while waiting for a map load that, in a
    /// shipped server, nobody performed.
    /// </para>
    /// <para>
    /// <b>Why an env var and not <c>IRONFRONT_GAMESERVER_MAP_IDS</c>.</b> That one is a
    /// matchmaking advertisement — a list of numeric ids the master filters on — and there is
    /// no mapId-to-scene table anywhere in the repository. Inventing one here would put a
    /// second, silently-diverging source of truth beside the master's. A scene name is what
    /// <c>SceneManager</c> actually takes, and it matches the harness's existing convention.
    /// </para>
    /// </remarks>
    public static class DedicatedServerSceneBootstrap
    {
        /// <summary>Scene to load. Mirrors <c>EnvRegistry.GameServerScene</c>.</summary>
        public const string SceneVariable = "IRONFRONT_GAMESERVER_SCENE";

        /// <summary>Default map, matching the lane-B harness's own default.</summary>
        public const string DefaultScene = "Dustbowl";

        /// <summary>Set by the lane-B harness; its presence means the harness owns scene loading.</summary>
        private const string HarnessRoleVariable = "IRONFRONT_LANEB_ROLE";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Batch mode only. An Editor Play session shares this domain with whatever the
            // person at the keyboard opened, and yanking them into Dustbowl because a stale
            // environment variable is set would be a hostile diagnostic.
            if (!Application.isBatchMode) return;

            // The harness loads its own map and strips a bootstrap while doing it. Two loaders
            // racing over one scene is worse than either alone.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HarnessRoleVariable)))
                return;

            string scene = Environment.GetEnvironmentVariable(SceneVariable);
            if (string.IsNullOrWhiteSpace(scene)) scene = DefaultScene;
            scene = scene.Trim();

            Scene active = SceneManager.GetActiveScene();
            if (string.Equals(active.name, scene, StringComparison.Ordinal))
            {
                Debug.Log($"[server] already in '{scene}'; no map load needed");
                return;
            }

            // Named, not indexed. A build-index constant is the other way this breaks silently:
            // reordering EditorBuildSettings then hosts a different map than the one configured,
            // and nothing anywhere says so.
            if (!Application.CanStreamedLevelBeLoaded(scene))
            {
                Debug.LogError(
                    $"[server] scene '{scene}' is not in the build. Set {SceneVariable} to a "
                    + "scene listed in EditorBuildSettings, or add it there. The server cannot "
                    + "host without a map: NetServerBootstrap lives in one, so the UDP port "
                    + "would never be bound and this process would accept nobody.");
                return;
            }

            Debug.Log($"[server] batch mode: loading map scene '{scene}' (from {SceneVariable})");
            SceneManager.LoadScene(scene);
        }
    }
}
