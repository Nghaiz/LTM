using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// orphan-closure O3 — a weapon that LAUNCHES stops being resolved as a bullet. Ledger
    /// <b>X-42</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the row was.</b> <c>artifacts/lane-b/r1-grenade-03</c> equipped a FRAG
    /// (<c>weaponId 7</c>), fired it 60 of 60 frames, and passed one shot:
    /// <c>rejection=None fired=True hits=1 targets=56 nearest[actor=42 alive=True d=1.2m]</c>.
    /// <c>explosionsTotal</c> was 0 on all three clients with <c>explosionsAttached</c> true, so
    /// the recorder was live and there was nothing to record. The grenade hit like a bullet:
    /// <c>ProjectilesPerShot</c> is a shotgun PELLET COUNT, not a weapon kind, and no branch
    /// launched anything.
    /// </para>
    /// <para>
    /// <b>The failing direction matters more than the passing one.</b> Most of these assert what
    /// a projectile weapon now does; <see cref="AHitscanWeaponIsUntouchedByTheDeliveryBranch"/>
    /// asserts that every gun in the game is unchanged, which is the regression a delivery kind
    /// defaulting the wrong way would cause — silently, on every rifle at once.
    /// </para>
    /// </remarks>
    public sealed class ThrownWeaponTests
    {
        private const ushort Shooter = 41;
        private const ushort Victim = 42;

        // A FRAG, as the catalogue holds it: one in the clip, 1.3 s cooldown, zero hitscan
        // damage, and a delivery that is not a ray.
        private static WeaponConfig Frag => WeaponCatalog.For(WeaponIds.FRAG);

        // ------------------------------------------------ the catalogue

        [Theory]
        [InlineData(WeaponIds.FRAG)]
        [InlineData(WeaponIds.SPEARHEAD)]
        [InlineData(WeaponIds.BEU_AW1)]
        [InlineData(WeaponIds.BIL_SCALPEL)]
        public void EveryWeaponWhoseDamageLivesOnAProjectileIsMarkedAsOne(byte weaponId)
        {
            // These four already carried `damage: 0f, force: 0f` with comments saying the real
            // numbers live on the projectile prefab. That was the tell: hitscan-resolving them
            // was always doing nothing, and only `hits=1` printing made it read as a near miss
            // rather than a category error.
            WeaponConfig config = WeaponCatalog.For(weaponId);

            Assert.Equal(WeaponDelivery.Projectile, config.Delivery);
            Assert.Equal(0f, config.Damage);
        }

        [Fact]
        public void EveryGunIsStillAHitscanWeapon()
        {
            // The companion direction, and the one that would catch a delivery kind applied too
            // widely. Asserting only that four ids are Projectile would pass just as happily on
            // a table where all seventeen are.
            byte[] guns =
            {
                WeaponIds.RK44, WeaponIds.SIND7, WeaponIds.SIND7_SUPPRESSED, WeaponIds.EAGLE_76,
                WeaponIds.SL_DEFENDER, WeaponIds.SIGNAL_DMR, WeaponIds.RECON_LRR,
            };

            foreach (byte id in guns)
            {
                Assert.Equal(WeaponDelivery.Hitscan, WeaponCatalog.For(id).Delivery);
                Assert.True(
                    WeaponCatalog.For(id).Damage > 0f,
                    $"weapon {id} is hitscan and does no damage, which is the shape X-42 named.");
            }

            Assert.Equal(WeaponDelivery.Hitscan, WeaponCatalog.Inert.Delivery);
        }

        // ------------------------------------------------ the authority

        [Fact]
        public void AThrownWeaponSpendsItsRoundAndSweepsNothing()
        {
            // The whole row in one assertion pair: the round IS spent (so ammo, cooldown and the
            // snapshot's weapon field all move as they should) and NO hitscan hit is produced
            // (so a grenade stops arriving as a bullet at 1.2 m).
            var fixture = new LaunchFixture();

            CombatTickResult result = fixture.Step(now: 10f, InputButtons.Fire);

            Assert.Equal(FireRejection.None, result.Rejection);
            Assert.True(result.Fired);
            Assert.True(result.LaunchedProjectile);
            Assert.Equal(0, result.HitCount);
            Assert.Equal(0, fixture.Weapon.AmmoInClip);
            Assert.True(result.WeaponChanged);
            Assert.Equal(1, fixture.Authority.ProjectilesLaunched);
        }

        [Fact]
        public void AThrownWeaponDoesNoDamageToABodyStandingOnTopOfIt()
        {
            // The literal artifact: a victim 1.2 m away, which the hitscan path resolved as a
            // hit. The blast is ActorManager.Explode's, and it is not this library's.
            var fixture = new LaunchFixture(victimFeet: new Vec3(0f, 0f, 1.2f));

            fixture.Step(now: 10f, InputButtons.Fire);

            Assert.Equal(100f, fixture.Sink.HealthOf(Victim));
            Assert.Equal(0, fixture.Sink.DamageApplications);
        }

        [Fact]
        public void AThrownWeaponHonoursTheServersCooldownAndItsOneRoundClip()
        {
            var fixture = new LaunchFixture();

            Assert.True(fixture.Step(10f, InputButtons.Fire).Fired);

            // Second pull inside the cooldown: refused, and counted as a rate violation, exactly
            // as a rifle's would be. A launcher that skipped the shared CheckCanFire would be a
            // second opinion about what a legal trigger pull is.
            CombatTickResult tooSoon = fixture.Step(10.1f, InputButtons.Fire);
            Assert.Equal(FireRejection.OnCooldown, tooSoon.Rejection);
            Assert.False(tooSoon.LaunchedProjectile);

            // Past the cooldown, but the clip holds one.
            CombatTickResult empty = fixture.Step(20f, InputButtons.Fire);
            Assert.Equal(FireRejection.NoAmmo, empty.Rejection);
            Assert.False(empty.LaunchedProjectile);

            Assert.Equal(1, fixture.Authority.ProjectilesLaunched);
        }

        [Fact]
        public void ACorpseDoesNotThrow()
        {
            // D5, and it has to hold on this path too or a queued frame from a dead player
            // launches a grenade the server already refused to let them shoot.
            var fixture = new LaunchFixture();

            CombatTickResult result = fixture.Step(10f, InputButtons.Fire, shooterIsAlive: false);

            Assert.Equal(FireRejection.ShooterDead, result.Rejection);
            Assert.False(result.LaunchedProjectile);
            Assert.Equal(1, fixture.Weapon.AmmoInClip);
        }

        [Fact]
        public void HoldingAThrownWeaponWithoutPullingTheTriggerLaunchesNothing()
        {
            // Rejection.None does not mean a shot was taken -- CombatTickResult's own remark.
            // Reading it as one here would have the bridge pull the engine's trigger on every
            // tick a player walked around holding a grenade.
            var fixture = new LaunchFixture();

            CombatTickResult result = fixture.Step(10f);

            Assert.Equal(FireRejection.None, result.Rejection);
            Assert.False(result.Fired);
            Assert.False(result.LaunchedProjectile);
            Assert.Equal(1, fixture.Weapon.AmmoInClip);
            Assert.Equal(0, fixture.Authority.ProjectilesLaunched);
        }

        [Fact]
        public void AHitscanWeaponIsUntouchedByTheDeliveryBranch()
        {
            // The regression a mis-defaulted delivery kind would cause: every gun in the game
            // silently stops doing damage, on the same day, with nothing naming the cause.
            var fixture = new LaunchFixture(weapon: WeaponConfig.Rifle,
                                            victimFeet: new Vec3(0f, 0f, 10f));

            CombatTickResult result = fixture.Step(10f, InputButtons.Fire);

            Assert.True(result.Fired);
            Assert.False(result.LaunchedProjectile);
            Assert.Equal(1, result.HitCount);
            Assert.True(fixture.Sink.HealthOf(Victim) < 100f);
            Assert.Equal(0, fixture.Authority.ProjectilesLaunched);
        }

        // ------------------------------------------------ the library still refuses to STEP one

        [Fact]
        public void TheProjectileStepperStillRefusesAGrenade()
        {
            // O-D3 rests on this staying true: the engine owns a grenade's flight because
            // nothing in this library models a bounce, and a stepped grenade would detonate on
            // the first wall it grazed. If this ever flips, O3's whole design needs re-reading
            // rather than adjusting.
            var authority = new ServerProjectileAuthority(
                new ServerProjectileRegistry(), new ProjectileCatalog());

            Assert.False(authority.StepsKind(ProjectileKind.Grenade));
            Assert.True(authority.StepsKind(ProjectileKind.Rocket));
        }

        // ------------------------------------------------ the Unity half, pinned as text

        [Fact]
        public void TheBridgePullsTheEnginesTriggerOnALaunchAndOnlyThen()
        {
            // Nothing under Assets/Scripts compiles here, so this is a text pin -- the same
            // arrangement VehicleClientSourceInvariantTests uses and for the same reason. What
            // it catches is the wiring being dropped during an unrelated edit, which is exactly
            // how this path came to be missing in the first place.
            string bridge = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerCombatBridge.cs");

            Assert.Contains(
                "if (result.LaunchedProjectile) LaunchCarriedProjectile(session, actor, in result);",
                bridge, StringComparison.Ordinal);

            // AFTER the authority's own `if (!result.Fired) return;`, never instead of it: a
            // launch that reached the engine without the server spending the round would be
            // infinite grenades.
            int fired = bridge.IndexOf("if (!result.Fired) return;", StringComparison.Ordinal);
            int launch = bridge.IndexOf("if (result.LaunchedProjectile)", StringComparison.Ordinal);
            Assert.True(fired >= 0 && launch > fired,
                        "the launch must sit after the fired-guard, or the server is not the one "
                        + "deciding whether a grenade leaves the hand.");

            // A body holding nothing is LOGGED. A silent zero here presents as a grenade count
            // going down and an explosion that never happens -- the row itself.
            Assert.Contains("holding nothing, so nothing was launched", bridge, StringComparison.Ordinal);
        }

        [Fact]
        public void TheGameplaySeamReachesTheWeaponTheBodyIsHolding()
        {
            string bindings = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/NetBindings/IronfrontNetBindings.cs");

            Assert.Contains(
                "weapon.Fire(new Vector3(directionX, directionY, directionZ), useMuzzleDirection: true);",
                bindings, StringComparison.Ordinal);
        }

        // ------------------------------------------------ helpers

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }

        /// <summary>A thrower at the origin and a body to fail to shoot.</summary>
        private sealed class LaunchFixture
        {
            private readonly WeaponConfig _config;
            private readonly HitscanTarget[] _targets;

            internal LaunchFixture(WeaponConfig? weapon = null, Vec3? victimFeet = null)
            {
                _config = weapon ?? Frag;

                Compensator = new LagCompensator(new HitboxHistory());
                Resolver = new ServerFireResolver(Compensator, seed: 7);
                Sink = new CountingDamageSink();
                Authority = new ServerCombatAuthority(Resolver, Sink, new ServerRespawnGate());

                Weapon = WeaponRuntimeState.Loaded(in _config);
                State = MoveState.AtRest(Vec3.Zero);
                Hits = new HitResult[Math.Max(1, _config.ProjectilesPerShot)];

                Sink.SetHealth(Victim, 100f);

                _targets = new[]
                {
                    new HitscanTarget(
                        Victim, true, HitboxSet.Humanoid(victimFeet ?? new Vec3(0f, 0f, 1.2f))),
                };
            }

            internal LagCompensator Compensator { get; }
            internal ServerFireResolver Resolver { get; }
            internal CountingDamageSink Sink { get; }
            internal ServerCombatAuthority Authority { get; }

            internal WeaponRuntimeState Weapon;
            internal MoveState State;
            internal HitResult[] Hits { get; }

            internal CombatTickResult Step(
                float now, InputButtons buttons = InputButtons.None, bool shooterIsAlive = true)
                => Authority.Step(
                    ref Weapon, in _config, Shooter,
                    InputFrame.FromFloats(0f, 0f, yawDegrees: 0f, pitchDegrees: 0f, buttons),
                    in State, _targets, shooterIsAlive, now, smoothedRttMs: 0f,
                    currentTick: (uint)(now * ProtocolConstants.SIM_TICK_RATE), Hits);
        }

        /// <summary>Health, and how many times anything asked to change it.</summary>
        private sealed class CountingDamageSink : IActorDamageSink
        {
            private readonly System.Collections.Generic.Dictionary<ushort, float> _health = new();

            internal int DamageApplications { get; private set; }

            internal void SetHealth(ushort actorId, float health) => _health[actorId] = health;

            internal float HealthOf(ushort actorId)
                => _health.TryGetValue(actorId, out float health) ? health : 0f;

            public DamageOutcome ApplyDamage(
                ushort actorId, float damage, float balanceDamage, ushort attackerActorId)
            {
                DamageApplications++;

                if (!_health.TryGetValue(actorId, out float health))
                    return new DamageOutcome(0f, died: false);

                float after = health - damage;
                if (after < 0f) after = 0f;
                _health[actorId] = after;

                return new DamageOutcome(after, died: health > 0f && after <= 0f);
            }

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }
    }
}
