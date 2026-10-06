using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Each gun's bullet flies its own arc under gravity and drag, and the server judges the shot
    /// along it. Owner request 2026-10-06: "at 300 m a sniper aimed right is sure to hit; a
    /// shotgun cannot".
    /// </summary>
    public sealed class BallisticShotTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;
        private static readonly Vec3 Eye = new Vec3(0f, 1.5f, 0f);

        private static HitboxSet TargetAt(float metres) => HitboxSet.Humanoid(new Vec3(0f, 0f, metres));

        private static HitResult Fire(
            in RoundBallistics round, float range, in Vec3 aimPoint, float targetMetres,
            Func<OcclusionQuery, bool>? occlusion = null)
        {
            var compensator = new LagCompensator(new HitboxHistory()) { Occlusion = occlusion };
            HitscanTarget[] targets = { new HitscanTarget(Target, true, TargetAt(targetMetres)) };
            Vec3 aim = (aimPoint - Eye).Normalized;
            return compensator.ResolveBallistic(
                targets, Shooter, in Eye, round.LaunchDirection(in aim), in round, range, 0f, 100u);
        }

        private static Vec3 HeadOf(float metres) => TargetAt(metres).Head.Center;

        private static WeaponConfig Gun(byte id) => WeaponCatalog.For(id);

        [Fact]
        public void EveryGunFliesItsOwnArc()
        {
            byte[] guns =
            {
                WeaponIds.RK44, WeaponIds.SIND7, WeaponIds.SIND7_SUPPRESSED, WeaponIds.EAGLE_76,
                WeaponIds.SL_DEFENDER, WeaponIds.SIGNAL_DMR, WeaponIds.RECON_LRR,
            };
            foreach (byte id in guns)
            {
                Assert.True(Gun(id).Round.IsBallistic, $"weapon {id} still fires a straight ray");
            }
        }

        [Fact]
        public void ASniperAimedAtAHeadThreeHundredMetresOutHitsLower()
        {
            WeaponConfig sniper = Gun(WeaponIds.SL_DEFENDER);

            HitResult hit = Fire(sniper.Round, sniper.Range, HeadOf(300f), 300f);

            Assert.True(hit.Hit, "the round dropped clean under the whole body");
            Assert.NotEqual(HitboxType.Head, hit.HitboxType);
        }

        [Fact]
        public void HoldingOverForTheDropPutsTheSniperRoundInTheHead()
        {
            WeaponConfig sniper = Gun(WeaponIds.SL_DEFENDER);
            Vec3 head = HeadOf(300f);
            Vec3 holdOver = new Vec3(head.X, head.Y + sniper.Round.DropBelowSight(300f), head.Z);

            HitResult hit = Fire(sniper.Round, sniper.Range, in holdOver, 300f);

            Assert.True(hit.Hit);
            Assert.Equal(HitboxType.Head, hit.HitboxType);
        }

        [Fact]
        public void AtItsZeroARifleRoundLandsWhereTheSightsLook()
        {
            WeaponConfig rifle = Gun(WeaponIds.RK44);

            HitResult hit = Fire(rifle.Round, rifle.Range, HeadOf(100f), 100f);

            Assert.True(hit.Hit);
            Assert.Equal(HitboxType.Head, hit.HitboxType);
        }

        [Fact]
        public void AShotgunCannotReachThreeHundredMetres()
        {
            WeaponConfig shotgun = Gun(WeaponIds.EAGLE_76);
            Vec3 torso = TargetAt(300f).Torso.Center;

            Assert.False(Fire(shotgun.Round, shotgun.Range, in torso, 300f).Hit);
            // Nor at any holdover: by then the pellets have fallen tens of metres and slowed by
            // more than eight times, and the gun's range ends long before.
            Assert.True(shotgun.Round.DropBelowSight(300f) > 20f);
        }

        [Fact]
        public void APistolDropsMoreThanARifleWhichDropsMoreThanASniperRifle()
        {
            float pistol = Gun(WeaponIds.SIND7).Round.DropBelowSight(150f);
            float rifle = Gun(WeaponIds.RK44).Round.DropBelowSight(150f);
            float sniper = Gun(WeaponIds.SL_DEFENDER).Round.DropBelowSight(150f);

            Assert.True(pistol > rifle && rifle > sniper, $"pistol {pistol:F2}, rifle {rifle:F2}, sniper {sniper:F2}");
        }

        [Fact]
        public void ARidgeUnderTheArcStopsTheRound()
        {
            WeaponConfig sniper = Gun(WeaponIds.SL_DEFENDER);
            Vec3 torso = TargetAt(300f).Torso.Center;

            // The world answers yes for any chord crossing z = 150 m, and only once a hit asks.
            bool Ridge(OcclusionQuery q) => q.Origin.Z <= 150f && q.Point.Z >= 150f;

            Assert.True(Fire(sniper.Round, sniper.Range, in torso, 300f).Hit);
            Assert.False(Fire(sniper.Round, sniper.Range, in torso, 300f, Ridge).Hit);
        }

        [Fact]
        public void TheDistanceIsThePathFlownForTheDropOff()
        {
            WeaponConfig sniper = Gun(WeaponIds.SL_DEFENDER);
            Vec3 torso = TargetAt(300f).Torso.Center;

            HitResult hit = Fire(sniper.Round, sniper.Range, in torso, 300f);

            Assert.InRange(hit.Distance, 299f, 301f);
        }

        [Fact]
        public void AGunWithNoFlightStillSweepsAStraightRay()
        {
            Vec3 torso = TargetAt(300f).Torso.Center;
            RoundBallistics none = default;

            HitResult hit = Fire(in none, 400f, in torso, 300f);

            Assert.True(hit.Hit);
            Assert.Equal(HitboxType.Body, hit.HitboxType);
        }

        [Fact]
        public void TheResolverSweepsABallisticGunAlongItsArc()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            var resolver = new ServerFireResolver(compensator) { DiagnosticSpreadScale = 0f };
            WeaponConfig sniper = Gun(WeaponIds.SL_DEFENDER);
            WeaponRuntimeState state = WeaponRuntimeState.Loaded(in sniper);
            HitscanTarget[] targets = { new HitscanTarget(Target, true, TargetAt(300f)) };
            Vec3 aim = (HeadOf(300f) - Eye).Normalized;
            Span<HitResult> hits = stackalloc HitResult[1];

            FireRejection rejection = resolver.Resolve(
                ref state, in sniper, targets, Shooter, true, in Eye, in aim, 100f, 0f, 100u,
                hits, out int hitCount);

            Assert.Equal(FireRejection.None, rejection);
            Assert.Equal(1, compensator.BallisticShots);
            Assert.Equal(1, hitCount);
            Assert.NotEqual(HitboxType.Head, hits[0].HitboxType);
        }
    }
}
