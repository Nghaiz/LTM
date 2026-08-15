using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>What one actor's combat step did. phase-05 task 1.</summary>
    public readonly struct CombatTickResult
    {
        /// <summary>
        /// Why the trigger pull was refused, or <see cref="FireRejection.None"/>.
        /// </summary>
        /// <remarks>
        /// <b>None does not mean a shot was taken</b> — it also means no trigger was pulled.
        /// Read <see cref="Fired"/> for that. Collapsing the two would have the Unity seam
        /// broadcast an S_WEAPON_FIRE on every tick a player walked around holding nothing.
        /// </remarks>
        public readonly FireRejection Rejection;

        /// <summary>True when a shot was actually taken and ammo was spent.</summary>
        public readonly bool Fired;

        /// <summary>Projectiles that connected. Entries are in the caller's hit span.</summary>
        public readonly int HitCount;

        /// <summary>
        /// True when the ammo count changed, so the next snapshot carries
        /// <see cref="SnapshotField.Weapon"/>.
        /// </summary>
        /// <remarks>
        /// This flag is the reported bug, expressed as a boolean: the client clears its
        /// <c>_reloadPending</c> on the first snapshot that carries the weapon field, and until
        /// this phase nothing on the server ever made that field move.
        /// </remarks>
        public readonly bool WeaponChanged;

        /// <summary>True when a hit this step took a victim from alive to dead.</summary>
        public readonly bool VictimDied;

        /// <summary>The actor that died, meaningful only when <see cref="VictimDied"/>.</summary>
        public readonly ushort DeadActorId;

        /// <summary>The direction the shot was taken in, for the S_WEAPON_FIRE tracer.</summary>
        public readonly Vec3 AimDirection;

        /// <summary>Where the shot originated — eye height above the shooter's feet (D10).</summary>
        public readonly Vec3 Origin;

        public CombatTickResult(
            FireRejection rejection, bool fired, int hitCount, bool weaponChanged,
            bool victimDied, ushort deadActorId, in Vec3 aimDirection, in Vec3 origin)
        {
            Rejection = rejection;
            Fired = fired;
            HitCount = hitCount;
            WeaponChanged = weaponChanged;
            VictimDied = victimDied;
            DeadActorId = deadActorId;
            AimDirection = aimDirection;
            Origin = origin;
        }
    }

    /// <summary>
    /// Turns one accepted input frame into authoritative combat: reload, fire, damage.
    /// phase-05 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine-free, and that is decision D2 rather than a preference.</b> Written inside
    /// <c>ServerPlayer</c> this would all be MonoBehaviour code, so every combat bug would be
    /// an Editor-only bug reproducible only by two people playing at once. Here the whole of it
    /// — the ordering, the eye height, the rejection rules, the death edge — is graded by
    /// <c>dotnet test</c> in CI.
    /// </para>
    /// <para>
    /// <b>The order inside <see cref="Step"/> is load-bearing.</b> A running reload is
    /// completed first, then a fresh reload intent is considered, then the trigger. Any other
    /// order costs a frame somewhere: completing after the trigger check rejects a shot on the
    /// tick the reload finished; accepting a new reload before completing the old one restarts
    /// a reload that was about to land.
    /// </para>
    /// <para>
    /// <b>Nothing here allocates.</b> Hits are written into a caller-owned
    /// <see cref="Span{T}"/>, the resolver and the sink are constructor fields, and there is no
    /// closure anywhere on the path — this runs once per player per accepted frame at 30 Hz.
    /// </para>
    /// </remarks>
    public sealed class ServerCombatAuthority
    {
        private readonly ServerFireResolver _fireResolver;
        private readonly IActorDamageSink _damageSink;
        private readonly ServerRespawnGate _respawnGate;

        public ServerCombatAuthority(
            ServerFireResolver fireResolver,
            IActorDamageSink damageSink,
            ServerRespawnGate respawnGate)
        {
            _fireResolver = fireResolver ?? throw new ArgumentNullException(nameof(fireResolver));
            _damageSink = damageSink ?? throw new ArgumentNullException(nameof(damageSink));
            _respawnGate = respawnGate ?? throw new ArgumentNullException(nameof(respawnGate));
        }

        /// <summary>Reload intents accepted. Non-zero is the reported bug being closed.</summary>
        public long ReloadsStarted { get; private set; }

        /// <summary>Reloads that ran to completion and refilled a clip.</summary>
        public long ReloadsCompleted { get; private set; }

        /// <summary>Damage applications that took a victim from alive to dead.</summary>
        public long KillsResolved { get; private set; }

        /// <summary>
        /// Steps one actor's combat for one accepted input frame.
        /// </summary>
        /// <param name="weapon">The actor's weapon state. Mutated by a reload or a shot.</param>
        /// <param name="config">The server's copy of the weapon numbers. Never client-supplied.</param>
        /// <param name="shooterActorId">Excluded from its own hitscan sweep.</param>
        /// <param name="frame">
        /// The frame exactly as it arrived, so the buttons this phase adds —
        /// <see cref="InputButtons.Fire"/> and <see cref="InputButtons.Reload"/> — are still
        /// present. <c>MoveInput</c> drops both, which is why the observer seam carries the
        /// frame rather than the converted input.
        /// </param>
        /// <param name="state">
        /// The authoritative movement state after this frame was applied. Supplies the shot's
        /// origin and the crouch half of the stance.
        /// </param>
        /// <param name="targets">Candidate victims. Reused caller-owned storage.</param>
        /// <param name="shooterIsAlive">A corpse's queued input must not fire (D5).</param>
        /// <param name="nowSeconds">Server clock.</param>
        /// <param name="smoothedRttMs">
        /// This connection's real smoothed RTT, from the transport. Passing 0 here is the
        /// phase-02 debt this phase settles: it silently disables lag compensation, and the
        /// only symptom is high-ping players missing.
        /// </param>
        /// <param name="currentTick">The tick the shot is resolved on.</param>
        /// <param name="hits">
        /// Receives one entry per projectile that connected. Size it to
        /// <see cref="WeaponConfig.ProjectilesPerShot"/>.
        /// </param>
        public CombatTickResult Step(
            ref WeaponRuntimeState weapon,
            in WeaponConfig config,
            ushort shooterActorId,
            in InputFrame frame,
            in MoveState state,
            ReadOnlySpan<HitscanTarget> targets,
            bool shooterIsAlive,
            float nowSeconds,
            float smoothedRttMs,
            uint currentTick,
            Span<HitResult> hits)
        {
            byte ammoBefore = weapon.AmmoInClip;

            // 1. A reload already running finishes on the server's clock, before anything reads
            //    the ammo count. This is the line that makes SnapshotField.Weapon move.
            if (ServerReloadPolicy.CompleteReloadIfElapsed(ref weapon, in config, nowSeconds))
                ReloadsCompleted++;

            // 2. A fresh reload intent.
            if (frame.IsPressed(InputButtons.Reload)
                && ServerReloadPolicy.BeginReload(
                       ref weapon, in config, shooterIsAlive, nowSeconds)
                   == ServerReloadPolicy.Rejection.None)
                ReloadsStarted++;

            Vec3 origin = ShotOrigin(in state, in frame);

            if (!frame.IsPressed(InputButtons.Fire))
                return new CombatTickResult(
                    FireRejection.None, fired: false, hitCount: 0,
                    weaponChanged: weapon.AmmoInClip != ammoBefore,
                    victimDied: false, deadActorId: 0, Vec3.Zero, in origin);

            // 3. The trigger. CheckCanFire runs inside Resolve against the SERVER clock, so a
            //    client sending ten frames in one tick gets one shot and nine OnCooldown
            //    rejections — which is what moves FireRateViolations, the signal phase-05
            //    criterion 2 is graded on.
            Vec3 aim = AimDirection(frame.YawDegrees, frame.PitchDegrees);

            FireRejection rejection = _fireResolver.Resolve(
                ref weapon, in config, targets, shooterActorId, shooterIsAlive,
                in origin, in aim, nowSeconds, smoothedRttMs, currentTick,
                hits, out int hitCount);

            bool fired = rejection == FireRejection.None;
            bool victimDied = false;
            ushort deadActorId = 0;

            for (int i = 0; i < hitCount; i++)
            {
                ref readonly HitResult hit = ref hits[i];

                float damage = ServerFireResolver.DamageFor(in config, hit.HitboxType);
                DamageOutcome outcome =
                    _damageSink.ApplyDamage(hit.TargetActorId, damage, shooterActorId);

                if (!outcome.Died) continue;

                // Edge-triggered by the sink, so a shotgun blast whose second pellet lands on
                // an already-dead target reports one death, not two.
                victimDied = true;
                deadActorId = hit.TargetActorId;
                KillsResolved++;

                _respawnGate.MarkDeath(hit.TargetActorId, nowSeconds);
            }

            return new CombatTickResult(
                rejection, fired, hitCount,
                weaponChanged: weapon.AmmoInClip != ammoBefore,
                victimDied, deadActorId, in aim, in origin);
        }

        /// <summary>
        /// Where a shot leaves the shooter: their feet plus an eye height that drops when they
        /// are crouched or prone. Decision D10.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not a camera or a muzzle transform.</b> Reading either would drag hitscan into
        /// Unity against D2 and make every eye-height test an Editor test. The position is the
        /// one the client is already predicting against, so the two sides agree on it by
        /// construction.
        /// </para>
        /// <para>
        /// <b>Prone comes from the frame, crouch from the state.</b>
        /// <see cref="MoveState"/> carries <c>IsCrouching</c> and has no prone field — the
        /// simulation does not model prone — so the prone half of D10 is read from the button
        /// the frame already carries. Taking crouch from the state rather than the frame is
        /// deliberate too: the state is what the movement simulation actually applied, and a
        /// crouch that was refused because of a low ceiling must not lower the shot.
        /// </para>
        /// </remarks>
        public static Vec3 ShotOrigin(in MoveState state, in InputFrame frame)
        {
            bool lowered = state.IsCrouching || frame.IsPressed(InputButtons.Prone);

            float eye = lowered
                ? ProtocolConstants.EYE_HEIGHT_CROUCHED
                : ProtocolConstants.EYE_HEIGHT;

            return new Vec3(state.Position.X, state.Position.Y + eye, state.Position.Z);
        }

        /// <summary>
        /// The unit aim vector for a yaw/pitch pair, in the engine's left-handed Y-up frame.
        /// </summary>
        /// <remarks>
        /// Pitch is negated because the client packs Unity's euler X, where looking <i>down</i>
        /// is positive — see <c>LocalInputSource.Pitch</c>. Getting that sign backwards
        /// produces shots that are mirrored vertically: aiming at a target's head hits its
        /// feet, which at short range still hits and therefore still looks like it works.
        /// </remarks>
        public static Vec3 AimDirection(float yawDegrees, float pitchDegrees)
        {
            const float toRadians = (float)(Math.PI / 180.0);

            float yaw = yawDegrees * toRadians;
            float pitch = pitchDegrees * toRadians;

            float cosPitch = MathF.Cos(pitch);

            return new Vec3(
                MathF.Sin(yaw) * cosPitch,
                -MathF.Sin(pitch),
                MathF.Cos(yaw) * cosPitch);
        }

        /// <summary>Zeroes the counters. The resolver keeps its own.</summary>
        public void ResetStatistics()
        {
            ReloadsStarted = 0;
            ReloadsCompleted = 0;
            KillsResolved = 0;
        }
    }
}
