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

        /// <summary>
        /// Declares the process a dedicated server before any map scene <c>Awake</c> can read it,
        /// so the client half of the map scene declines to dial. AD-1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists at all.</b> `Dustbowl` carries an active `NetServer` AND an active
        /// `NetClient`, so every process that loads it is a listen server. The lane-B harness
        /// strips the half it is not; the shipped dedicated server stripped nothing, so it
        /// joined its own match: a real body at a real spawn point, holding one of sixteen
        /// player slots and one connection, with the congestion controller reacting to its own
        /// loopback traffic. Observed on the first deployment that anybody read the log of
        /// (2026-08-28, `ironfront/game-server` on the sandbox node):
        /// <c>[net] conn 1 joined as actor 41 (127.0.0.1:59244)</c>. `architecture.md` AD-1 says
        /// there is no host/listen-server mode; until now nothing enforced it.
        /// </para>
        /// <para>
        /// <b>Why a flag and not <c>NetContext.IsServer</c>.</b> The role is settled by an
        /// `Awake` race between the two bootstraps — see `NetContext.IsDedicatedServer`. This
        /// runs at `BeforeSceneLoad`, ahead of every `Awake` in every scene, which is the window
        /// the harness's own `DeclareRole` remark records having got wrong once.
        /// </para>
        /// <para>
        /// <b>The guards are the same three `Install` uses, and deliberately so.</b> Not batch
        /// mode means a person is at the keyboard and an Editor listen server stays exactly as
        /// it was; a harness role means lane B owns the topology and already strips a bootstrap.
        /// A process reaching here with neither is a headless build launched to host a map, and
        /// that is the only thing this declares.
        /// </para>
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DeclareDedicatedServer()
        {
            if (!Application.isBatchMode) return;

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HarnessRoleVariable)))
                return;

            NetContext.DeclareDedicatedServer();
        }

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
