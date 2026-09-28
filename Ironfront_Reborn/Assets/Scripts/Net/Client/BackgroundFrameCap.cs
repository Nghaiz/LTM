using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Drops a rendered client that has lost focus to <see cref="BackgroundFrameRate"/> frames a
    /// second, and gives it back exactly the VSync and frame-rate settings it had on refocus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> <c>ProjectSettings.asset</c> sets <c>runInBackground</c>, so an unfocused client
    /// keeps simulating -- it must, or it freezes and reads as a replication defect. It also kept
    /// RENDERING at full rate: measured 2026-09-27 on a two-client lane-B run, the window nobody
    /// was looking at still drew 70-170 frames a second on about 3.6 cores and its share of the
    /// GPU, beside the one being played and the game server. Testing two players on one machine
    /// is the normal way this project is play-tested, so that load landed on every such session.
    /// Thirty frames a second is the netcode's own tick rate, so a background window still
    /// advances one prediction tick per frame and nothing it shows goes stale.
    /// </para>
    /// <para>
    /// <b>VSync has to be switched off to cap, and switched back on after.</b> Unity ignores
    /// <see cref="Application.targetFrameRate"/> while <see cref="QualitySettings.vSyncCount"/> is
    /// non-zero, and an occluded window's VSync does not reliably limit anything (the player log
    /// says so: "vsync is broken"). Both values are remembered on the way out and restored on the
    /// way in, so a VSync the player chose in the settings screen survives every alt-tab.
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

        private const string HarnessRoleVariable = "IRONFRONT_LANEB_ROLE";

        private static BackgroundFrameCap _installed;

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
        }

        /// <summary>The focus edge, callable without an application to lose focus in.</summary>
        internal void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus && !_capped)
            {
                _savedVSyncCount = QualitySettings.vSyncCount;
                _savedTargetFrameRate = Application.targetFrameRate;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = BackgroundFrameRate;
                _capped = true;
            }
            else if (hasFocus && _capped)
            {
                QualitySettings.vSyncCount = _savedVSyncCount;
                Application.targetFrameRate = _savedTargetFrameRate;
                _capped = false;
            }
        }
    }
}
