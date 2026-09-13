using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A shooter with no targets in front of it, driven one accepted frame at a time through
    /// the effective-trigger state machine.
    /// </summary>
    /// <remarks>
    /// <b>No targets, on purpose.</b> Every test in this file and its two neighbours grades what
    /// the trigger and the reserve did, and an empty candidate set makes "the shot was taken"
    /// visible as an ammo count rather than as a hit that also depends on hitbox geometry,
    /// spread rolls and lag compensation. <c>ServerCombatAuthorityTests</c> is where hits are
    /// measured.
    /// </remarks>
    internal sealed class TriggerFixture
    {
        public const ushort Shooter = 11;

        private readonly WeaponConfig _config;

        public TriggerFixture(WeaponConfig? weapon = null, ActorAmmoSource? ammo = null)
        {
            _config = weapon ?? WeaponConfig.Rifle;
            Ammo = ammo ?? ActorAmmoSource.Unlimited(Shooter);

            Authority = new ServerCombatAuthority(
                new ServerFireResolver(new LagCompensator(new HitboxHistory()), seed: 7),
                new SilentDamageSink(),
                new ServerRespawnGate());

            Weapon = WeaponRuntimeState.Loaded(in _config);
            State = MoveState.AtRest(new Vec3(0f, MovementCore.StandHeight * 0.5f, 0f));
            Hits = new HitResult[Math.Max(1, _config.ProjectilesPerShot)];
        }

        public ServerCombatAuthority Authority { get; }

        public WeaponConfig Config => _config;

        public WeaponRuntimeState Weapon;
        public EffectiveTrigger Trigger = EffectiveTrigger.Idle;
        public ActorAmmoSource Ammo;
        public ActorFireEligibility Actor = ActorFireEligibility.OnFoot(isAlive: true);
        public MoveState State;
        public HitResult[] Hits { get; }

        public CombatTickResult Step(float now, InputButtons buttons = InputButtons.None)
            => Authority.Step(
                ref Weapon, ref Trigger, in _config, Shooter, Frame(buttons), in State,
                ReadOnlySpan<HitscanTarget>.Empty, in Actor, in Ammo,
                now, smoothedRttMs: 0f,
                currentTick: (uint)(now * ProtocolConstants.SIM_TICK_RATE), Hits);

        /// <summary>Runs <paramref name="ticks"/> frames at the sim rate and counts the shots.</summary>
        public int StepFor(int ticks, InputButtons buttons, float startAt = 0f)
        {
            int fired = 0;

            for (int i = 0; i < ticks; i++)
            {
                float now = startAt + i / (float)ProtocolConstants.SIM_TICK_RATE;
                if (Step(now, buttons).Fired) fired++;
            }

            return fired;
        }

        public static InputFrame Frame(InputButtons buttons)
            => new InputFrame(0, 0, 0, 0, buttons);

        /// <summary>A sink nothing is ever shot at, because these tests supply no targets.</summary>
        private sealed class SilentDamageSink : IActorDamageSink
        {
            public DamageOutcome ApplyDamage(
                ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)
                => DamageOutcome.NoOp;

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }
    }

    /// <summary>
    /// Handoff section 5.1: the sprint rule the server never learned, and the window after it.
    /// </summary>
    /// <remarks>
    /// The reported symptom was a magazine draining with no muzzle flash: the client's own
    /// <c>FpsActorController.Fire()</c> refused the shot while sprinting, the server did not,
    /// and the round was taken for a shot nobody ever saw.
    /// </remarks>
    public sealed class SprintFireGateTests
    {
        [Fact]
        public void FireWhileSprintingTakesNoShotAndSpendsNothing()
        {
            var fixture = new TriggerFixture();
            byte before = fixture.Weapon.AmmoInClip;

            int fired = fixture.StepFor(10, InputButtons.Fire | InputButtons.Sprint);

            Assert.Equal(0, fired);
            Assert.Equal(before, fixture.Weapon.AmmoInClip);
            Assert.Equal(10L, fixture.Authority.SprintBlockedTriggers);
        }

        [Fact]
        public void SprintReleasedWithFireStillHeldFiresNothingUntilTheWindowExpires()
        {
            // A semi-automatic, so "exactly one" is a real assertion rather than a restatement
            // of the cooldown: an automatic would fire again three ticks later and the test
            // would pass whatever the window did.
            var fixture = new TriggerFixture(SemiAuto);

            fixture.Step(0f, InputButtons.Fire | InputButtons.Sprint);

            // The block is stamped from the last sprint frame, so it runs to 0.2 s.
            Assert.Equal(0, fixture.StepFor(6, InputButtons.Fire, startAt: 0.033f));
            Assert.Equal(0, fixture.Weapon.ClipSpent(fixture.Config));

            int afterWindow = fixture.StepFor(6, InputButtons.Fire, startAt: 0.25f);

            Assert.Equal(1, afterWindow);
            Assert.Equal(1, fixture.Weapon.ClipSpent(fixture.Config));
        }

        [Fact]
        public void HipFireWithoutAimStillFiresWhenNotSprinting()
        {
            // Aim is not a precondition. Requiring it would make every hip-fired shot vanish on
            // the server while the client rendered it -- the same disagreement as the sprint
            // defect, pointed the other way.
            var fixture = new TriggerFixture();

            CombatTickResult result = fixture.Step(0f, InputButtons.Fire);

            Assert.True(result.Fired);
            Assert.True(result.EffectiveTriggerDown);
            Assert.False(result.BlockedBySprint);
        }

        [Fact]
        public void SprintLowersTheWeaponAndTheEndOfTheSprintRaisesIt()
        {
            var fixture = new TriggerFixture();

            fixture.Step(0f, InputButtons.Sprint);
            Assert.False(fixture.Weapon.Unholstered);

            fixture.Step(0.033f, InputButtons.None);
            Assert.True(fixture.Weapon.Unholstered);
        }

        [Fact]
        public void TheSprintRuleDoesNotRaiseAWeaponItDidNotLower()
        {
            // A weapon holstered by a switch is in a bag on purpose. Raising it because the
            // player happens not to be sprinting would let a shot leave it.
            var fixture = new TriggerFixture();
            fixture.Weapon.Unholstered = false;

            fixture.Step(0f, InputButtons.Fire);

            Assert.False(fixture.Weapon.Unholstered);
            Assert.Equal(0, fixture.Weapon.ClipSpent(fixture.Config));
        }

        [Fact]
        public void TheWindowIsMeasuredFromTheEndOfTheSprintAndComesFromTheSharedConstant()
        {
            // Stamped once on the leading edge it would expire mid-sprint; and the duration is
            // read from ProtocolConstants rather than written as 0.2 anywhere, because a client
            // blocking for 0.2 s against a server blocking for 0.25 s is the same disagreement
            // in a narrower band.
            var fixture = new TriggerFixture();

            fixture.Step(0f, InputButtons.Sprint);
            fixture.Step(1f, InputButtons.Sprint);

            float expected = 1f + ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS;
            Assert.True(
                Math.Abs(expected - fixture.Trigger.SprintFireBlockedUntil) < 1e-5f,
                $"block ran to {fixture.Trigger.SprintFireBlockedUntil}, expected {expected}");
        }

        [Fact]
        public void ADeadOrUndeployedActorHasNoEffectiveTrigger()
        {
            var fixture = new TriggerFixture { Actor = new ActorFireEligibility(true, isDeployed: false) };

            Assert.False(fixture.Step(0f, InputButtons.Fire).EffectiveTriggerDown);

            fixture.Actor = ActorFireEligibility.OnFoot(isAlive: false);
            Assert.Equal(FireRejection.ShooterDead, fixture.Step(0.1f, InputButtons.Fire).Rejection);
        }

        [Fact]
        public void ASeatWithoutACarriedWeaponHasNoEffectiveTrigger()
        {
            var fixture = new TriggerFixture
            {
                Actor = new ActorFireEligibility(
                    isAlive: true, isDeployed: true, isSeatedWithoutCarriedWeapon: true),
            };

            Assert.Equal(0, fixture.StepFor(5, InputButtons.Fire));
        }

        /// <summary>A rifle's numbers with one press per shot.</summary>
        internal static WeaponConfig SemiAuto => new WeaponConfig(
            cooldown: 0.1f, spread: 0f, projectilesPerShot: 1, range: 300f,
            damage: 25f, force: 200f, clipSize: 30, automatic: false);
    }

    internal static class WeaponRuntimeStateTestExtensions
    {
        /// <summary>Rounds gone from a full clip. Reads better in an assertion than the count.</summary>
        public static int ClipSpent(this in WeaponRuntimeState state, in WeaponConfig config)
            => config.ClipSize - state.AmmoInClip;
    }
}
