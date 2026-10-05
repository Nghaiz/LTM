using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Caps a rendered client's frame rate while it is unfocused or loading a map, and gives it
    /// back exactly the VSync and frame-rate settings it had once neither holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unfocused: <see cref="BackgroundFrameRate"/>.</b> <c>ProjectSettings.asset</c> sets
    /// <c>runInBackground</c>, so an unfocused client keeps simulating -- it must, or it freezes and
    /// reads as a replication defect. It also kept RENDERING at full rate: measured 2026-09-27 on a
    /// two-client lane-B run, the window nobody was looking at still drew 70-170 frames a second on
    /// about 3.6 cores and its share of the GPU, beside the one being played and the game server.
    /// Testing two players on one machine is the normal way this project is play-tested, so that
    /// load landed on every such session. Thirty frames a second is the netcode's own tick rate, so
    /// a background window still advances one prediction tick per frame and nothing it shows goes
    /// stale.
    /// </para>
    /// <para>
    /// <b>Loading a map: <see cref="LoadingFrameRate"/>.</b> While a map loads, Unity's loading
    /// thread builds the terrain's physics heightfield, and the job memory it holds for that stays
    /// alive until the build is done. Unity calls any such block older than four frames a probable
    /// leak. Forest Lake's heightmap is 2049 x 2049, sixteen times Dustbowl's, and a focused client
    /// drawing its loading screen at a hundred-odd frames a second ran out of four frames before
    /// the build finished: "JobTempAlloc has allocations that are more than the maximum lifespan of
    /// 4 frames old", and "deleting an allocation that is older than its permitted lifetime of 4
    /// frames (age = 6)", on the first Forest Lake load of the v3.1.0 release test (stack recovered
    /// from the player's own log with the release PDB: CollisionMeshCooking::CreateHeightField
    /// under Heightmap::CreateHeightField under PreloadManager::Run). The unfocused client, already
    /// capped at 30, logged nothing. At this rate four frames last 266 ms, several times the build,
    /// and the loading thread gets the CPU the loading screen was spending.
    /// </para>
    /// <para>
    /// <b>VSync has to be switched off to cap, and switched back on after.</b> Unity ignores
    /// <see cref="Application.targetFrameRate"/> while <see cref="QualitySettings.vSyncCount"/> is
    /// non-zero, and an occluded window's VSync does not reliably limit anything (the player log
    /// says so: "vsync is broken"). Both values are remembered when the first cap starts and
    /// restored when the last one ends, so a VSync the player chose in the settings screen survives
    /// every alt-tab and every map load, in any order.
    /// </para>
    /// <para>
    /// <b>Not in batchmode, and not under the lane-B harness.</b> A headless server has no window
    /// to lose focus and sets its own rate in <c>NetServerBootstrap</c>; a harness client's timing
    /// is part of what its runs measure and is left exactly as it was. Subscribed to
    /// <see cref="Application.focusChanged"/> rather than hosted on a GameObject: it is a reaction
    /// to one application event and owns no scene object.
    /// </para>
    /// </remarks>
    public sealed class BackgroundFrameCap
    {
        /// <summary>Frames per second while unfocused: one prediction tick per frame.</summary>
        public const int BackgroundFrameRate = 30;

        /// <summary>Frames per second while a map loads. See the remarks.</summary>
        public const int LoadingFrameRate = 15;

        internal const string HarnessRoleVariable = "IRONFRONT_LANEB_ROLE";

        private static BackgroundFrameCap _installed;

        private bool _unfocused;
        private bool _loading;
        private bool _inMenus;
        private bool _capped;
        private int _savedVSyncCount;
        private int _savedTargetFrameRate;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRendered()
        {
            if (_installed != null) return;
            if (Application.isBatchMode) return;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(HarnessRoleVariable))) return;

            _installed = new BackgroundFrameCap();
            Application.focusChanged += _installed.OnFocusChanged;
            SceneManager.activeSceneChanged += (_, next) => _installed.OnMenusChanged(IsMenuScene(next.name));
            _installed.OnMenusChanged(IsMenuScene(SceneManager.GetActiveScene().name));
        }

        /// <summary>
        /// Whether <paramref name="sceneName"/> is one of the menus: the title, login, room browser,
        /// lobby and settings all live in <c>Menu</c>, and <c>Splash</c> comes before it. Every map,
        /// practice included, is something else.
        /// </summary>
        internal static bool IsMenuScene(string sceneName) => sceneName == "Menu" || sceneName == "Splash";

        /// <summary>
        /// Applies the frame rate the player chose in Settings: at once when nothing caps the
        /// game, or as the rate to come back to when a cap lifts. Without this, saving Settings in
        /// the menus -- which are always capped -- was undone the moment the cap lifted.
        /// </summary>
        public static void ApplyPlayerChoice(int vSyncCount, int targetFrameRate)
        {
            if (_installed != null)
            {
                _installed.OnPlayerChoice(vSyncCount, targetFrameRate);
                return;
            }
            QualitySettings.vSyncCount = vSyncCount;
            Application.targetFrameRate = targetFrameRate;
        }

        /// <summary>
        /// Called by the client flow when it starts loading a match map and once the map is up or the
        /// load is abandoned. A no-op where the cap is not installed.
        /// </summary>
        public static void SetLoadingMap(bool loading) => _installed?.OnLoadingChanged(loading);

        /// <summary>The focus edge, callable without an application to lose focus in.</summary>
        internal void OnFocusChanged(bool hasFocus)
        {
            _unfocused = !hasFocus;
            Apply();
        }

        /// <summary>The Settings edge, callable without a settings screen.</summary>
        internal void OnPlayerChoice(int vSyncCount, int targetFrameRate)
        {
            if (_capped)
            {
                _savedVSyncCount = vSyncCount;
                _savedTargetFrameRate = targetFrameRate;
                Apply();
                return;
            }
            QualitySettings.vSyncCount = vSyncCount;
            Application.targetFrameRate = targetFrameRate;
        }

        /// <summary>The menus edge, callable without a scene to change.</summary>
        internal void OnMenusChanged(bool inMenus)
        {
            _inMenus = inMenus;
            Apply();
        }

        /// <summary>The loading edge, callable without a map to load.</summary>
        internal void OnLoadingChanged(bool loading)
        {
            _loading = loading;
            Apply();
        }

        private void Apply()
        {
            int cap = _loading ? LoadingFrameRate
                : _unfocused ? BackgroundFrameRate
                : _inMenus ? CpuBudgetRules.MenuFrameCap
                : 0;

            if (cap > 0)
            {
                if (!_capped)
                {
                    _savedVSyncCount = QualitySettings.vSyncCount;
                    _savedTargetFrameRate = Application.targetFrameRate;
                    _capped = true;
                }
                // Never above the rate the player's own settings give: a 15 fps load stays 15 under any limit.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = CpuBudgetRules.CapUnder(cap, _savedVSyncCount > 0 ? -1 : _savedTargetFrameRate);
            }
            else if (_capped)
            {
                QualitySettings.vSyncCount = _savedVSyncCount;
                Application.targetFrameRate = _savedTargetFrameRate;
                _capped = false;
            }
        }
    }
}
