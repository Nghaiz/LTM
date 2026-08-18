using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-02 task 4, criteria 6 (rapid fire blocked) and 9 (headshots deal 4x).
    /// </summary>
    public sealed class ServerFireResolutionTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;

        private static readonly Vec3 Muzzle = new Vec3(0f, 1.5f, 0f);
        private static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);

        // ------------------------------------------------------------------ criterion 9

        [Fact]
        public void AHeadshotDealsFourTimesBodyDamage()
        {
            WeaponConfig rifle = WeaponConfig.Rifle;

            Assert.Equal(4.0f, ServerFireResolver.HitboxMultiplier(HitboxType.Head), 4);
            Assert.Equal(1.0f, ServerFireResolver.HitboxMultiplier(HitboxType.Body), 4);
            Assert.Equal(0.75f, ServerFireResolver.HitboxMultiplier(HitboxType.Limb), 4);

            Assert.Equal(
                4f * ServerFireResolver.DamageFor(in rifle, HitboxType.Body, 0f),
                ServerFireResolver.DamageFor(in rifle, HitboxType.Head, 0f), 4);
        }

        [Fact]
        public void AHeadshotKillsAFullHealthTargetWithOneRifleRound()
        {
            // 25 damage x 4 = 100. The number the criterion is really about.
            Assert.Equal(100f, ServerFireResolver.DamageFor(WeaponConfig.Rifle, HitboxType.Head, 0f), 3);
        }

        [Fact]
        public void HitboxDamageSurvivesTheWireRoundTrip()
        {
            // S_HIT_CONFIRM carries damage as x10 fixed point, so a limb hit's 18.75 has to
            // still be 18.75 when the client draws the number.
            float damage = ServerFireResolver.DamageFor(WeaponConfig.Rifle, HitboxType.Limb, 0f);
            ushort packed = HitConfirmMessage.PackDamage(damage);

            var message = new HitConfirmMessage(Target, packed, HitboxType.Limb, HitFlags.None);

            Assert.Equal(18.75f, damage, 3);
            Assert.Equal(18.8f, message.Damage, 2);
        }

        // ------------------------------------------------------------------ criterion 6

        [Fact]
        public void FiringFasterThanTheCooldownIsRejected()
        {
            // architecture.md section 9: rapid fire is one of the cheats server authority is
            // supposed to close for free — but only if the cooldown being checked is the
            // server's number, not one the client sent.
            var fixture = new FireFixture();
            WeaponConfig rifle = WeaponConfig.Rifle;

            Assert.Equal(FireRejection.None, fixture.Fire(nowSeconds: 10.0f));
            Assert.Equal(FireRejection.OnCooldown, fixture.Fire(nowSeconds: 10.0f + rifle.Cooldown * 0.5f));

            Assert.Equal(1, fixture.Resolver.FireRateViolations);
            Assert.Equal(1, fixture.Resolver.ShotsFired);
        }

        [Fact]
        public void ARejectedShotConsumesNoAmmo()
        {
            var fixture = new FireFixture();
            fixture.Fire(nowSeconds: 10.0f);
            byte afterFirst = fixture.State.AmmoInClip;

            fixture.Fire(nowSeconds: 10.01f);

            Assert.Equal(afterFirst, fixture.State.AmmoInClip);
        }

        [Fact]
        public void AShotExactlyOnTheCooldownBoundaryIsLegal()
        {
            // Rejecting the boundary penalises an honest client whose timing is perfect.
            var fixture = new FireFixture();
            fixture.Fire(nowSeconds: 10.0f);

            Assert.Equal(FireRejection.None, fixture.Fire(nowSeconds: 10.0f + WeaponConfig.Rifle.Cooldown));
            Assert.Equal(0, fixture.Resolver.FireRateViolations);
        }

        [Fact]
        public void ASustainedRapidFireAttackLandsOnlyTheLegalShots()
        {
            // A modified client emptying its intent buffer as fast as the network allows.
            var fixture = new FireFixture();

            for (int i = 0; i < 100; i++) fixture.Fire(nowSeconds: 10f + i * 0.01f);

            // 100 attempts over 1 s against a 0.1 s cooldown: 10 legal, 90 rejected.
            Assert.Equal(10, fixture.Resolver.ShotsFired);
            Assert.Equal(90, fixture.Resolver.FireRateViolations);
        }

        [Fact]
        public void AnEmptyClipCannotFire()
        {
            var fixture = new FireFixture();
            fixture.State.AmmoInClip = 0;

            Assert.Equal(FireRejection.NoAmmo, fixture.Fire(nowSeconds: 10f));
            Assert.Equal(0, fixture.Resolver.ShotsFired);
        }

        [Fact]
        public void AReloadingWeaponCannotFire()
        {
            var fixture = new FireFixture();
            fixture.State.Reloading = true;

            Assert.Equal(FireRejection.Reloading, fixture.Fire(nowSeconds: 10f));
        }

        [Fact]
        public void AHolsteredWeaponCannotFire()
        {
            var fixture = new FireFixture();
            fixture.State.Unholstered = false;

            Assert.Equal(FireRejection.Holstered, fixture.Fire(nowSeconds: 10f));
        }

        [Fact]
        public void ADeadShooterCannotFire()
        {
            // Input already in the ring when the shooter died would otherwise fire from a corpse.
            var fixture = new FireFixture();

            Assert.Equal(FireRejection.ShooterDead, fixture.Fire(nowSeconds: 10f, shooterIsAlive: false));
        }

        [Fact]
        public void TheChecksRunBeforeAnyRaycast()
        {
            // The order is the security property: a rapid-fire attack should cost a handful of
            // comparisons, not a full lag-compensated sweep per rejected intent.
            var fixture = new FireFixture();
            fixture.Fire(nowSeconds: 10f);
            long sweepsAfterLegalShot = fixture.Compensator.ShotsResolved;

            for (int i = 0; i < 50; i++) fixture.Fire(nowSeconds: 10f + i * 0.0001f);

            Assert.Equal(sweepsAfterLegalShot, fixture.Compensator.ShotsResolved);
        }

        // ------------------------------------------------------------------ resolution

        [Fact]
        public void AnAcceptedShotConsumesExactlyOneRound()
        {
            var fixture = new FireFixture();
            byte before = fixture.State.AmmoInClip;

            fixture.Fire(nowSeconds: 10f);

            Assert.Equal(before - 1, fixture.State.AmmoInClip);
        }

        [Fact]
        public void AHitIsReportedWithItsTargetAndHitbox()
        {
            var fixture = new FireFixture();
            fixture.Fire(nowSeconds: 10f);

            Assert.Equal(1, fixture.HitCount);
            Assert.True(fixture.Hits[0].Hit);
            Assert.Equal(Target, fixture.Hits[0].TargetActorId);
        }

        [Fact]
        public void AShotgunResolvesEveryPelletSeparately()
        {
            var shotgun = new WeaponConfig(
                cooldown: 0.8f, spread: 0.05f, projectilesPerShot: 8, range: 50f,
                damage: 12f, force: 300f, clipSize: 6);

            var fixture = new FireFixture(shotgun);
            fixture.Fire(nowSeconds: 10f);

            Assert.Equal(8, fixture.Compensator.ShotsResolved);
            Assert.True(fixture.HitCount > 1, "a shotgun at 10 m should land more than one pellet");
        }

        [Fact]
        public void SpreadStaysInsideItsCone()
        {
            // The target is sized to the cone, which is the entire point. An earlier version of
            // this used a 40 m wall at 20 m range: a pellet needed to deviate more than 45
            // degrees to miss it, against a cone half-angle of 5.7 degrees, so it only failed if
            // spread was wrong by a factor of about ten.
            //
            // spread 0.1 offsets the unit aim by at most 0.1 before renormalising, so the
            // maximum deviation is asin(0.1) = 5.74 degrees. At 20 m that is 2.01 m of lateral
            // travel, so a panel with a 2.5 m half-width must catch every pellet.
            const float spread = 0.1f;
            const float range = 20f;
            float maxLateral = MathF.Tan(MathF.Asin(spread)) * range;

            var config = new WeaponConfig(
                cooldown: 0f, spread: spread, projectilesPerShot: 200, range: 300f,
                damage: 1f, force: 0f, clipSize: 255);

            Assert.Equal(200, FirePelletsAtPanel(in config, range, halfWidth: maxLateral + 0.5f));
        }

        [Fact]
        public void SpreadActuallyReachesTheEdgeOfItsCone()
        {
            // The other half: a cone that is real must also MISS a panel narrower than itself.
            // Together these bracket the spread rather than only bounding it from one side.
            const float spread = 0.1f;
            const float range = 20f;
            float maxLateral = MathF.Tan(MathF.Asin(spread)) * range;

            var config = new WeaponConfig(
                cooldown: 0f, spread: spread, projectilesPerShot: 200, range: 300f,
                damage: 1f, force: 0f, clipSize: 255);

            int hits = FirePelletsAtPanel(in config, range, halfWidth: maxLateral * 0.25f);

            Assert.True(hits > 0, "every pellet missed a panel a quarter of the cone wide");
            Assert.True(hits < 200,
                $"all 200 pellets fitted inside a quarter of the cone — spread is not reaching "
                + $"its stated {spread} offset");
        }

        /// <summary>Fires one full shot at a flat panel and counts the pellets that land.</summary>
        private static int FirePelletsAtPanel(in WeaponConfig config, float range, float halfWidth)
        {
            var compensator = new LagCompensator(new HitboxHistory());
            var resolver = new ServerFireResolver(compensator, seed: 7);
            WeaponRuntimeState state = WeaponRuntimeState.Loaded(in config);
            var hits = new HitResult[config.ProjectilesPerShot];

            // Tall enough that vertical spread never leaves it, so this measures the horizontal
            // bound only and cannot fail for the wrong reason.
            var panel = Aabb.FromSize(
                new Vec3(0f, Muzzle.Y, range), new Vec3(halfWidth * 2f, 200f, 0.5f));
            var target = new HitboxSet(panel, panel, panel, panel);

            resolver.Resolve(
                ref state, in config, new[] { new HitscanTarget(Target, true, target) },
                Shooter, true, Muzzle, Forward, nowSeconds: 10f, smoothedRttMs: 0f,
                currentTick: 10, hits, out int hitCount);

            return hitCount;
        }

        [Fact]
        public void SpreadIsRolledByTheServerAndVariesBetweenPellets()
        {
            // Decision AD-3: the client's roll is never sent and never trusted. If it were, a
            // modified client would send zero spread and turn every weapon into a laser.
            var config = new WeaponConfig(
                cooldown: 0f, spread: 0.2f, projectilesPerShot: 32, range: 300f,
                damage: 1f, force: 0f, clipSize: 255);

            var compensator = new LagCompensator(new HitboxHistory());
            var resolver = new ServerFireResolver(compensator, seed: 11);
            WeaponRuntimeState state = WeaponRuntimeState.Loaded(in config);
            var hits = new HitResult[32];

            // A narrow post: with real spread, some pellets miss it.
            var post = HitboxSet.Humanoid(new Vec3(0f, 0f, 30f));

            resolver.Resolve(
                ref state, in config, new[] { new HitscanTarget(Target, true, post) },
                Shooter, true, Muzzle, Forward, nowSeconds: 10f, smoothedRttMs: 0f,
                currentTick: 10, hits, out int hitCount);

            Assert.True(hitCount > 0, "every pellet missed — the cone is not pointing at the target");
            Assert.True(hitCount < 32, "every pellet landed identically — spread is not being applied");
        }

        [Fact]
        public void TheSameSeedProducesTheSameSpread()
        {
            // AD-3 means the client need not agree, but a failing hit-rate measurement still
            // has to be replayable.
            Assert.Equal(FireSequenceWithSeed(4242), FireSequenceWithSeed(4242));
            Assert.NotEqual(FireSequenceWithSeed(4242), FireSequenceWithSeed(99));
        }

        [Fact]
        public void ZeroSpreadFiresStraightDownTheAim()
        {
            var laser = new WeaponConfig(
                cooldown: 0f, spread: 0f, projectilesPerShot: 4, range: 300f,
                damage: 1f, force: 0f, clipSize: 255);

            var fixture = new FireFixture(laser);
            fixture.Fire(nowSeconds: 10f);

            Assert.Equal(4, fixture.HitCount);
            for (int i = 1; i < fixture.HitCount; i++)
                Assert.Equal(fixture.Hits[0].Distance, fixture.Hits[i].Distance, 4);
        }

        [Fact]
        public void MoreProjectilesThanTheHitBufferHoldsDoesNotOverrun()
        {
            var shotgun = new WeaponConfig(
                cooldown: 0f, spread: 0.01f, projectilesPerShot: 8, range: 50f,
                damage: 12f, force: 0f, clipSize: 6);

            var compensator = new LagCompensator(new HitboxHistory());
            var resolver = new ServerFireResolver(compensator);
            WeaponRuntimeState state = WeaponRuntimeState.Loaded(in shotgun);
            var undersized = new HitResult[3];

            resolver.Resolve(
                ref state, in shotgun,
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, true, Muzzle, Forward, nowSeconds: 10f, smoothedRttMs: 0f,
                currentTick: 10, undersized, out int hitCount);

            Assert.True(hitCount <= 3);
        }

        [Fact]
        public void ProjectilesPerShotIsNeverLessThanOne()
        {
            // A misconfigured asset with 0 pellets would otherwise consume ammo and resolve
            // nothing — a weapon that fires blanks, with no error anywhere.
            var broken = new WeaponConfig(
                cooldown: 0.1f, spread: 0f, projectilesPerShot: 0, range: 100f,
                damage: 10f, force: 0f, clipSize: 10);

            Assert.Equal(1, broken.ProjectilesPerShot);
        }

        [Fact]
        public void AFreshWeaponCanFireImmediately()
        {
            // LastFiredTime starts at negative infinity, not 0: starting at 0 would block the
            // first shot of any match whose clock had not yet passed the cooldown.
            WeaponRuntimeState state = WeaponRuntimeState.Loaded(WeaponConfig.Rifle);

            Assert.Equal(
                FireRejection.None,
                ServerFireResolver.CheckCanFire(in state, WeaponConfig.Rifle, true, nowSeconds: 0f));
        }

        [Fact]
        public void ResetStatisticsZeroesTheCounters()
        {
            var fixture = new FireFixture();
            fixture.Fire(nowSeconds: 10f);

            fixture.Resolver.ResetStatistics();

            Assert.Equal(0, fixture.Resolver.ShotsFired);
            Assert.Equal(0, fixture.Resolver.FireRateViolations);
        }

        // ------------------------------------------------------------------ helpers

        private static string FireSequenceWithSeed(uint seed)
        {
            var config = new WeaponConfig(
                cooldown: 0f, spread: 0.15f, projectilesPerShot: 16, range: 300f,
                damage: 1f, force: 0f, clipSize: 255);

            var resolver = new ServerFireResolver(new LagCompensator(new HitboxHistory()), seed);
            WeaponRuntimeState state = WeaponRuntimeState.Loaded(in config);
            var hits = new HitResult[16];

            resolver.Resolve(
                ref state, in config,
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 25f))) },
                Shooter, true, Muzzle, Forward, nowSeconds: 10f, smoothedRttMs: 0f,
                currentTick: 10, hits, out int hitCount);

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < hitCount; i++)
                builder.Append(hits[i].Distance.ToString("F5")).Append('|');

            return builder.ToString();
        }

        /// <summary>A shooter, a rifle, and one target standing at 10 m.</summary>
        private sealed class FireFixture
        {
            public readonly LagCompensator Compensator = new LagCompensator(new HitboxHistory());
            public readonly ServerFireResolver Resolver;
            public readonly HitResult[] Hits;

            public WeaponRuntimeState State;

            private readonly WeaponConfig _config;
            private readonly HitscanTarget[] _targets;

            public int HitCount { get; private set; }

            public FireFixture(WeaponConfig? config = null)
            {
                _config = config ?? WeaponConfig.Rifle;
                Resolver = new ServerFireResolver(Compensator, seed: 2026);
                State = WeaponRuntimeState.Loaded(in _config);
                Hits = new HitResult[Math.Max(_config.ProjectilesPerShot, 1)];

                _targets = new[]
                {
                    new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))),
                };
            }

            public FireRejection Fire(float nowSeconds, bool shooterIsAlive = true)
            {
                FireRejection rejection = Resolver.Resolve(
                    ref State, in _config, _targets, Shooter, shooterIsAlive,
                    Muzzle, Forward, nowSeconds, smoothedRttMs: 0f, currentTick: 10,
                    Hits, out int hitCount);

                HitCount = hitCount;
                return rejection;
            }
        }
    }
}
