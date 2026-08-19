using System;

namespace Ironfront.Net.Replication.World
{
    /// <summary>When a spawner is allowed to produce its next vehicle.</summary>
    /// <remarks>Values match the original <c>VehicleSpawner.RespawnType</c> ordinals.</remarks>
    public enum VehicleRespawnType : byte
    {
        /// <summary>Respawn once the vehicle it produced is destroyed.</summary>
        AfterDestroyed = 0,

        /// <summary>Respawn once somebody drives the vehicle away.</summary>
        AfterMoved = 1,

        /// <summary>One vehicle, ever.</summary>
        Never = 2,
    }

    /// <summary>Where one spawner is in its cycle.</summary>
    public enum VehicleSpawnPhase : byte
    {
        /// <summary>Nothing scheduled and nothing standing.</summary>
        Idle = 0,

        /// <summary>Counting down to a spawn.</summary>
        CountingDown = 1,

        /// <summary>Countdown finished; the pad is occupied and being re-checked.</summary>
        WaitingForSpace = 2,

        /// <summary>Its vehicle is standing on the pad.</summary>
        Spawned = 3,

        /// <summary>The retry budget ran out. Re-armed only by the next lifecycle event.</summary>
        GaveUp = 4,
    }

    /// <summary>What the caller must do as a result of one <see cref="VehicleSpawnScheduler.Tick"/>.</summary>
    public readonly struct VehicleSpawnStep
    {
        /// <summary>Instantiate now, then call <see cref="VehicleSpawnScheduler.ReportSpawned"/>.</summary>
        public readonly bool ShouldSpawn;

        /// <summary>The budget was exhausted on this tick. Log once; do not log every tick.</summary>
        public readonly bool GaveUp;

        internal VehicleSpawnStep(bool shouldSpawn, bool gaveUp)
        {
            ShouldSpawn = shouldSpawn;
            GaveUp      = gaveUp;
        }

        internal static VehicleSpawnStep None => default;
    }

    /// <summary>
    /// One vehicle spawner's lifecycle: the countdown, the bounded retry budget, the
    /// re-entrancy guard, and the respawn rules. Phase-V8 task 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine-free so the defects it closes are testable.</b> Both of them were coroutine and
    /// <c>Invoke</c> behaviour inside a MonoBehaviour, which no CI run can reach:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The unbounded retry.</b> The original waited on
    /// <c>while (SpawnIsBlocked()) yield return new WaitForSeconds(1f)</c>, so a pad permanently
    /// blocked by a wreck re-tested once a second for the life of the process.
    /// <see cref="MaxBlockedRetries"/> bounds it; exhaustion is reported once, and the spawner
    /// re-arms on the next death or first-driver event rather than staying dead forever.
    /// </description></item>
    /// <item><description>
    /// <b>The missing re-entrancy guard.</b> <c>spawningQueued</c> was declared and never read
    /// or written, while <c>StartSpawnCountdown</c> was a bare <c>Invoke</c> — so two
    /// <c>VehicleDied</c> calls scheduled two spawns and one spawner produced two vehicles.
    /// <see cref="HasSpawnPending"/> is the guard that field was named for.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>One instance per spawner, not an array indexed by spawner id.</b> Every input the
    /// state machine needs — is the pad blocked, did my vehicle die, did somebody drive it —
    /// arrives from one <c>MonoBehaviour</c> about itself, so a central array would be a table
    /// each spawner writes exactly one row of. The instance is allocated once in <c>Awake</c>
    /// and nothing on the tick path allocates.
    /// </para>
    /// <para>
    /// <b>The blocked test is a delegate, and the caller must cache it.</b> It is invoked only
    /// in <see cref="VehicleSpawnPhase.WaitingForSpace"/> once the retry timer has elapsed — so
    /// an idle spawner costs no physics query at all, where a bool parameter would have forced
    /// one every frame from every spawner on the map. Passing a lambda per call would allocate
    /// one per frame; the caller holds a field.
    /// </para>
    /// </remarks>
    public sealed class VehicleSpawnScheduler
    {
        /// <summary>Blocked re-tests before a spawner gives up. Roughly 30 seconds by default.</summary>
        public const int DefaultMaxBlockedRetries = 30;

        /// <summary>Seconds between blocked re-tests. Matches the original's <c>WaitForSeconds(1f)</c>.</summary>
        public const float DefaultBlockedRetrySeconds = 1f;

        private readonly VehicleRespawnType _respawnType;
        private readonly float _spawnSeconds;
        private readonly int _maxBlockedRetries;
        private readonly float _blockedRetrySeconds;

        private float _countdown;
        private float _retryTimer;

        public VehicleSpawnScheduler(
            VehicleRespawnType respawnType,
            float spawnSeconds,
            int maxBlockedRetries = DefaultMaxBlockedRetries,
            float blockedRetrySeconds = DefaultBlockedRetrySeconds)
        {
            if (spawnSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(spawnSeconds));
            if (maxBlockedRetries < 1) throw new ArgumentOutOfRangeException(nameof(maxBlockedRetries));
            if (blockedRetrySeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(blockedRetrySeconds));

            _respawnType         = respawnType;
            _spawnSeconds        = spawnSeconds;
            _maxBlockedRetries   = maxBlockedRetries;
            _blockedRetrySeconds = blockedRetrySeconds;
        }

        public VehicleSpawnPhase Phase { get; private set; } = VehicleSpawnPhase.Idle;

        /// <summary>Blocked re-tests spent on the current attempt.</summary>
        public int BlockedRetries { get; private set; }

        public VehicleRespawnType RespawnType => _respawnType;

        /// <summary>The retry budget, so a caller can state it in a log line rather than guess.</summary>
        public int MaxBlockedRetries => _maxBlockedRetries;

        /// <summary>
        /// A spawn is already on its way. Defect 2's guard: scheduling again while this holds
        /// is what produced two vehicles from one spawner.
        /// </summary>
        public bool HasSpawnPending
            => Phase == VehicleSpawnPhase.CountingDown || Phase == VehicleSpawnPhase.WaitingForSpace;

        /// <summary>Spawn as soon as the pad is clear, with no countdown. The opening spawn.</summary>
        public void RequestSpawnNow()
        {
            if (HasSpawnPending) return;

            _countdown = 0f;
            EnterWaitingForSpace();
        }

        /// <summary>Schedule a spawn <c>spawnSeconds</c> from now, unless one is already pending.</summary>
        public void ScheduleRespawn()
        {
            if (HasSpawnPending) return;

            _countdown = _spawnSeconds;
            Phase      = VehicleSpawnPhase.CountingDown;
        }

        /// <summary>The spawner's vehicle was destroyed.</summary>
        /// <param name="wasLastSpawned">The dead vehicle is the one this spawner last produced.</param>
        /// <param name="hasBeenUsed">Somebody had already driven that vehicle.</param>
        /// <remarks>
        /// The two flags reproduce the original's conditions exactly. <c>AfterDestroyed</c>
        /// respawns when any of its vehicles dies; <c>AfterMoved</c> respawns only when the
        /// vehicle still sitting on its pad is destroyed, because once it has been driven off
        /// the replacement was already scheduled by <see cref="ReportFirstDriverEntered"/>.
        /// </remarks>
        public void ReportVehicleDied(bool wasLastSpawned, bool hasBeenUsed)
        {
            if (_respawnType == VehicleRespawnType.AfterDestroyed)
            {
                ScheduleRespawn();
                return;
            }

            if (_respawnType == VehicleRespawnType.AfterMoved && wasLastSpawned && !hasBeenUsed)
                ScheduleRespawn();
        }

        /// <summary>Somebody took the wheel of the vehicle this spawner last produced.</summary>
        public void ReportFirstDriverEntered(bool wasLastSpawned)
        {
            if (!wasLastSpawned) return;
            if (_respawnType != VehicleRespawnType.AfterMoved) return;

            ScheduleRespawn();
        }

        /// <summary>The caller instantiated the vehicle this scheduler asked for.</summary>
        public void ReportSpawned()
        {
            Phase          = VehicleSpawnPhase.Spawned;
            BlockedRetries = 0;
            _retryTimer    = 0f;
        }

        /// <summary>
        /// The world was torn down between rounds. Returns to <see cref="VehicleSpawnPhase.Idle"/>
        /// and cancels anything pending — the caller despawns the vehicle and decides whether the
        /// next round gets one.
        /// </summary>
        public void ReportWorldReset()
        {
            Phase          = VehicleSpawnPhase.Idle;
            BlockedRetries = 0;
            _countdown     = 0f;
            _retryTimer    = 0f;
        }

        /// <summary>Advances the cycle by one frame.</summary>
        /// <param name="deltaSeconds">Elapsed time.</param>
        /// <param name="isSpawnBlocked">
        /// Tests whether the pad is occupied. Invoked at most once per
        /// <see cref="DefaultBlockedRetrySeconds"/> and only while waiting for space — cache the
        /// delegate; a fresh lambda per call allocates once per frame per spawner.
        /// </param>
        public VehicleSpawnStep Tick(float deltaSeconds, Func<bool> isSpawnBlocked)
        {
            if (isSpawnBlocked == null) throw new ArgumentNullException(nameof(isSpawnBlocked));
            if (deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

            switch (Phase)
            {
                case VehicleSpawnPhase.CountingDown:
                    _countdown -= deltaSeconds;
                    if (_countdown > 0f) return VehicleSpawnStep.None;

                    EnterWaitingForSpace();
                    return Probe(isSpawnBlocked);

                case VehicleSpawnPhase.WaitingForSpace:
                    _retryTimer -= deltaSeconds;
                    if (_retryTimer > 0f) return VehicleSpawnStep.None;

                    return Probe(isSpawnBlocked);

                default:
                    return VehicleSpawnStep.None;
            }
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Zero retry timer, so the first test happens on the tick the countdown ends rather
        /// than a second later — the original checked <c>SpawnIsBlocked()</c> before its first
        /// <c>WaitForSeconds</c>, and a clear pad must still spawn immediately.
        /// </summary>
        private void EnterWaitingForSpace()
        {
            Phase          = VehicleSpawnPhase.WaitingForSpace;
            BlockedRetries = 0;
            _retryTimer    = 0f;
        }

        private VehicleSpawnStep Probe(Func<bool> isSpawnBlocked)
        {
            if (!isSpawnBlocked()) return new VehicleSpawnStep(shouldSpawn: true, gaveUp: false);

            _retryTimer = _blockedRetrySeconds;
            BlockedRetries++;

            if (BlockedRetries < _maxBlockedRetries) return VehicleSpawnStep.None;

            Phase = VehicleSpawnPhase.GaveUp;
            return new VehicleSpawnStep(shouldSpawn: false, gaveUp: true);
        }
    }
}
