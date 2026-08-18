using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V2 task 5. The catalog, the drop-off ramp, the stagger number, and the two traps
    /// that would silently undo the phase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This file is the gate that replaces a SpecChecker entry</b> (D10). The weapon ID is a
    /// wire contract and SpecChecker owns it; the weapon NUMBERS are not on the wire, and putting
    /// a balance tweak behind two protocol approvals is how balance work stops happening. A unit
    /// test fails the build just as hard and costs nobody a review round.
    /// </para>
    /// </remarks>
    public sealed class WeaponCatalogTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;

        private static readonly Vec3 Muzzle = new Vec3(0f, 1.5f, 0f);
        private static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);

        // ------------------------------------------------------------------ the table itself

        [Fact]
        public void EveryAssignedWeaponIdHasACatalogEntry()
        {
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                WeaponConfig config = WeaponCatalog.For(id);

                // "Has an entry" cannot be "is not Inert" — six of the seventeen ids are
                // legitimately inert (D4). Cooldown-or-clip is what separates an authored inert
                // entry from a hole in the array, since Inert has neither and every real entry
                // has at least one.
                bool isDeliberatelyInert =
                    id == WeaponIds.BINOCS || id == WeaponIds.AMMO_BAG ||
                    id == WeaponIds.MEDIPACK || id == WeaponIds.NV_GOGGLES ||
                    id == WeaponIds.WRENCH || id == WeaponIds.SUPER_WRENCH;

                if (isDeliberatelyInert)
                {
                    Assert.Equal(0f, config.Damage);
                    continue;
                }

                Assert.True(
                    config.Cooldown > 0f || config.ClipSize > 0,
                    "weapon id " + id + " (" + WeaponIds.NameOf(id)
                    + ") has no catalog entry — add one to WeaponCatalog.BuildConfigs");
            }
        }

        [Fact]
        public void AnUnknownWeaponIdResolvesToInertAndNotToRifle()
        {
            // The single most important assertion in this phase: a fallback to Rifle would
            // silently undo it, turning a medipack back into a gun.
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.NONE).Damage);
            Assert.Equal(0f, WeaponCatalog.For(200).Damage);
            Assert.Equal(0f, WeaponCatalog.For((byte)(WeaponIds.MAX_ASSIGNED + 1)).Damage);

            Assert.Equal(0, WeaponCatalog.For(WeaponIds.NONE).ClipSize);
            Assert.NotEqual(WeaponConfig.Rifle.Damage, WeaponCatalog.For(WeaponIds.NONE).Damage);
        }

        [Fact]
        public void ThingsThatAreNotGunsDoNoDamage()
        {
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.MEDIPACK).Damage);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.AMMO_BAG).Damage);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.BINOCS).Damage);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.NV_GOGGLES).Damage);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.WRENCH).Damage);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.SUPER_WRENCH).Damage);

            // Thrown ordnance carries a real throw rate and a real count carried, and zero
            // hitscan damage — V7 owns the projectile.
            Assert.True(WeaponCatalog.For(WeaponIds.FRAG).Cooldown > 0f);
            Assert.True(WeaponCatalog.For(WeaponIds.FRAG).ClipSize > 0);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.FRAG).Damage);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.SPEARHEAD).Damage);
        }

        [Fact]
        public void OnlyTheMeleeWeaponsRemainUnauthored()
        {
            // The inversion V2's D3 asked for, performed in the commit that filled the numbers.
            // Fifteen entries now carry values from the weapon assets. The two that do not are
            // the melee weapons, and they are excluded for a modelling reason rather than a
            // missing-data one: WeaponConfig describes a hitscan shot and a wrench swing is not
            // one. If a later phase teaches this table about melee, this assertion is the thing
            // that must change with it.
            Assert.Equal(WeaponIds.MAX_ASSIGNED - 2, WeaponCatalog.AuthoredCount);
            Assert.Equal(2, WeaponCatalog.PlaceholderCount);

            Assert.True(WeaponCatalog.IsAuthored(WeaponIds.RK44));
            Assert.True(WeaponCatalog.IsAuthored(WeaponIds.RECON_LRR));
            Assert.False(WeaponCatalog.IsAuthored(WeaponIds.WRENCH));
            Assert.False(WeaponCatalog.IsAuthored(WeaponIds.SUPER_WRENCH));

            string warning = WeaponCatalog.DescribeUnauthored();
            Assert.Contains("PLACEHOLDER", warning);
            Assert.Contains(WeaponIds.NameOf(WeaponIds.WRENCH), warning);
            Assert.DoesNotContain(WeaponIds.NameOf(WeaponIds.RECON_LRR), warning);
        }

        [Fact]
        public void TheWeaponsWhoseCLASSWasGuessedWrongAreNowRight()
        {
            // Every id below was catalogued as the wrong KIND of weapon, not merely with loose
            // numbers, and each passed every other test in this file while doing so. Pinning the
            // distinguishing property of each is what would catch a regression to a class guess.

            // An SMAW rocket launcher, once an 8-pellet shotgun. One shell, and the payload is
            // the rocket's, so hitscan damage is zero rather than 8 x 12.
            Assert.Equal(1, WeaponCatalog.For(WeaponIds.BEU_AW1).ClipSize);
            Assert.Equal(1, WeaponCatalog.For(WeaponIds.BEU_AW1).ProjectilesPerShot);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.BEU_AW1).Damage);

            // A Javelin guided missile, once a marksman rifle doing 40 a shot.
            Assert.Equal(1, WeaponCatalog.For(WeaponIds.BIL_SCALPEL).ClipSize);
            Assert.Equal(0f, WeaponCatalog.For(WeaponIds.BIL_SCALPEL).Damage);

            // A sniper, once an automatic. The cadence is the tell: 1.5 s, not 0.1 s.
            Assert.True(WeaponCatalog.For(WeaponIds.SL_DEFENDER).Cooldown >= 1f);
            Assert.True(WeaponCatalog.For(WeaponIds.SL_DEFENDER).Damage >= 60f);

            // A 20-pellet shotgun, once a single-projectile marksman rifle.
            Assert.Equal(20, WeaponCatalog.For(WeaponIds.EAGLE_76).ProjectilesPerShot);
            Assert.True(WeaponCatalog.For(WeaponIds.EAGLE_76).Range <= 100f);

            // No two ids share an identical config any more. The bug this phase exists to close
            // was seventeen ids resolving to one gun; four still shared the "automatic" literal
            // after V2, which is the same defect one order smaller.
            for (byte a = 1; a <= WeaponIds.MAX_ASSIGNED; a++)
            {
                if (!WeaponCatalog.IsAuthored(a) || WeaponCatalog.For(a).Damage == 0f) continue;

                for (byte b = (byte)(a + 1); b <= WeaponIds.MAX_ASSIGNED; b++)
                {
                    if (!WeaponCatalog.IsAuthored(b) || WeaponCatalog.For(b).Damage == 0f) continue;

                    WeaponConfig left = WeaponCatalog.For(a);
                    WeaponConfig right = WeaponCatalog.For(b);
                    bool identical =
                        left.Damage == right.Damage &&
                        left.Cooldown == right.Cooldown &&
                        left.ClipSize == right.ClipSize &&
                        left.Range == right.Range &&
                        left.DropoffEndMetres == right.DropoffEndMetres;

                    Assert.False(identical,
                        WeaponIds.NameOf(a) + " and " + WeaponIds.NameOf(b) + " are the same gun");
                }
            }
        }

        // ------------------------------------------------------------------ the drop-off ramp

        [Fact]
        public void DamageFallsOffWithDistance()
        {
            WeaponConfig smg = WeaponCatalog.For(WeaponIds.RK44);

            Assert.Equal(1f, WeaponConfig.DropoffMultiplier(in smg, smg.DropoffStartMetres), 4);
            Assert.Equal(
                smg.DropoffMinMultiplier,
                WeaponConfig.DropoffMultiplier(in smg, smg.DropoffEndMetres), 4);

            float previous = float.MaxValue;
            for (float d = 0f; d <= smg.DropoffEndMetres; d += 5f)
            {
                float multiplier = WeaponConfig.DropoffMultiplier(in smg, d);
                Assert.True(multiplier <= previous + 1e-5f, "drop-off must be monotonic at " + d);
                previous = multiplier;
            }
        }

        [Fact]
        public void DropoffNeverExceedsOneOrDropsBelowTheFloor()
        {
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                WeaponConfig config = WeaponCatalog.For(id);

                foreach (float distance in new[] { 0f, 1f, 50f, 250f, 1000f, 10000f })
                {
                    float multiplier = WeaponConfig.DropoffMultiplier(in config, distance);

                    Assert.True(multiplier <= 1f, "id " + id + " exceeded 1 at " + distance);
                    Assert.True(
                        multiplier >= config.DropoffMinMultiplier,
                        "id " + id + " fell below its floor at " + distance);
                }
            }
        }

        [Fact]
        public void AnInvertedDropoffRangeDoesNotProduceNaN()
        {
            // A NaN multiplier makes every subsequent damage comparison false, so the shot lands
            // and does nothing and nothing anywhere reports it. Same shape as a NaN sentinel.
            var inverted = new WeaponConfig(
                cooldown: 0.1f, spread: 0f, projectilesPerShot: 1, range: 300f,
                damage: 25f, force: 200f, clipSize: 30,
                balanceDamage: 10f,
                dropoffStartMetres: 200f, dropoffEndMetres: 50f, dropoffMinMultiplier: 0.25f);

            foreach (float distance in new[] { 0f, 49f, 50f, 100f, 200f, 201f, 10000f })
            {
                float multiplier = WeaponConfig.DropoffMultiplier(in inverted, distance);

                Assert.False(float.IsNaN(multiplier), "NaN at " + distance);
                Assert.False(float.IsInfinity(multiplier), "infinite at " + distance);
                Assert.InRange(multiplier, 0.25f, 1f);
            }

            Assert.Equal(1f, WeaponConfig.DropoffMultiplier(in inverted, 100f), 4);
            Assert.Equal(0.25f, WeaponConfig.DropoffMultiplier(in inverted, 250f), 4);
        }

        [Fact]
        public void AMistypedDropoffFloorCannotBecomeADamageBonus()
        {
            var mistyped = new WeaponConfig(
                cooldown: 0.1f, spread: 0f, projectilesPerShot: 1, range: 300f,
                damage: 25f, force: 200f, clipSize: 30,
                dropoffMinMultiplier: 10f);

            Assert.Equal(1f, mistyped.DropoffMinMultiplier, 4);
            Assert.Equal(1f, WeaponConfig.DropoffMultiplier(in mistyped, 10000f), 4);
        }

        [Fact]
        public void HeadshotAndDropoffCommute()
        {
            // D8. Multiplication is commutative, so "headshot then drop-off" and "drop-off then
            // headshot" are the same number. Pinned so it is not argued about again.
            WeaponConfig dmr = WeaponCatalog.For(WeaponIds.SIGNAL_DMR);

            const float distance = 420f;

            float headshotThenDropoff =
                dmr.Damage * ServerFireResolver.HitboxMultiplier(HitboxType.Head)
                * WeaponConfig.DropoffMultiplier(in dmr, distance);

            float dropoffThenHeadshot =
                dmr.Damage * WeaponConfig.DropoffMultiplier(in dmr, distance)
                * ServerFireResolver.HitboxMultiplier(HitboxType.Head);

            Assert.Equal(
                headshotThenDropoff,
                ServerFireResolver.DamageFor(in dmr, HitboxType.Head, distance), 4);
            Assert.Equal(headshotThenDropoff, dropoffThenHeadshot, 4);
        }

        [Fact]
        public void ARifleKeepsItsPreEexistingNumbersAtPointBlank()
        {
            // Every phase-05 combat test measures against Rifle. Its original seven numbers are
            // unchanged and the ramp is identity at zero distance, so those tests still measure
            // the same weapon.
            Assert.Equal(100f, ServerFireResolver.DamageFor(WeaponConfig.Rifle, HitboxType.Head, 0f), 3);
            Assert.Equal(25f, ServerFireResolver.DamageFor(WeaponConfig.Rifle, HitboxType.Body, 0f), 3);
        }

        // ------------------------------------------------------------------ criterion 8

        [Fact]
        public void ANonRifleBehavesDifferentlyFromARifleOnTheServer()
        {
            // The sniper is SL_DEFENDER (sniper.prefab, 80 damage over a 1.5 s cycle), not
            // RECON_LRR — that one is RFB.prefab, a 0.1 s marksman rifle whose ramp starts at
            // 36 m. This test used to name RECON_LRR and passed, because the placeholder numbers
            // made it a bolt-action doing 95. Against the real assets that comparison inverts:
            // RECON_LRR's ramp starts EARLIER than the rifle's, so the gap narrows with range
            // rather than widening, and the assertion below is what catches it.
            WeaponConfig smg = WeaponCatalog.For(WeaponIds.RK44);
            WeaponConfig sniper = WeaponCatalog.For(WeaponIds.SL_DEFENDER);

            float smgClose = ServerFireResolver.DamageFor(in smg, HitboxType.Body, 10f);
            float sniperClose = ServerFireResolver.DamageFor(in sniper, HitboxType.Body, 10f);

            float smgFar = ServerFireResolver.DamageFor(in smg, HitboxType.Body, 250f);
            float sniperFar = ServerFireResolver.DamageFor(in sniper, HitboxType.Body, 250f);

            Assert.NotEqual(smgClose, sniperClose);

            // And the gap widens with range, which is the half the drop-off ramp exists for. A
            // catalog with per-weapon damage but no drop-off would pass the line above and fail
            // this one.
            Assert.True(
                sniperFar - smgFar > sniperClose - smgClose,
                "a sniper must out-range an SMG by more at 250 m than at 10 m: "
                + (sniperFar - smgFar) + " vs " + (sniperClose - smgClose));
        }

        [Fact]
        public void AShotgunFiresMoreProjectilesThanARifle()
        {
            // The shotgun is EAGLE_76 (shotgun.prefab, a ShellLoadedWeapon firing 20 pellets),
            // not BEU_AW1 — that one is smaw.prefab, a rocket launcher with a single shell whose
            // damage belongs to the rocket. This test used to name BEU_AW1 and passed, because
            // the placeholder had invented an 8-pellet spread for it.
            WeaponConfig shotgun = WeaponCatalog.For(WeaponIds.EAGLE_76);
            WeaponConfig rifle = WeaponCatalog.For(WeaponIds.RK44);

            Assert.True(shotgun.ProjectilesPerShot > rifle.ProjectilesPerShot);

            var fixture = new CatalogFireFixture(shotgun);
            Assert.Equal(FireRejection.None, fixture.Fire(nowSeconds: 10f));

            // The pellet count reaches Resolve's loop rather than sitting in the struct unread.
            Assert.True(fixture.HitCount > 1, "expected multiple pellets, got " + fixture.HitCount);
        }

        // ------------------------------------------------------------------ stagger

        [Fact]
        public void BalanceDamageReachesTheSink()
        {
            WeaponConfig smg = WeaponCatalog.For(WeaponIds.RK44);

            float close = ServerFireResolver.BalanceDamageFor(in smg, 10f);
            float far = ServerFireResolver.BalanceDamageFor(in smg, 300f);

            Assert.Equal(smg.BalanceDamage, close, 4);
            Assert.True(far < close, "stagger must fall off with distance the way damage does");
            Assert.Equal(smg.BalanceDamage * smg.DropoffMinMultiplier, far, 4);

            // And a thing that is not a gun staggers nobody.
            Assert.Equal(
                0f, ServerFireResolver.BalanceDamageFor(WeaponCatalog.For(WeaponIds.MEDIPACK), 5f), 4);
        }

        // ------------------------------------------------------------------ the ordering trap

        [Fact]
        public void ASpawnAssignsTheWeaponIdBeforeLoadingTheClip()
        {
            // The trap Task 3 creates: WeaponRuntimeState.Loaded copies ClipSize out of the
            // config, the config is now derived from WeaponId, so ResetWeapon() before the id is
            // assigned loads a clip of ZERO — and the symptom is FireRejection.NoAmmo forever,
            // which looks exactly like the ammo bug phase-05 closed. This is the only thing
            // standing between the design and that bug.
            var wrong = new ClientSession(connectionId: 1, actorId: 1);
            wrong.ResetWeapon();
            wrong.WeaponId = WeaponIds.RK44;

            Assert.Equal(0, wrong.Weapon.AmmoInClip);
            Assert.Equal(
                FireRejection.NoAmmo,
                ServerFireResolver.CheckCanFire(
                    in wrong.Weapon, wrong.WeaponConfig, shooterIsAlive: true, nowSeconds: 100f));

            var right = new ClientSession(connectionId: 2, actorId: 2);
            right.WeaponId = WeaponIds.RK44;
            right.ResetWeapon();

            Assert.Equal(WeaponCatalog.For(WeaponIds.RK44).ClipSize, right.Weapon.AmmoInClip);
            Assert.Equal(
                FireRejection.None,
                ServerFireResolver.CheckCanFire(
                    in right.Weapon, right.WeaponConfig, shooterIsAlive: true, nowSeconds: 100f));
        }

        [Fact]
        public void ASessionsWeaponConfigIsDerivedFromItsWeaponId()
        {
            // Criterion 7: one weapon-numbers source on the server. Changing the id changes the
            // numbers, with nothing to keep in sync and nothing that can diverge.
            var session = new ClientSession(connectionId: 1, actorId: 1);

            session.WeaponId = WeaponIds.RECON_LRR;
            Assert.Equal(WeaponCatalog.For(WeaponIds.RECON_LRR).Damage, session.WeaponConfig.Damage);

            session.WeaponId = WeaponIds.BEU_AW1;
            Assert.Equal(
                WeaponCatalog.For(WeaponIds.BEU_AW1).ProjectilesPerShot,
                session.WeaponConfig.ProjectilesPerShot);

            session.WeaponId = WeaponIds.NONE;
            Assert.Equal(0f, session.WeaponConfig.Damage);
        }

        /// <summary>A shooter and one standing target 10 m downrange.</summary>
        private sealed class CatalogFireFixture
        {
            private readonly ServerFireResolver _resolver;
            private readonly WeaponConfig _config;
            private readonly HitscanTarget[] _targets;
            private readonly HitResult[] _hits;

            private WeaponRuntimeState _state;

            public CatalogFireFixture(in WeaponConfig config)
            {
                _config = config;
                _resolver = new ServerFireResolver(
                    new LagCompensator(new HitboxHistory()), seed: 2026);
                _state = WeaponRuntimeState.Loaded(in config);
                _hits = new HitResult[Math.Max(config.ProjectilesPerShot, 1)];

                _targets = new[]
                {
                    new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))),
                };
            }

            public int HitCount { get; private set; }

            public FireRejection Fire(float nowSeconds)
            {
                FireRejection rejection = _resolver.Resolve(
                    ref _state, in _config, _targets, Shooter, shooterIsAlive: true,
                    Muzzle, Forward, nowSeconds, smoothedRttMs: 0f, currentTick: 10,
                    _hits, out int hitCount);

                HitCount = hitCount;
                return rejection;
            }
        }
    }
}
