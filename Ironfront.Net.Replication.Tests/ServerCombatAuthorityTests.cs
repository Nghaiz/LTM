using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-05 tasks 1, 2 and 6: the server owns combat.
    /// </summary>
    /// <remarks>
    /// Every test here runs without Unity, which is the point of decision D2. Written inside
    /// <c>ServerPlayer</c> the same logic would be reachable only from a Play Mode session with
    /// two participants, so none of it would be graded on any commit.
    /// </remarks>
    public sealed class ServerCombatAuthorityTests
    {
        private const ushort Shooter = 1;
        private const ushort Victim = 2;

        // ------------------------------------------------------------------ reload

        [Fact]
        public void AReloadCompletesOnTheServerClock()
        {
            var fixture = new CombatFixture();
            fixture.Weapon.AmmoInClip = 10;

            fixture.Step(now: 10f, InputButtons.Reload);
            Assert.True(fixture.Weapon.Reloading);
            Assert.Equal(10, fixture.Weapon.AmmoInClip);

            // One tick short. The clip must NOT be full yet — a reload that completes early is
            // the same class of bug as one that never completes, and only one of the two is
            // visible to a player.
            fixture.Step(now: 10f + ProtocolConstants.RELOAD_SECONDS - 0.05f);
            Assert.True(fixture.Weapon.Reloading);
            Assert.Equal(10, fixture.Weapon.AmmoInClip);

            CombatTickResult result = fixture.Step(now: 10f + ProtocolConstants.RELOAD_SECONDS);

            Assert.False(fixture.Weapon.Reloading);
            Assert.Equal(WeaponConfig.Rifle.ClipSize, fixture.Weapon.AmmoInClip);

            // The whole reason this phase exists: the ammo count moved, so the next snapshot
            // carries SnapshotField.Weapon and the client's _reloadPending finally clears.
            Assert.True(result.WeaponChanged);
        }

        [Fact]
        public void AReloadIsRefusedWhenTheClipIsFull()
        {
            var fixture = new CombatFixture();

            fixture.Step(now: 10f, InputButtons.Reload);

            Assert.False(fixture.Weapon.Reloading);
            Assert.Equal(0, fixture.Authority.ReloadsStarted);
        }

        [Fact]
        public void AReloadIsRefusedWhileDead()
        {
            var fixture = new CombatFixture();
            fixture.Weapon.AmmoInClip = 1;

            fixture.Step(now: 10f, InputButtons.Reload, shooterIsAlive: false);

            Assert.False(fixture.Weapon.Reloading);
        }

        [Fact]
        public void AReloadIsRefusedWhileHolstered()
        {
            var fixture = new CombatFixture();
            fixture.Weapon.AmmoInClip = 1;
            fixture.Weapon.Unholstered = false;

            fixture.Step(now: 10f, InputButtons.Reload);

            Assert.False(fixture.Weapon.Reloading);
        }

        [Fact]
        public void RePressingReloadDoesNotRestartTheClock()
        {
            // Otherwise a player leaning on R never finishes reloading, and the symptom is a
            // reload that "sometimes takes ages" rather than anything that looks like a bug.
            var fixture = new CombatFixture();
            fixture.Weapon.AmmoInClip = 5;

            fixture.Step(now: 10f, InputButtons.Reload);
            fixture.Step(now: 11f, InputButtons.Reload);
            fixture.Step(now: 12f, InputButtons.Reload);

            Assert.Equal(WeaponConfig.Rifle.ClipSize, fixture.Weapon.AmmoInClip);
            Assert.Equal(1, fixture.Authority.ReloadsStarted);
        }

        [Fact]
        public void TheClientAndServerReloadDurationsAreOneNumber()
        {
            // D3. Two literals agree until somebody tunes one of them, and the only symptom is
            // a clip that refills at a slightly different moment on each side.
            Assert.Equal(
                Client.ClientCombatState.DefaultReloadSeconds, ServerReloadPolicy.ReloadSeconds);

            Assert.Equal(
                Client.ClientCombatState.DefaultRespawnDelaySeconds,
                ServerRespawnGate.RespawnSeconds);
        }

        // ------------------------------------------------------------------ fire

        [Fact]
        public void FiringSpendsServerAmmoAndHonoursCooldown()
        {
            var fixture = new CombatFixture();
            byte before = fixture.Weapon.AmmoInClip;

            CombatTickResult first = fixture.Step(now: 10f, InputButtons.Fire);

            Assert.True(first.Fired);
            Assert.Equal(before - 1, fixture.Weapon.AmmoInClip);
            Assert.True(first.WeaponChanged);

            // Inside the cooldown. A modified client firing as fast as the network allows gets
            // one shot and a violation, which is the signal criterion 2 is graded on.
            CombatTickResult second = fixture.Step(
                now: 10f + WeaponConfig.Rifle.Cooldown * 0.5f, InputButtons.Fire);

            Assert.False(second.Fired);
            Assert.Equal(FireRejection.OnCooldown, second.Rejection);
            Assert.Equal(before - 1, fixture.Weapon.AmmoInClip);
            Assert.Equal(1, fixture.Resolver.FireRateViolations);
        }

        [Fact]
        public void FiringIsRefusedWhileReloadingAndDoesNotCancelTheReload()
        {
            // Decision D7: the server mirrors the client's rules exactly. A fire that cancelled
            // the reload here but not on the client would leave the two disagreeing about
            // whether a reload was in flight for the rest of the life.
            var fixture = new CombatFixture();
            fixture.Weapon.AmmoInClip = 5;

            fixture.Step(now: 10f, InputButtons.Reload);

            CombatTickResult result = fixture.Step(now: 10.5f, InputButtons.Fire);

            Assert.Equal(FireRejection.Reloading, result.Rejection);
            Assert.False(result.Fired);
            Assert.True(fixture.Weapon.Reloading);
            Assert.Equal(5, fixture.Weapon.AmmoInClip);

            // And it still finishes on its original clock.
            fixture.Step(now: 10f + ProtocolConstants.RELOAD_SECONDS);
            Assert.Equal(WeaponConfig.Rifle.ClipSize, fixture.Weapon.AmmoInClip);
        }

        [Fact]
        public void NotPressingTheTriggerIsNotAShot()
        {
            // FireRejection.None means "nothing was refused", which is also what an unpressed
            // trigger produces. Collapsing the two would have the Unity seam broadcast an
            // S_WEAPON_FIRE for every tick a player spent walking around.
            var fixture = new CombatFixture();

            CombatTickResult result = fixture.Step(now: 10f);

            Assert.Equal(FireRejection.None, result.Rejection);
            Assert.False(result.Fired);
            Assert.Equal(0, result.HitCount);
            Assert.Equal(WeaponConfig.Rifle.ClipSize, fixture.Weapon.AmmoInClip);
        }

        [Fact]
        public void ADeadShootersQueuedInputDoesNotFire()
        {
            var fixture = new CombatFixture();

            CombatTickResult result = fixture.Step(
                now: 10f, InputButtons.Fire, shooterIsAlive: false);

            Assert.Equal(FireRejection.ShooterDead, result.Rejection);
            Assert.False(result.Fired);
        }

        // ------------------------------------------------------------------ damage and death

        [Fact]
        public void DamageReachingZeroHealthEmitsDeath()
        {
            var fixture = new CombatFixture();

            // A level shot from standing eye height lands on the head: 25 x 4 = 100.
            CombatTickResult result = fixture.Step(now: 10f, InputButtons.Fire);

            Assert.True(result.Fired);
            Assert.Equal(1, result.HitCount);
            Assert.True(result.VictimDied);
            Assert.Equal(Victim, result.DeadActorId);
            Assert.Equal(0f, fixture.Sink.HealthOf(Victim));
            Assert.Equal(1, fixture.Sink.DeathsReported);
            Assert.Equal(1, fixture.Authority.KillsResolved);
        }

        [Fact]
        public void ADamagedButLivingVictimReportsNoDeath()
        {
            var fixture = new CombatFixture();
            fixture.Sink.SetHealth(Victim, 500f);

            CombatTickResult result = fixture.Step(now: 10f, InputButtons.Fire);

            Assert.Equal(1, result.HitCount);
            Assert.False(result.VictimDied);
            Assert.Equal(400f, fixture.Sink.HealthOf(Victim));
            Assert.Equal(0, fixture.Sink.DeathsReported);
        }

        [Fact]
        public void AShotgunBlastThatKillsReportsExactlyOneDeath()
        {
            // Criterion 4's "exactly once". Two pellets crossing zero on the same trigger pull
            // would otherwise call ReportDeath twice, take two tickets off the team, and end the
            // round early — which reads as a scoring bug rather than a damage bug.
            var shotgun = new WeaponConfig(
                cooldown: 0.8f, spread: 0.03f, projectilesPerShot: 8, range: 50f,
                damage: 40f, force: 300f, clipSize: 6);

            var fixture = new CombatFixture(shotgun);
            fixture.Sink.SetHealth(Victim, 50f);

            CombatTickResult result = fixture.Step(now: 10f, InputButtons.Fire);

            Assert.True(result.HitCount > 1, "a shotgun at 10 m should land more than one pellet");
            Assert.True(result.VictimDied);
            Assert.Equal(1, fixture.Sink.DeathsReported);
            Assert.Equal(1, fixture.Authority.KillsResolved);
        }

        [Fact]
        public void AKilledVictimIsStampedIntoTheRespawnGate()
        {
            var fixture = new CombatFixture();

            fixture.Step(now: 10f, InputButtons.Fire);

            Assert.True(fixture.RespawnGate.IsDead(Victim));
            Assert.Equal(
                ProtocolConstants.RESPAWN_SECONDS,
                fixture.RespawnGate.SecondsUntilRespawn(Victim, 10f), 3);
        }

        // ------------------------------------------------------------------ respawn gate

        [Fact]
        public void ARespawnRequestBeforeTheDelayIsRefused()
        {
            var gate = new ServerRespawnGate();
            gate.MarkDeath(Victim, 10f);

            Assert.False(gate.MayRespawn(Victim, 10f));
            Assert.False(gate.MayRespawn(Victim, 10f + ProtocolConstants.RESPAWN_SECONDS - 0.01f));
            Assert.Equal(2, gate.EarlyRequestsRefused);

            // At the boundary exactly. Refusing here would penalise a client whose clock is
            // perfect, which is the same reasoning the fire cooldown uses.
            Assert.True(gate.MayRespawn(Victim, 10f + ProtocolConstants.RESPAWN_SECONDS));
        }

        [Fact]
        public void ARespawnStaysAvailableUntilItIsTaken()
        {
            // Clearing the record when the delay elapses instead of when the respawn is granted
            // would make MayRespawn answer true once and false forever, so a player who did not
            // press the button immediately could never come back at all.
            var gate = new ServerRespawnGate();
            gate.MarkDeath(Victim, 10f);

            float ready = 10f + ProtocolConstants.RESPAWN_SECONDS;

            Assert.True(gate.MayRespawn(Victim, ready));
            Assert.True(gate.MayRespawn(Victim, ready + 5f));

            gate.MarkRespawned(Victim);

            Assert.False(gate.IsDead(Victim));
            Assert.False(gate.MayRespawn(Victim, ready + 6f));
        }

        [Fact]
        public void ASecondDeathStampDoesNotPushTheClockOut()
        {
            var gate = new ServerRespawnGate();
            gate.MarkDeath(Victim, 10f);
            gate.MarkDeath(Victim, 12f);

            Assert.True(gate.MayRespawn(Victim, 10f + ProtocolConstants.RESPAWN_SECONDS));
        }

        // ------------------------------------------------------------------ eye height (D10)

        [Fact]
        public void AShotOriginatesAtEyeHeightAndDropsWhenCrouched()
        {
            var standing = MoveState.AtRest(Vec3.Zero);
            MoveState crouched = MoveState.AtRest(Vec3.Zero);
            crouched.IsCrouching = true;

            InputFrame frame = Frame(InputButtons.None);

            Assert.Equal(
                ProtocolConstants.EYE_HEIGHT,
                ServerCombatAuthority.ShotOrigin(in standing, in frame).Y, 4);

            Assert.Equal(
                ProtocolConstants.EYE_HEIGHT_CROUCHED,
                ServerCombatAuthority.ShotOrigin(in crouched, in frame).Y, 4);

            // Prone is a button, not a MoveState field — the simulation does not model it — so
            // the frame supplies that half of the stance.
            Assert.Equal(
                ProtocolConstants.EYE_HEIGHT_CROUCHED,
                ServerCombatAuthority.ShotOrigin(in standing, Frame(InputButtons.Prone)).Y, 4);
        }

        [Fact]
        public void AShotFiredFromTheFeetMissesWhatAStandingShotHits()
        {
            // The assertion that catches an eye height wired to zero, which nothing else would:
            // against a target standing on the ground, a shot from the feet still lands — on the
            // legs — so only an elevated target makes the difference decisive.
            var elevated = new Vec3(0f, 1.2f, 10f);

            var standing = new CombatFixture(victimFeet: elevated);
            CombatTickResult hit = standing.Step(now: 10f, InputButtons.Fire);

            var crouching = new CombatFixture(victimFeet: elevated);
            crouching.State.IsCrouching = true;
            CombatTickResult miss = crouching.Step(now: 10f, InputButtons.Fire);

            Assert.Equal(1, hit.HitCount);
            Assert.Equal(0, miss.HitCount);

            // Both spent a round: the shot was legal either way, it simply went under the target.
            Assert.True(miss.Fired);
        }

        [Fact]
        public void EyeHeightDecidesWhichHitboxAShotLandsOn()
        {
            var onTheGround = new Vec3(0f, 0f, 10f);

            var standing = new CombatFixture(victimFeet: onTheGround);
            CombatTickResult high = standing.Step(now: 10f, InputButtons.Fire);

            var crouching = new CombatFixture(victimFeet: onTheGround);
            crouching.State.IsCrouching = true;
            CombatTickResult low = crouching.Step(now: 10f, InputButtons.Fire);

            Assert.Equal(HitboxType.Head, standing.Hits[0].HitboxType);
            Assert.Equal(HitboxType.Limb, crouching.Hits[0].HitboxType);
            Assert.Equal(1, high.HitCount);
            Assert.Equal(1, low.HitCount);
        }

        [Fact]
        public void AimPitchPointsDownwardsForAPositivePitch()
        {
            // The client packs Unity's euler X, where looking DOWN is positive. Getting the sign
            // backwards mirrors every shot vertically — and at short range it still hits, so it
            // still looks like it works.
            Vec3 down = ServerCombatAuthority.AimDirection(yawDegrees: 0f, pitchDegrees: 45f);
            Vec3 level = ServerCombatAuthority.AimDirection(yawDegrees: 0f, pitchDegrees: 0f);

            Assert.True(down.Y < 0f, $"a positive pitch aimed upwards: {down.Y}");
            Assert.Equal(0f, level.Y, 4);
            Assert.Equal(1f, level.Z, 4);
            Assert.Equal(1f, down.Magnitude, 3);
        }

        [Fact]
        public void AimYawFollowsTheSameConventionAsTheInterestViewCone()
        {
            // Yaw 90 is +X in this project's frame. If these two disagreed, a player would be
            // shot at from a direction nobody was looking.
            Vec3 east = ServerCombatAuthority.AimDirection(yawDegrees: 90f, pitchDegrees: 0f);

            Assert.Equal(1f, east.X, 3);
            Assert.Equal(0f, east.Z, 3);
        }

        // ------------------------------------------------------------------ task 2, the seam

        [Fact]
        public void TheObserverSeesOnlyAcceptedFrames()
        {
            var session = new ClientSession(connectionId: 7, actorId: Shooter);
            var observer = new RecordingObserver();

            // Enqueued before anything has been processed, so the ring accepts the duplicate —
            // the session only screens against LastProcessedInputTick once HasInput is set. It
            // is TryAccept, inside the drain, that must throw the repeat away.
            session.EnqueueInput(1, Frame(InputButtons.Fire));
            session.EnqueueInput(2, Frame(InputButtons.Fire));
            session.EnqueueInput(2, Frame(InputButtons.Fire));
            session.EnqueueInput(3, Frame(InputButtons.Fire));

            int steps = InputAuthority.ApplyPendingInput(
                session, dt: 1f / 30f, applyMove: Move, observer: observer);

            Assert.Equal(3, steps);
            Assert.Equal(new uint[] { 1, 2, 3 }, observer.Ticks);
        }

        [Fact]
        public void TheObserverIsNotNotifiedForACoastedTick()
        {
            // Coasting repeats a MOVEMENT intent to cover one dropped packet. Repeating the
            // combat intent too would hand a player whose connection hiccuped three free rounds
            // from a frame the anti-cheat has already graded.
            var session = new ClientSession(connectionId: 7, actorId: Shooter);
            var observer = new RecordingObserver();

            session.EnqueueInput(1, Frame(InputButtons.Fire));
            InputAuthority.ApplyPendingInput(session, 1f / 30f, Move, observer);

            int coasted = InputAuthority.ApplyPendingInput(session, 1f / 30f, Move, observer);

            Assert.Equal(1, coasted);
            Assert.Single(observer.Ticks);
        }

        [Fact]
        public void TheObserverSeesTheButtonsMoveInputThrowsAway()
        {
            // The root cause, asserted directly: MoveInput.FromFrame keeps Jump, Sprint and
            // Crouch and drops Fire, Reload and Aim, so a seam carrying MoveInput instead of the
            // frame would reproduce the original bug exactly.
            var session = new ClientSession(connectionId: 7, actorId: Shooter);
            var observer = new RecordingObserver();

            session.EnqueueInput(1, Frame(InputButtons.Fire | InputButtons.Reload));
            InputAuthority.ApplyPendingInput(session, 1f / 30f, Move, observer);

            Assert.True(observer.Frames[0].IsPressed(InputButtons.Fire));
            Assert.True(observer.Frames[0].IsPressed(InputButtons.Reload));
        }

        [Fact]
        public void TheThreeArgumentOverloadStillWorks()
        {
            // Movement's callers were not edited, and this is what says so.
            var session = new ClientSession(connectionId: 7, actorId: Shooter);
            session.EnqueueInput(1, Frame(InputButtons.None));

            Assert.Equal(1, InputAuthority.ApplyPendingInput(session, 1f / 30f, Move));
        }

        // ------------------------------------------------------------------ task 6, the wire half

        [Fact]
        public void AWorldKillIsFramedAsAnEnvironmentDeath()
        {
            // Bot and world damage reaches the netcode through the Actor.Damage guard, which has
            // no shooter to name — a fall, a grenade with no owner, a vehicle. The sentinel is
            // what stops the killfeed crediting actor 0, which is a real id.
            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];

            var message = new DeathMessage(
                Victim, DeathMessage.EnvironmentKiller, CauseOfDeath.Explosion,
                0, 0, 0, (byte)HitboxType.Body);

            int written = ServerEventWriter.WriteDeath(payload, in message);
            Assert.True(written > 0);

            Assert.True(message.KilledByEnvironment);
        }

        [Fact]
        public void TheLocalPlayerAcceptsAnEnvironmentDeath()
        {
            // The guard's client branch skips Die() and waits for this. If S_DEATH with the
            // environment sentinel were rejected here, a player killed by a bot would keep
            // walking around on their own screen at zero health.
            var state = new Client.ClientCombatState { LocalActorId = Victim };

            bool wasLocal = state.ApplyDeath(
                new DeathMessage(
                    Victim, DeathMessage.EnvironmentKiller, CauseOfDeath.Explosion,
                    0, 0, 0, (byte)HitboxType.Body),
                nowSeconds: 10f);

            Assert.True(wasLocal);
            Assert.False(state.IsAlive);
            Assert.False(state.CanRequestRespawn(10f));
            Assert.True(state.CanRequestRespawn(10f + ProtocolConstants.RESPAWN_SECONDS));
        }

        [Fact]
        public void ASecondDeathFromTheOtherDamagePathDoesNotMoveTheRespawnClock()
        {
            // Both damage paths stamp the gate — hitscan through ServerCombatAuthority, bot and
            // world damage through the Actor.Damage guard — and a victim can be hit by both on
            // the same tick. The stamp has to be idempotent or the countdown jumps.
            var gate = new ServerRespawnGate();

            gate.MarkDeath(Victim, 10f);
            gate.MarkDeath(Victim, 10.5f);

            Assert.True(gate.MayRespawn(Victim, 10f + ProtocolConstants.RESPAWN_SECONDS));
        }

        // ------------------------------------------------------------------ helpers

        private static Vec3 Move(Vec3 motion) => motion;

        private static InputFrame Frame(
            InputButtons buttons, float yaw = 0f, float pitch = 0f)
            => InputFrame.FromFloats(0f, 0f, yaw, pitch, buttons);

        /// <summary>Records every frame the observer seam delivers.</summary>
        private sealed class RecordingObserver : IAcceptedFrameObserver
        {
            public List<uint> Ticks { get; } = new List<uint>();
            public List<InputFrame> Frames { get; } = new List<InputFrame>();

            public void OnAcceptedFrame(
                ClientSession session, uint frameTick, in InputFrame frame, in MoveInput input)
            {
                Ticks.Add(frameTick);
                Frames.Add(frame);
            }
        }

        /// <summary>
        /// A damage sink over a dictionary, mirroring the Unity one's edge-triggered death.
        /// </summary>
        private sealed class FakeDamageSink : IActorDamageSink
        {
            private readonly Dictionary<ushort, float> _health = new Dictionary<ushort, float>();
            private readonly HashSet<ushort> _dead = new HashSet<ushort>();

            public long DeathsReported { get; private set; }

            public void SetHealth(ushort actorId, float health)
            {
                _health[actorId] = health;
                _dead.Remove(actorId);
            }

            public float HealthOf(ushort actorId)
                => _health.TryGetValue(actorId, out float h) ? h : 0f;

            public DamageOutcome ApplyDamage(ushort victimId, float amount, ushort attackerId)
            {
                if (_dead.Contains(victimId)) return new DamageOutcome(0f, died: false);
                if (!_health.TryGetValue(victimId, out float health)) return DamageOutcome.NoOp;

                float remaining = health - amount;
                if (remaining < 0f) remaining = 0f;

                _health[victimId] = remaining;
                if (remaining > 0f) return new DamageOutcome(remaining, died: false);

                _dead.Add(victimId);
                DeathsReported++;
                return new DamageOutcome(0f, died: true);
            }
        }

        /// <summary>
        /// A shooter at the origin, a humanoid victim 10 m down +Z, and everything in between.
        /// </summary>
        private sealed class CombatFixture
        {
            private readonly WeaponConfig _config;
            private readonly HitscanTarget[] _targets;

            public CombatFixture(WeaponConfig? weapon = null, Vec3? victimFeet = null)
            {
                _config = weapon ?? WeaponConfig.Rifle;

                Compensator = new LagCompensator(new HitboxHistory());
                Resolver = new ServerFireResolver(Compensator, seed: 7);
                Sink = new FakeDamageSink();
                RespawnGate = new ServerRespawnGate();
                Authority = new ServerCombatAuthority(Resolver, Sink, RespawnGate);

                Weapon = WeaponRuntimeState.Loaded(in _config);
                State = MoveState.AtRest(Vec3.Zero);
                Hits = new HitResult[Math.Max(1, _config.ProjectilesPerShot)];

                Sink.SetHealth(Victim, 100f);

                _targets = new[]
                {
                    new HitscanTarget(
                        Victim, true, HitboxSet.Humanoid(victimFeet ?? new Vec3(0f, 0f, 10f))),
                };
            }

            public LagCompensator Compensator { get; }
            public ServerFireResolver Resolver { get; }
            public FakeDamageSink Sink { get; }
            public ServerRespawnGate RespawnGate { get; }
            public ServerCombatAuthority Authority { get; }

            public WeaponRuntimeState Weapon;
            public MoveState State;
            public HitResult[] Hits { get; }

            public CombatTickResult Step(
                float now, InputButtons buttons = InputButtons.None, bool shooterIsAlive = true)
                => Authority.Step(
                    ref Weapon, in _config, Shooter, Frame(buttons), in State,
                    _targets, shooterIsAlive, now, smoothedRttMs: 0f,
                    currentTick: (uint)(now * ProtocolConstants.SIM_TICK_RATE), Hits);
        }
    }
}
