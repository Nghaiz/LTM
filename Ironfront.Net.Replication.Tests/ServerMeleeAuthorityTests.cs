using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The wrench online (owner's run of 2026-10-12). It carried the inert config, so a networked
    /// player's swing was refused for want of ammo and the wrench neither hurt nor repaired
    /// anything; nobody in either season of the live master had a single melee kill. The authority
    /// now approves the swing and the engine deals it.
    /// </summary>
    public sealed class ServerMeleeAuthorityTests
    {
        private const ushort Shooter = 3;
        private const byte Slot = 4;

        [Fact]
        public void AWrenchSwingIsApprovedForTheEngineAndSpendsNothing()
        {
            var h = new Harness();

            CombatTickResult swing = h.Step(InputButtons.Fire, tick: 100);

            Assert.True(swing.SwungMelee);
            Assert.True(swing.Fired);
            Assert.False(swing.LaunchedProjectile);
            Assert.Equal(0, swing.HitCount);
            Assert.Equal(FireRejection.None, swing.Rejection);
            Assert.Equal(1, h.Weapon.AmmoInClip);
            Assert.Equal(1, h.Authority.MeleeSwings);
        }

        [Fact]
        public void OnePressIsOneSwingAndTheWrenchsCooldownHolds()
        {
            var h = new Harness();

            Assert.True(h.Step(InputButtons.Fire, tick: 100).SwungMelee);
            Assert.False(h.Step(InputButtons.Fire, tick: 101).SwungMelee);      // still held
            Assert.False(h.Step(InputButtons.None, tick: 102).SwungMelee);
            CombatTickResult early = h.Step(InputButtons.Fire, tick: 106);       // 0.2 s after the swing
            Assert.False(early.SwungMelee);
            Assert.Equal(FireRejection.OnCooldown, early.Rejection);
            Assert.False(h.Step(InputButtons.None, tick: 107).SwungMelee);
            Assert.True(h.Step(InputButtons.Fire, tick: 112).SwungMelee);       // 0.4 s after
        }

        [Fact]
        public void OnlineTheWrenchIsMeleeAndTheGoldenWrenchStaysInert()
        {
            Assert.Equal(WeaponDelivery.Melee, WeaponCatalog.For(WeaponIds.WRENCH).Delivery);
            Assert.False(WeaponCatalog.For(WeaponIds.WRENCH).SpendsAmmo);
            Assert.False(WeaponCatalog.For(WeaponIds.WRENCH).Automatic);
            Assert.Equal(0, WeaponCatalog.For(WeaponIds.SUPER_WRENCH).ClipSize);
        }

        private sealed class Harness
        {
            private readonly WeaponConfig _config = WeaponCatalog.For(WeaponIds.WRENCH);
            private readonly ActorSpareAmmoPool _pool = new ActorSpareAmmoPool();
            private readonly ActorAmmoSource _ammo;
            private readonly HitResult[] _hits = new HitResult[4];
            private readonly MoveState _move = MoveState.AtRest(Vec3.Zero);
            private EffectiveTrigger _trigger = EffectiveTrigger.Idle;

            internal Harness()
            {
                Weapon = WeaponRuntimeState.Loaded(in _config);
                _pool.SetLoadout(Shooter, Slot, _config.SpareAmmo, _config.SpareAmmo, 0);
                _ammo = ActorAmmoSource.FromSlot(_pool, Shooter, Slot);
                Authority = new ServerCombatAuthority(
                    new ServerFireResolver(new LagCompensator(new HitboxHistory()), seed: 7),
                    new NoOpDamageSink());
            }

            internal ServerCombatAuthority Authority { get; }

            internal WeaponRuntimeState Weapon;

            internal CombatTickResult Step(InputButtons buttons, uint tick)
            {
                InputFrame frame = InputFrame.FromFloats(0f, 0f, 0f, 0f, buttons);
                return Authority.Step(
                    ref Weapon, ref _trigger, in _config, Shooter, in frame, in _move,
                    ReadOnlySpan<HitscanTarget>.Empty,
                    new ActorFireEligibility(isAlive: true, isDeployed: true), in _ammo,
                    tick / (float)ProtocolConstants.SIM_TICK_RATE, 0f, tick, _hits, tick);
            }
        }

        private sealed class NoOpDamageSink : IActorDamageSink
        {
            public DamageOutcome ApplyDamage(ushort actorId, float damage, float balanceDamage, ushort attackerActorId)
                => DamageOutcome.NoOp;

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }
    }
}
