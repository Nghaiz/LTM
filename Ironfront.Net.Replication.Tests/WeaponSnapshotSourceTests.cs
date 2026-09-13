using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Handoff sections 2.2 and 4.5: the authoritative reserve and the reload flag reaching the
    /// five-byte weapon field.
    /// </summary>
    /// <remarks>
    /// Until protocol 10 the weapon field was <c>weaponId + ammoInClip</c>, so a client held an
    /// authoritative clip beside a reserve of its own that nothing ever corrected. Two sources
    /// for one number, and the clip-of-one weapons are where they visibly disagreed.
    /// </remarks>
    public sealed class WeaponSnapshotSourceTests
    {
        private const ushort Shooter = 11;
        private const byte Slot = 2;

        [Fact]
        public void TheReserveComesFromThePoolKeyedByActorAndSlot()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 42);
            pool.Set(Shooter, 0, 7);

            WeaponSnapshotFields fields = Resolve(
                WeaponConfig.Rifle, ActorAmmoSource.FromSlot(pool, Shooter, Slot));

            Assert.Equal(SpareAmmoKind.Finite, fields.Reserve.Kind);
            Assert.Equal(42, fields.Reserve.Rounds);
        }

        [Fact]
        public void AWeaponWithNoReserveReportsNoResupplyRatherThanZero()
        {
            // A pool answers 0 both for a weapon that has spent its reserve and for one that
            // never had one. Only the config can tell those apart, and on a HUD they are the
            // difference between "find an ammo bag" and "this never refills".
            var pool = new ActorSpareAmmoPool();

            WeaponSnapshotFields fields = Resolve(
                NoResupplyWeapon, ActorAmmoSource.FromSlot(pool, Shooter, Slot));

            Assert.Equal(SpareAmmoKind.NoResupply, fields.Reserve.Kind);
            Assert.Equal(SpareAmmo.NoResupplyEncoded, fields.SpareAmmoEncoded);
        }

        [Fact]
        public void AnInfiniteReserveSurvivesTheRoundTrip()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, WeaponConfig.InfiniteSpareAmmo);

            WeaponSnapshotFields fields = Resolve(
                WeaponConfig.Rifle, ActorAmmoSource.FromSlot(pool, Shooter, Slot));

            Assert.Equal(SpareAmmo.InfiniteEncoded, fields.SpareAmmoEncoded);
            Assert.Equal(SpareAmmoKind.Infinite, SpareAmmo.Decode(fields.SpareAmmoEncoded).Kind);
        }

        [Fact]
        public void AnUnknownLoadoutSlotReportsNoResupply()
        {
            // Section 4.5: not slot 0's reserve, and not a zero that reads as an empty pouch.
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, 0, 90);

            WeaponSnapshotFields fields = Resolve(
                WeaponConfig.Rifle, ActorAmmoSource.UnknownSlot(pool, Shooter));

            Assert.Equal(SpareAmmoKind.NoResupply, fields.Reserve.Kind);
        }

        [Fact]
        public void TheReloadingFlagIsSetWhileAReloadIsRunningAndClearOnCompletion()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 90);

            var source = ActorAmmoSource.FromSlot(pool, Shooter, Slot);
            WeaponRuntimeState weapon = WeaponRuntimeState.Loaded(WeaponConfig.Rifle);
            weapon.AmmoInClip = 10;

            SpareAmmo reserve = source.Reserve(in weapon, WeaponConfig.Rifle);
            ServerReloadPolicy.BeginReload(
                ref weapon, WeaponConfig.Rifle, shooterIsAlive: true, nowSeconds: 0f,
                in source, in reserve);

            Assert.Equal(
                WeaponStateFlags.Reloading,
                SnapshotBuilder.ResolveWeaponFields(in weapon, WeaponConfig.Rifle, in source).StateFlags);

            ServerReloadPolicy.CompleteReloadIfElapsed(
                ref weapon, WeaponConfig.Rifle, ProtocolConstants.RELOAD_SECONDS, in source);

            WeaponSnapshotFields done =
                SnapshotBuilder.ResolveWeaponFields(in weapon, WeaponConfig.Rifle, in source);

            // Cleared, and carrying the NEW clip and the NEW reserve in the same entry -- the
            // four weapon parts are masked and applied together, so a flag from one tick beside
            // a clip from another is not a state the client can ever see.
            Assert.Equal(WeaponStateFlags.None, done.StateFlags);
            Assert.Equal(30, done.AmmoInClip);
            Assert.Equal(70, done.Reserve.Rounds);
        }

        [Fact]
        public void CaptureCarriesTheResolvedFieldsOntoTheEntry()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 12);

            WeaponRuntimeState weapon = WeaponRuntimeState.Loaded(WeaponConfig.Rifle);
            weapon.Reloading = true;

            WeaponSnapshotFields fields = SnapshotBuilder.ResolveWeaponFields(
                in weapon, WeaponConfig.Rifle, ActorAmmoSource.FromSlot(pool, Shooter, Slot));

            ActorSnapshotEntry entry = SnapshotBuilder.Capture(
                Shooter, new Vec3(1f, 2f, 3f), 0f, 0f, Vec3.Zero,
                ActorStateFlags.IsAlive, 100f, WeaponIds.RK44, fields.AmmoInClip, team: 1,
                vehicleId: 0, seatIndex: 0,
                spareAmmoEncoded: fields.SpareAmmoEncoded,
                weaponStateFlags: fields.StateFlags);

            Assert.Equal(12, entry.SpareAmmoEncoded);
            Assert.Equal(WeaponStateFlags.Reloading, entry.WeaponStateFlags);
            Assert.Equal(30, entry.AmmoInClip);
        }

        [Fact]
        public void ACaptureThatWasNotToldTheReserveSaysNoResupplyRatherThanZero()
        {
            // The default matters: fourteen call sites capture actors without a weapon story,
            // and a zero there would show every one of them as an empty pouch.
            ActorSnapshotEntry entry = SnapshotBuilder.Capture(
                Shooter, Vec3.Zero, 0f, 0f, Vec3.Zero,
                ActorStateFlags.IsAlive, 100f, WeaponIds.RK44, ammoInClip: 30, team: 1);

            Assert.Equal(SpareAmmo.NoResupplyEncoded, entry.SpareAmmoEncoded);
            Assert.Equal(WeaponStateFlags.None, entry.WeaponStateFlags);
        }

        [Fact]
        public void TheSessionRefusesToNameASlotItCannotHold()
        {
            var session = new ClientSession(connectionId: 4, actorId: Shooter);

            Assert.False(session.HasActiveLoadoutSlot);

            Assert.True(session.SetActiveLoadoutSlot(3));
            Assert.True(session.HasActiveLoadoutSlot);
            Assert.Equal(3, session.ActiveLoadoutSlot);

            // Past the five the loadout has: the slot is forgotten rather than clamped, because
            // a clamped slot is a guess wearing a valid index.
            Assert.False(session.SetActiveLoadoutSlot(9));
            Assert.False(session.HasActiveLoadoutSlot);
        }

        private static WeaponSnapshotFields Resolve(WeaponConfig config, ActorAmmoSource ammo)
        {
            WeaponRuntimeState weapon = WeaponRuntimeState.Loaded(in config);
            return SnapshotBuilder.ResolveWeaponFields(in weapon, in config, in ammo);
        }

        /// <summary>The horn's shape: a clip that never refills and a reserve that never exists.</summary>
        private static WeaponConfig NoResupplyWeapon => new WeaponConfig(
            cooldown: 0.2f, spread: 0f, projectilesPerShot: 1, range: 0f,
            damage: 0f, force: 0f, clipSize: 1,
            spareAmmo: WeaponConfig.NoResupplySpareAmmo, spendsAmmo: false);
    }
}
