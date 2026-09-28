using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Handoff section 5.3: the reload draws from <see cref="ActorSpareAmmoPool"/>, keyed by
    /// <c>(actorId, loadoutSlot)</c>, through one state machine.
    /// </summary>
    /// <remarks>
    /// <b>One state machine, including for the clip-of-one weapons.</b> A bazooka and a grenade
    /// are tested here beside an ordinary rifle rather than on a path of their own, because a
    /// second reload path is the one nobody writes the test for -- and those two are exactly the
    /// weapons the reported desync was seen on.
    /// </remarks>
    public sealed class ReloadReserveTests
    {
        private const ushort Shooter = TriggerFixture.Shooter;
        private const byte Slot = 2;
        private const float Reload = ProtocolConstants.RELOAD_SECONDS;

        [Fact]
        public void AnOrdinaryClipReloadMovesTheClipAndTheReserve()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 90);

            var fixture = Fixture(WeaponConfig.Rifle, pool);
            fixture.Weapon.AmmoInClip = 10;

            fixture.Step(0f, InputButtons.Reload);
            Assert.True(fixture.Weapon.Reloading);

            fixture.Step(Reload, InputButtons.None);

            Assert.False(fixture.Weapon.Reloading);
            Assert.Equal(30, fixture.Weapon.AmmoInClip);
            Assert.Equal(70, Remaining(pool));
        }

        [Fact]
        public void ABazookaGoesFromZeroOfFourToOneOfThreeExactlyOnce()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 4);

            var fixture = Fixture(ClipOfOne, pool);
            fixture.Weapon.AmmoInClip = 0;

            fixture.Step(0f, InputButtons.Reload);
            fixture.Step(Reload, InputButtons.None);

            Assert.Equal(1, fixture.Weapon.AmmoInClip);
            Assert.Equal(3, Remaining(pool));

            // Reload still held. The clip is full now, so nothing further may be drawn -- a
            // second grant here is the "one press, two rounds" the reported desync looked like.
            for (int i = 1; i <= 10; i++)
                fixture.Step(Reload + i * 0.5f, InputButtons.Reload);

            Assert.Equal(1, fixture.Weapon.AmmoInClip);
            Assert.Equal(3, Remaining(pool));
        }

        [Fact]
        public void AGrenadeGoesFromZeroOfTwoToOneOfOne()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 2);

            var fixture = Fixture(Thrown, pool);
            fixture.Weapon.AmmoInClip = 0;

            fixture.Step(0f, InputButtons.Reload);
            fixture.Step(Reload, InputButtons.None);

            Assert.Equal(1, fixture.Weapon.AmmoInClip);
            Assert.Equal(1, Remaining(pool));
        }

        [Fact]
        public void AReserveOfZeroCreatesNoRounds()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 0);

            var fixture = Fixture(ClipOfOne, pool);
            fixture.Weapon.AmmoInClip = 0;

            fixture.Step(0f, InputButtons.Reload);

            // Refused at BEGIN, not at completion: the snapshot carries Reloading from the
            // moment the server accepts, so an accepted reload against an empty pouch would
            // play a full animation on every client and hand back the same empty clip.
            Assert.False(fixture.Weapon.Reloading);

            fixture.Step(Reload, InputButtons.Reload);

            Assert.Equal(0, fixture.Weapon.AmmoInClip);
            Assert.Equal(0, Remaining(pool));
        }

        [Fact]
        public void AnInfiniteReserveRefillsTheClipAndKeepsTheSentinel()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, WeaponConfig.InfiniteSpareAmmo);

            var fixture = Fixture(WeaponConfig.Rifle, pool);
            fixture.Weapon.AmmoInClip = 4;

            fixture.Step(0f, InputButtons.Reload);
            fixture.Step(Reload, InputButtons.None);

            Assert.Equal(30, fixture.Weapon.AmmoInClip);

            // -1 out of the pool is INFINITE, which is the other -1 to the weapon model's
            // no-resupply. Decrementing the sentinel would turn it into a plain negative count.
            Assert.Equal(-1, Remaining(pool));
            Assert.Equal(SpareAmmoKind.Infinite, fixture.Ammo.Reserve(in fixture.Weapon, WeaponConfig.Rifle).Kind);
        }

        [Fact]
        public void DeathCancelsARunningReload()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 90);

            var fixture = Fixture(WeaponConfig.Rifle, pool);
            fixture.Weapon.AmmoInClip = 10;

            fixture.Step(0f, InputButtons.Reload);
            Assert.True(fixture.Weapon.Reloading);

            fixture.Actor = ActorFireEligibility.OnFoot(isAlive: false);
            fixture.Step(0.5f, InputButtons.None);

            Assert.False(fixture.Weapon.Reloading);

            // And it does not finish afterwards either: the clip a corpse was refilling must
            // not arrive on its own clock.
            fixture.Step(Reload + 1f, InputButtons.None);
            Assert.Equal(10, fixture.Weapon.AmmoInClip);
            Assert.Equal(90, Remaining(pool));
        }

        [Fact]
        public void TheDeathEdgeClearsTheReloadAndTheTriggerLatch()
        {
            // The cross-lane half of "death cancels a reload": ServerTickLoop calls this on the
            // death edge, because ResetWeapon does not run until the player deploys again and a
            // reload left running would complete under a corpse in between.
            var session = new ClientSession(connectionId: 4, actorId: Shooter);
            session.WeaponId = WeaponIds.RK44;
            session.ResetWeapon();

            session.Weapon.AmmoInClip = 5;
            session.Weapon.Reloading = true;
            session.Weapon.ReloadStartedAt = 0f;
            session.Trigger.WasEffective = true;
            session.Trigger.SprintFireBlockedUntil = 99f;

            session.ClearCombatStateOnDeath();

            Assert.False(session.Weapon.Reloading);
            Assert.Equal(float.NegativeInfinity, session.Weapon.ReloadStartedAt);
            Assert.False(session.Trigger.WasEffective);
            Assert.Equal(float.NegativeInfinity, session.Trigger.SprintFireBlockedUntil);

            // And the clip a life ended on is left where it was: ResetWeapon owns what the next
            // one starts with, and two writers of the ammo count is the divergence D9 removed.
            Assert.Equal(5, session.Weapon.AmmoInClip);
        }

        [Fact]
        public void AWeaponSwitchCancelsARunningReload()
        {
            var session = new ClientSession(connectionId: 4, actorId: Shooter);
            session.WeaponId = WeaponIds.RK44;
            session.ResetWeapon();

            session.Weapon.AmmoInClip = 5;
            session.Weapon.Reloading = true;
            session.Weapon.ReloadStartedAt = 0f;

            session.SwitchWeaponTo(WeaponIds.FRAG);

            Assert.False(session.Weapon.Reloading);

            // And coming back does not resume one that finished in the bag.
            session.SwitchWeaponTo(WeaponIds.RK44);
            Assert.False(session.Weapon.Reloading);
            Assert.Equal(5, session.Weapon.AmmoInClip);
        }

        [Fact]
        public void HoldingReloadDoesNotRestartTheTimer()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 90);

            var fixture = Fixture(WeaponConfig.Rifle, pool);
            fixture.Weapon.AmmoInClip = 10;

            // Held every tick for the whole duration. A restart per tick would push the
            // completion out forever and the clip would never refill.
            for (int tick = 0; tick <= (int)(Reload * ProtocolConstants.SIM_TICK_RATE); tick++)
                fixture.Step(tick / (float)ProtocolConstants.SIM_TICK_RATE, InputButtons.Reload);

            Assert.Equal(30, fixture.Weapon.AmmoInClip);
            Assert.Equal(1L, fixture.Authority.ReloadsStarted);
            Assert.Equal(1L, fixture.Authority.ReloadsCompleted);
        }

        [Fact]
        public void FiringDuringAReloadIsRefusedAndConsumesNothing()
        {
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, Slot, 90);

            var fixture = Fixture(WeaponConfig.Rifle, pool);
            fixture.Weapon.AmmoInClip = 10;

            fixture.Step(0f, InputButtons.Reload);

            CombatTickResult result = fixture.Step(0.5f, InputButtons.Fire);

            Assert.Equal(FireRejection.Reloading, result.Rejection);
            Assert.False(result.Fired);
            Assert.Equal(10, fixture.Weapon.AmmoInClip);

            // And the reload it interrupted is still running: a shot does not abort one, which
            // is what the client already does.
            Assert.True(fixture.Weapon.Reloading);
        }

        [Fact]
        public void AnUnknownLoadoutSlotRefusesTheReloadRatherThanSpendingSlotZero()
        {
            // Section 4.5's state-inconsistency case. Slot 0 is somebody's primary, so the
            // fallback that looks harmless drains a magazine the player is not holding.
            var pool = new ActorSpareAmmoPool();
            pool.Set(Shooter, 0, 90);

            var fixture = new TriggerFixture(
                WeaponConfig.Rifle, ActorAmmoSource.UnknownSlot(pool, Shooter));
            fixture.Weapon.AmmoInClip = 10;

            fixture.Step(0f, InputButtons.Reload);
            fixture.Step(Reload, InputButtons.Reload);

            Assert.False(fixture.Weapon.Reloading);
            Assert.Equal(10, fixture.Weapon.AmmoInClip);
            Assert.Equal(90, pool.Remaining(Shooter, 0, default));
            Assert.True(fixture.Authority.ReloadsRefusedForUnknownSlot > 0);
        }

        private static TriggerFixture Fixture(WeaponConfig config, ActorSpareAmmoPool pool)
            => new TriggerFixture(config, ActorAmmoSource.FromSlot(pool, Shooter, Slot));

        private static int Remaining(ActorSpareAmmoPool pool)
            => pool.Remaining(Shooter, Slot, default);

        /// <summary>A launcher: one round in the tube, one press per shot.</summary>
        private static WeaponConfig ClipOfOne => new WeaponConfig(
            cooldown: 0.05f, spread: 0f, projectilesPerShot: 1, range: 300f,
            damage: 0f, force: 0f, clipSize: 1,
            delivery: WeaponDelivery.Projectile, automatic: false);

        /// <summary>A grenade. Same shape as the launcher, which is the point.</summary>
        private static WeaponConfig Thrown => new WeaponConfig(
            cooldown: 1.3f, spread: 0.01f, projectilesPerShot: 1, range: 40f,
            damage: 0f, force: 0f, clipSize: 1,
            delivery: WeaponDelivery.Projectile, automatic: false);
    }
}
