// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
#if !IRONFRONT_NO_DIAGNOSTICS
using Ironfront.Net.Unity.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// One window of death-driven input suppression, as it was observed frame by frame.
    /// </summary>
    /// <remarks>
    /// <b>A window, for the same reason <see cref="AllocationWindow"/> is one.</b> A death can
    /// open and close between two captures: across all 21 checkpoints of
    /// <c>p4-pointblank-01</c> the record read <c>alive: true</c> every time while the killfeed
    /// proved both players died repeatedly. An instantaneous read therefore samples the case
    /// only when the capture lands inside the dead window; a window that counts every frame
    /// between two captures cannot miss one.
    /// <para>
    /// <b>Stated narrowly on purpose.</b> "The dead window is always shorter than the cadence"
    /// would be false -- seven lane-B artifacts predating this sampler DO carry a checkpoint
    /// with <c>alive: false</c>, and <c>p5-separation-02</c>'s <c>killed</c> capture landed on
    /// one too. The claim is only that an instant cannot be relied on, which is weaker and
    /// true: over the same two runs the instant caught 1 of 6 windows in which a death occurred.
    /// </para>
    /// </remarks>
    public readonly struct DeathInputWindow
    {
        /// <summary>Frames sampled in this window. Zero means the window carries no answer.</summary>
        public readonly long Frames;

        /// <summary>Frames in this window on which input was suppressed BY A DEATH.</summary>
        public readonly long SuppressedFrames;

        /// <summary>
        /// Frames in this window on which the local combat state reported the player DEAD.
        /// </summary>
        /// <remarks>
        /// Recorded beside <see cref="SuppressedFrames"/> rather than instead of it, because the
        /// two together are what grade check 13's middle term. Dead frames with zero suppressed
        /// frames is the failure the check is looking for; zero dead frames is a run that never
        /// provoked the case, and the two must not render the same.
        /// </remarks>
        public readonly long DeadFrames;

        /// <summary>Whether a <c>NetClientLocalCombatDriver</c> was resolvable at all.</summary>
        /// <remarks>
        /// <c>false</c> makes every count above meaningless rather than zero -- the same reason
        /// <see cref="AllocationWindow.Valid"/> exists. A server process has no driver, and a
        /// client whose driver disabled itself in <c>Awake</c> reports identically to a client
        /// that simply never died.
        /// </remarks>
        public readonly bool DriverPresent;

        public DeathInputWindow(long frames, long suppressedFrames, long deadFrames, bool driverPresent)
        {
            Frames = frames;
            SuppressedFrames = suppressedFrames;
            DeadFrames = deadFrames;
            DriverPresent = driverPresent;
        }

        /// <summary>Whether input was suppressed by a death on at least one frame of the window.</summary>
        public bool SuppressionObserved => SuppressedFrames > 0;

        /// <summary>Whether the player was dead on at least one frame of the window.</summary>
        public bool DeathObserved => DeadFrames > 0;
    }

    /// <summary>
    /// Watches <c>NetClientLocalCombatDriver</c> frame by frame and reports, per checkpoint
    /// window, whether a dead player's input was actually taken away. Ledger <b>X-29</b>,
    /// check 13's middle term.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the measurement check 13 was missing, and it is not the one already in the
    /// record.</b> <c>combat.driverEnabled</c> says whether the component is RUNNING -- it must
    /// keep running to accept a respawn request, so its staying <c>true</c> after a death is
    /// correct and is not an answer. <c>combat.localInputEnabled</c> is worse than silent: it
    /// reads <c>FpsActorController.IsInputEnabled</c>, which <c>Start</c> pins <c>false</c> for
    /// the whole life of a lane-B client because the only caller that re-enables it is
    /// <c>SpawnAt</c>, the gameplay spawn a networked body deliberately never runs. Reporting
    /// "input disabled after death" from that flag would be true, constant, and meaningless.
    /// </para>
    /// <para>
    /// <b>What IS the answer:</b> <c>NetClientLocalCombatDriver</c>'s own
    /// <c>_inputSuppressedByDeath</c>, set beside <c>local.DisableInput()</c> in
    /// <c>OnDied</c> and cleared in <c>OnRespawned</c> and <c>RestoreInput</c>. It is the flag
    /// the death path itself writes, so it cannot be pinned by an unrelated lifecycle.
    /// </para>
    /// <para>
    /// <b>The measurement CONFIRMS rather than discovers, and that is said out loud.</b> The
    /// behaviour was verified in the tree before this file was written. A check that could only
    /// ever pass is worth less than one that has been seen failing, so the accompanying
    /// mutation -- removing the suppression from <c>OnDied</c> -- is what proves this window can
    /// go red, and a run of it is quoted in the report rather than assumed.
    /// </para>
    /// <para>
    /// <b>Resolved lazily and re-resolved while null.</b> The driver is added by
    /// <c>NetClientBootstrap</c> after this sampler is constructed, and a cached reference to a
    /// destroyed or disabled component would silently stop counting -- which would render as a
    /// healthy "never died". Unity's overloaded <c>==</c> reports a destroyed component as null,
    /// so the destroyed case re-resolves on its own; the DISABLED case does not, and is checked
    /// explicitly. Once found and while it stays enabled the reference is kept, because a
    /// per-frame <c>FindFirstObjectByType</c> across a 40-actor scene is a cost this instrument
    /// has no reason to pay.
    /// </para>
    /// </remarks>
    public sealed class LaneBDeathInputSampler
    {
        private NetClientLocalCombatDriver _driver;
        private bool _everResolved;
        private bool _windowResolved;

        private long _windowFrames;
        private long _windowSuppressed;
        private long _windowDead;

        private long _runFrames;
        private long _runSuppressed;
        private long _runDead;

        /// <summary>
        /// The whole run so far, undrained.
        /// </summary>
        /// <remarks>
        /// <b>Nothing reads this yet</b>, and it is kept rather than deleted for the shape
        /// <c>LaneBAllocationSampler.Run</c> already has: a run total is what a summary line
        /// would want, and the per-window figures cannot be re-summed once drained. Said out
        /// loud so the next reader does not assume a consumer exists.
        /// </remarks>
        public DeathInputWindow Run =>
            new DeathInputWindow(_runFrames, _runSuppressed, _runDead, _everResolved);

        /// <summary>One frame's reading. Called from the harness's own Update.</summary>
        public void Sample()
        {
            if (_driver == null)
            {
                // Exclude, NOT Include. A disabled NetClientLocalCombatDriver unsubscribes
                // from OnDied/OnRespawned and calls RestoreInput() on the way out, so its flag
                // is pinned false for good. Latching one would report frames > 0, deadFrames 0,
                // suppressedFrames 0, driverPresent true -- indistinguishable from a healthy
                // client that never died, which is the exact silent zero DriverPresent exists
                // to prevent. An enabled driver is the only one that can answer.
                _driver = Object.FindFirstObjectByType<NetClientLocalCombatDriver>(
                    FindObjectsInactive.Exclude);

                if (_driver == null) return;

                _everResolved = true;
            }

            // Re-resolved next frame if it has since been disabled, for the same reason.
            if (!_driver.isActiveAndEnabled)
            {
                _driver = null;
                return;
            }

            _windowResolved = true;

            _windowFrames++;
            _runFrames++;

            if (_driver.IsInputSuppressedByDeath)
            {
                _windowSuppressed++;
                _runSuppressed++;
            }

            // IsAlive, not the absence of suppression: a body that is dead and STILL has input
            // is exactly the failure this window exists to catch, and reading deadness off the
            // suppression flag would make that case impossible to express.
            if (!_driver.State.IsAlive)
            {
                _windowDead++;
                _runDead++;
            }
        }

        /// <summary>
        /// Closes the current window and starts a new one. Called at each checkpoint.
        /// </summary>
        /// <remarks>
        /// Draining rather than accumulating, for <c>LaneBAllocationSampler.TakeWindow</c>'s
        /// reason: a cumulative count would carry the first death into every later checkpoint
        /// and make "was input suppressed during THIS window" unanswerable.
        /// </remarks>
        public DeathInputWindow TakeWindow()
        {
            var window = new DeathInputWindow(
                _windowFrames, _windowSuppressed, _windowDead, _windowResolved);

            _windowFrames = 0;
            _windowSuppressed = 0;
            _windowDead = 0;
            _windowResolved = false;

            return window;
        }
    }
}
#endif
