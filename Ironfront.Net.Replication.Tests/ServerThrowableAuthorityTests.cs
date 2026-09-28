using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    public sealed class ServerThrowableAuthorityTests
    {
        [Fact]
        public void DelayedProjectileBeginsNowAndReleasesOnItsScheduledTick()
        {
            var h = new Harness(WeaponIds.FRAG);

            CombatTickResult begin = h.Step(InputButtons.Fire, inputTick: 10, serverTick: 100);

            Assert.True(begin.ReleaseBegan);
            Assert.False(begin.Fired);
            Assert.False(begin.LaunchedProjectile);
            Assert.True(h.Weapon.PendingRelease);
            Assert.Equal(1, h.Weapon.AmmoInClip);

            Assert.False(h.Advance(128).Released);
            ThrowableTransition release = h.Advance(129);

            Assert.True(release.Released);
            Assert.Equal(1, h.Weapon.AmmoInClip);
            Assert.Equal(0, h.ReserveRounds);
        }

        [Fact]
        public void ImmediateProjectileLauncherKeepsItsExistingFirePath()
        {
            var h = new Harness(WeaponIds.BEU_AW1);

            CombatTickResult result = h.Step(InputButtons.Fire, inputTick: 10, serverTick: 100);

            Assert.False(result.ReleaseBegan);
            Assert.True(result.Fired);
            Assert.True(result.LaunchedProjectile);
            Assert.False(h.Weapon.PendingRelease);
            Assert.Equal(0, h.Weapon.AmmoInClip);
        }

        [Fact]
        public void AReloadCompletingInTheStepOfARefusedThrowStillReportsTheWeaponChanged()
        {
            var h = new Harness(WeaponIds.FRAG);

            // The clip is empty and its reload has run long enough to complete this step, while
            // the last release is still inside the 1.3 s cooldown -- so the throw is refused
            // AFTER the reload refilled the clip. Every other branch reports that clip change;
            // the delayed branch used to report only whether a throw began.
            h.Weapon.AmmoInClip = 0;
            h.Weapon.Reloading = true;
            h.Weapon.ReloadStartedAt = 0f;
            h.Weapon.LastThrowableReleaseTick = 2999;
            h.Weapon.HasThrowableReleaseTick = true;

            CombatTickResult result = h.Step(InputButtons.Fire, inputTick: 10, serverTick: 3000);

            Assert.False(result.ReleaseBegan);
            Assert.Equal(FireRejection.OnCooldown, result.Rejection);
            Assert.Equal(1, h.Weapon.AmmoInClip);
            Assert.True(result.WeaponChanged);
        }

        private sealed class Harness
        {
            private const ushort Shooter = 3;
            private const byte Slot = 2;
            private readonly WeaponConfig _config;
            private readonly ServerCombatAuthority _authority;
            private readonly ActorSpareAmmoPool _pool = new ActorSpareAmmoPool();
            private readonly ActorAmmoSource _ammo;
            private readonly HitResult[] _hits = new HitResult[1];
            private readonly MoveState _move = MoveState.AtRest(Vec3.Zero);
            private EffectiveTrigger _trigger = EffectiveTrigger.Idle;

            internal Harness(byte weaponId)
            {
                _config = WeaponCatalog.For(weaponId);
                Weapon = WeaponRuntimeState.Loaded(in _config);
                _pool.SetLoadout(Shooter, Slot, _config.SpareAmmo, _config.SpareAmmo, 0);
                _ammo = ActorAmmoSource.FromSlot(_pool, Shooter, Slot);
                _authority = new ServerCombatAuthority(
                    new ServerFireResolver(new LagCompensator(new HitboxHistory()), seed: 7),
                    new NoOpDamageSink());
            }

            internal WeaponRuntimeState Weapon;
            internal int ReserveRounds => _pool.Remaining(Shooter, Slot, in Weapon);

            internal CombatTickResult Step(InputButtons buttons, uint inputTick, uint serverTick)
            {
                InputFrame frame = InputFrame.FromFloats(0f, 0f, 0f, 0f, buttons);
                return _authority.Step(
                    ref Weapon, ref _trigger, in _config, Shooter, in frame, in _move,
                    ReadOnlySpan<HitscanTarget>.Empty,
                    new ActorFireEligibility(isAlive: true, isDeployed: true), in _ammo,
                    serverTick / (float)ProtocolConstants.SIM_TICK_RATE, 0f, serverTick, _hits,
                    inputTick);
            }

            internal ThrowableTransition Advance(uint serverTick)
                => _authority.AdvancePendingRelease(
                    ref Weapon, in _config, serverTick, in _ammo);
        }

        private sealed class NoOpDamageSink : IActorDamageSink
        {
            public DamageOutcome ApplyDamage(
                ushort actorId, float damage, float balanceDamage, ushort attackerActorId)
                => DamageOutcome.NoOp;

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }
    }
}
