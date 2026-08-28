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

        /// <summary>
        /// True when this shot LAUNCHED rather than swept, so the caller must pull the engine
        /// weapon's trigger. Ledger <b>X-42</b>.
        /// </summary>
        /// <remarks>
        /// <b>A fact about what happened, not a re-read of the config.</b> The bridge could ask
        /// <c>session.WeaponConfig.Delivery</c> again, and then two places would decide what this
        /// shot was — including on the tick a weapon switch lands between the step and the emit.
        /// <see cref="Fired"/> stays the answer to "was a round spent"; this says which of the
        /// two things spending it meant.
        /// </remarks>
        public readonly bool LaunchedProjectile;

        public CombatTickResult(
            FireRejection rejection, bool fired, int hitCount, bool weaponChanged,
            bool victimDied, ushort deadActorId, in Vec3 aimDirection, in Vec3 origin,
            bool launchedProjectile = false)
        {
            Rejection = rejection;
            Fired = fired;
            HitCount = hitCount;
            WeaponChanged = weaponChanged;
            VictimDied = victimDied;
            DeadActorId = deadActorId;
            AimDirection = aimDirection;
            Origin = origin;
            LaunchedProjectile = launchedProjectile;
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
        /// Accepted trigger pulls on a <see cref="WeaponDelivery.Projectile"/> weapon. Ledger
        /// <b>X-42</b>.
        /// </summary>
        /// <remarks>
        /// Counted here rather than inferred from the absence of hits: "fired and hit nothing"
        /// and "fired a grenade" produce the same <c>hits=0</c>, and telling them apart from an
        /// artifact was exactly what X-42 cost. A run that reports zero launches while a client
        /// held a FRAG has found this row again.
        /// </remarks>
        public long ProjectilesLaunched { get; private set; }

        /// <summary>
        /// The resolver this authority steps, for diagnostics that need to reach
        /// <see cref="ServerFireResolver.DiagnosticSpreadScale"/> or the shot counters.
        /// </summary>
        public ServerFireResolver FireResolver => _fireResolver;

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

            // 3a. A weapon that LAUNCHES does not sweep. Ledger X-42: the same trigger rules
            //     apply -- CheckCanFire is shared, not restated -- but the flight and the
            //     detonation belong to the engine (V7-D1), so this path spends the round and
            //     stops. Sweeping it as well would resolve a thrown grenade as a bullet, which
            //     is precisely what shipped: `rejection=None fired=True hits=1` at 1.2 m, zero
            //     damage, and no explosion anywhere (artifacts/lane-b/r1-grenade-03).
            if (config.Delivery == WeaponDelivery.Projectile)
            {
                FireRejection launchRejection = _fireResolver.ResolveLaunch(
                    ref weapon, in config, shooterIsAlive, nowSeconds);

                bool launched = launchRejection == FireRejection.None;
                if (launched) ProjectilesLaunched++;

                return new CombatTickResult(
                    launchRejection, launched, hitCount: 0,
                    weaponChanged: weapon.AmmoInClip != ammoBefore,
                    victimDied: false, deadActorId: 0, in aim, in origin,
                    launchedProjectile: launched);
            }

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

                // Distance comes straight off the hit — HitResult has always carried it, so
                // drop-off costs no new plumbing. Balance damage rides the same ramp (phase-V2
                // D5, D6): a sniper round that has lost half its damage at range has lost half
                // its stagger too, which is what the original's shared DamageDropOff() does.
                float damage = ServerFireResolver.DamageFor(in config, hit.HitboxType, hit.Distance);
                float balanceDamage = ServerFireResolver.BalanceDamageFor(in config, hit.Distance);

                DamageOutcome outcome = _damageSink.ApplyDamage(
                    hit.TargetActorId, damage, balanceDamage, shooterActorId);

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
            ProjectilesLaunched = 0;
        }
    }
}
