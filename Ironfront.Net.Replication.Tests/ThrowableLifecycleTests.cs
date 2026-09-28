using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    public sealed class ThrowableLifecycleTests
    {
        private static readonly Vec3 Aim = new Vec3(0.25f, 0.5f, 1f);

        [Theory]
        [InlineData(WeaponIds.FRAG, 1, 1, 2, 29)]
        [InlineData(WeaponIds.SPEARHEAD, 1, 2, 3, 29)]
        [InlineData(WeaponIds.AMMO_BAG, 1, WeaponConfig.NoResupplySpareAmmo, 1, 10)]
        [InlineData(WeaponIds.MEDIPACK, 1, WeaponConfig.NoResupplySpareAmmo, 1, 10)]
        public void CataloguePinsRavenfieldThrowableInventoryAndReleaseTiming(
            byte weaponId, byte clip, short reserve, int totalUses, ushort releaseTicks)
        {
            WeaponConfig config = WeaponCatalog.For(weaponId);

            Assert.Equal(clip, config.ClipSize);
            Assert.Equal(reserve, config.SpareAmmo);
            Assert.Equal(totalUses, clip + Math.Max(0, (int)reserve));
            Assert.Equal(releaseTicks, config.ReleaseDelayTicks);
            Assert.True(config.HasDelayedRelease);
        }

        [Fact]
        public void FragCommitsAtReleaseAndRefillsFromReserveExactlyOnce()
        {
            var h = new Harness(WeaponIds.FRAG);

            Assert.Equal(ThrowableRejection.None, h.Begin(inputTick: 100, serverTick: 500));
            Assert.True(h.State.PendingRelease);
            Assert.Equal(1, h.State.AmmoInClip);
            Assert.Equal(1, h.ReserveRounds);

            Assert.False(h.Release(serverTick: 528).Released);
            ThrowableTransition released = h.Release(serverTick: 529);

            Assert.True(released.Released);
            Assert.Equal(Aim, released.Aim);
            Assert.False(h.State.PendingRelease);
            Assert.Equal(1, h.State.AmmoInClip);
            Assert.Equal(0, h.ReserveRounds);
            Assert.False(h.Release(serverTick: 530).Released);
        }

        [Fact]
        public void FragAndSpearheadReachTheirExactStableInventorySequences()
        {
            var frag = new Harness(WeaponIds.FRAG);
            frag.ThrowAt(100);
            Assert.Equal((1, 0), frag.Inventory);
            frag.ThrowAt(200);
            Assert.Equal((0, 0), frag.Inventory);
            Assert.Equal(ThrowableRejection.NoAmmo, frag.Begin(3, 300));

            var spearhead = new Harness(WeaponIds.SPEARHEAD);
            spearhead.ThrowAt(100);
            Assert.Equal((1, 1), spearhead.Inventory);
            spearhead.ThrowAt(200);
            Assert.Equal((1, 0), spearhead.Inventory);
            spearhead.ThrowAt(300);
            Assert.Equal((0, 0), spearhead.Inventory);
        }

        [Theory]
        [InlineData(WeaponIds.AMMO_BAG)]
        [InlineData(WeaponIds.MEDIPACK)]
        public void NoResupplyDeployableHasOneReleaseAndNeverRefills(byte weaponId)
        {
            var h = new Harness(weaponId);

            h.ThrowAt(100);

            Assert.Equal((0, 0), h.Inventory);
            Assert.Equal(SpareAmmoKind.NoResupply, h.Reserve.Kind);
            Assert.Equal(ThrowableRejection.NoAmmo, h.Begin(2, 200));
        }

        [Fact]
        public void DuplicateBeginCannotReserveTheHeldObjectTwice()
        {
            var h = new Harness(WeaponIds.FRAG);

            Assert.Equal(ThrowableRejection.None, h.Begin(10, 100));
            Assert.Equal(ThrowableRejection.AlreadyPending, h.Begin(11, 101));
            Assert.Equal((1, 1), h.Inventory);
            Assert.Equal(10u, h.State.PendingInputTick);
        }

        [Fact]
        public void CancelRestoresReadinessWithoutChangingInventory()
        {
            var h = new Harness(WeaponIds.FRAG);
            h.Begin(10, 100);

            Assert.True(ThrowableLifecycle.Cancel(ref h.State));

            Assert.False(h.State.PendingRelease);
            Assert.Equal((1, 1), h.Inventory);
            Assert.Equal(ThrowableRejection.None, h.Begin(11, 101));
        }

        [Fact]
        public void FailedLaunchRollbackRestoresInventoryAndEndsTheTransaction()
        {
            var h = new Harness(WeaponIds.FRAG);
            h.Begin(10, 100);
            ThrowableTransition release = h.Release(129);
            Assert.Equal((1, 0), h.Inventory);

            ThrowableLifecycle.RollbackRelease(ref h.State, in release, in h.Ammo);

            Assert.Equal((1, 1), h.Inventory);
            Assert.False(h.State.PendingRelease);
        }

        [Fact]
        public void ReleaseTickComparisonSurvivesUIntWraparound()
        {
            var h = new Harness(WeaponIds.AMMO_BAG);
            h.Begin(10, uint.MaxValue - 5);

            Assert.False(h.Release(3).Released);
            Assert.True(h.Release(4).Released);
        }

        [Fact]
        public void CooldownStartsAtReleaseAndUsesWholeSimulationTicks()
        {
            var h = new Harness(WeaponIds.FRAG);
            h.Begin(10, 100);
            h.Release(129);

            Assert.Equal(ThrowableRejection.OnCooldown, h.Begin(11, 167));
            Assert.Equal(ThrowableRejection.None, h.Begin(12, 168));
        }

        [Fact]
        public void NonDelayedHolsteredAndReloadingWeaponsAreRejectedWithoutMutation()
        {
            WeaponConfig rifle = WeaponCatalog.For(WeaponIds.RK44);
            WeaponRuntimeState rifleState = WeaponRuntimeState.Loaded(in rifle);
            Assert.Equal(
                ThrowableRejection.NotDelayed,
                ThrowableLifecycle.TryBegin(ref rifleState, in rifle, 1, 1, in Aim));

            var h = new Harness(WeaponIds.FRAG);
            h.State.Unholstered = false;
            Assert.Equal(ThrowableRejection.Holstered, h.Begin(1, 1));
            h.State.Unholstered = true;
            h.State.Reloading = true;
            Assert.Equal(ThrowableRejection.Reloading, h.Begin(2, 2));
            Assert.Equal((1, 1), h.Inventory);
        }

        private sealed class Harness
        {
            private const ushort Owner = 7;
            private const byte Slot = 2;
            private readonly ActorSpareAmmoPool _pool = new ActorSpareAmmoPool();
            private readonly WeaponConfig _config;

            public Harness(byte weaponId)
            {
                _config = WeaponCatalog.For(weaponId);
                State = WeaponRuntimeState.Loaded(in _config);
                _pool.SetLoadout(Owner, Slot, _config.SpareAmmo, _config.SpareAmmo, 0);
                Ammo = ActorAmmoSource.FromSlot(_pool, Owner, Slot);
            }

            public WeaponRuntimeState State;
            public readonly ActorAmmoSource Ammo;
            public int ReserveRounds => _pool.Remaining(Owner, Slot, in State);
            public SpareAmmo Reserve => Ammo.Reserve(in State, in _config);
            public (int loaded, int reserve) Inventory => (State.AmmoInClip, ReserveRounds);

            public ThrowableRejection Begin(uint inputTick, uint serverTick)
                => ThrowableLifecycle.TryBegin(
                    ref State, in _config, inputTick, serverTick, in Aim);

            public ThrowableTransition Release(uint serverTick)
                => ThrowableLifecycle.TryRelease(
                    ref State, in _config, serverTick, in Ammo);

            public void ThrowAt(uint serverTick)
            {
                Assert.Equal(ThrowableRejection.None, Begin(serverTick, serverTick));
                Assert.True(Release(serverTick + _config.ReleaseDelayTicks).Released);
            }
        }
    }
}
