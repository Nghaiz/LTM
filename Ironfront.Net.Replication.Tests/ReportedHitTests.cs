using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// "What you see is what you hit" (14.0.6, owner's run of 2026-10-10: "aimed dead on and it
    /// does not hit"). The shooter's game reports what its rounds struck; the server matches each
    /// claim to a pull it accepted (<see cref="ReportedShotLedger"/>) and judges it
    /// (<see cref="ReportedHitJudge"/>) before any damage. Honest shots must pass at every edge,
    /// and claims a round could not have made must not.
    /// </summary>
    public sealed class ReportedHitTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;
        private static readonly Vec3 Eye = new Vec3(0f, 1.6f, 0f);

        private static HitboxSet BodyAt(float x, float z) => HitboxSet.Humanoid(new Vec3(x, 0f, z));

        private static ReportedShot Shot(byte weapon, in Vec3 aimAt, byte rounds = 1, float swing = 0f)
            => new ReportedShot
            {
                Shooter = Shooter,
                InputTick = 100,
                ServerTick = 100,
                WeaponId = weapon,
                Origin = Eye,
                Aim = (aimAt - Eye).Normalized,
                FiredAt = 10f,
                Rounds = rounds,
                AimSwing = swing,
            };

        private static ShotReportHit Claim(in Vec3 point, HitboxType box = HitboxType.Body, ushort target = Target)
            => new ShotReportHit(
                target, box, 0,
                ShotReportHit.PackMillimetres(point.X), ShotReportHit.PackMillimetres(point.Y),
                ShotReportHit.PackMillimetres(point.Z), 0, 100);

        private static ReportVerdict Judge(
            in ReportedShot shot, in ShotReportHit claim, in HitboxSet pose, float speed = 0f,
            bool alive = true, bool enclosed = false)
            => Judge(in shot, in claim, in pose, out _, speed, alive, enclosed);

        private static ReportVerdict Judge(
            in ReportedShot shot, in ShotReportHit claim, in HitboxSet pose, out HitboxType box,
            float speed = 0f, bool alive = true, bool enclosed = false)
            => ReportedHitJudge.Judge(
                in shot, WeaponCatalog.For(shot.WeaponId), in claim, claim.TargetActorId, alive, enclosed,
                new[] { pose }, speed, out box, out _);

        // ------------------------------------------------------------------ the judge

        [Fact]
        public void AHeadshotWhereTheShooterAimedIsAHeadshot()
        {
            HitboxSet pose = BodyAt(0f, 50f);
            Vec3 head = pose.Head.Center;

            ReportVerdict verdict = Judge(Shot(WeaponIds.RK44, in head), Claim(in head, HitboxType.Head), in pose, out HitboxType box);

            Assert.Equal(ReportVerdict.Accepted, verdict);
            Assert.Equal(HitboxType.Head, box);
        }

        [Fact]
        public void AHeadClaimOnTheChestCountsOnTheBody()
        {
            HitboxSet pose = BodyAt(0f, 30f);
            Vec3 chest = pose.Torso.Center;

            ReportVerdict verdict = Judge(Shot(WeaponIds.RK44, in chest), Claim(in chest, HitboxType.Head), in pose, out HitboxType box);

            Assert.Equal(ReportVerdict.Accepted, verdict);
            Assert.Equal(HitboxType.Body, box);
        }

        [Fact]
        public void NobodyShootsThemselvesTheDeadOrNobody()
        {
            HitboxSet pose = BodyAt(0f, 20f);
            Vec3 chest = pose.Torso.Center;
            ReportedShot shot = Shot(WeaponIds.RK44, in chest);

            Assert.Equal(ReportVerdict.Self, Judge(in shot, Claim(in chest, target: Shooter), in pose));
            Assert.Equal(ReportVerdict.NoTarget, Judge(in shot, Claim(in chest), in pose, alive: false));
            Assert.Equal(ReportVerdict.NoTarget, ReportedHitJudge.Judge(
                in shot, WeaponCatalog.For(WeaponIds.RK44), Claim(in chest), Target, true, false,
                ReadOnlySpan<HitboxSet>.Empty, 0f, out _, out _));
        }

        [Fact]
        public void AnEnclosedCrewIsReachedOnlyByAPiercingRound()
        {
            HitboxSet pose = BodyAt(0f, 40f);
            Vec3 chest = pose.Torso.Center;

            Assert.Equal(ReportVerdict.EnclosedSeat,
                Judge(Shot(WeaponIds.RK44, in chest), Claim(in chest), in pose, enclosed: true));
            Assert.Equal(ReportVerdict.Accepted,
                Judge(Shot(WeaponIds.SL_DEFENDER, in chest), Claim(in chest), in pose, enclosed: true));
        }

        [Fact]
        public void APistolReachesTwoHundredMetresAndNoFurther()
        {
            HitboxSet near = BodyAt(0f, 190f);
            HitboxSet far = BodyAt(0f, 260f);
            Vec3 nearChest = near.Torso.Center;
            Vec3 farChest = far.Torso.Center;

            Assert.Equal(ReportVerdict.Accepted,
                Judge(Shot(WeaponIds.SIND7, in nearChest), Claim(in nearChest), in near));
            Assert.Equal(ReportVerdict.OutOfRange,
                Judge(Shot(WeaponIds.SIND7, in farChest), Claim(in farChest), in far));
        }

        [Fact]
        public void ABodyTheShooterDidNotAimAtIsOffTheAim()
        {
            HitboxSet aimed = BodyAt(0f, 50f);
            HitboxSet beside = BodyAt(10f, 50f);
            Vec3 aimedChest = aimed.Torso.Center;
            Vec3 besideChest = beside.Torso.Center;

            Assert.Equal(ReportVerdict.OffAim,
                Judge(Shot(WeaponIds.RK44, in aimedChest), Claim(in besideChest), in beside));
        }

        [Fact]
        public void AFlickEarnsTheSwingTheMouseActuallyMade()
        {
            // The round left on the shooter's own frame, along the aim of that moment: five
            // degrees from the frame the server fired on, in the middle of a flick.
            HitboxSet pose = BodyAt(0f, 30f);
            Vec3 chest = pose.Torso.Center;
            float fiveDegrees = 5f * (float)Math.PI / 180f;
            Vec3 aimedBeside = Eye + RotateYaw(chest - Eye, fiveDegrees);

            Assert.Equal(ReportVerdict.OffAim,
                Judge(Shot(WeaponIds.RK44, in aimedBeside), Claim(in chest), in pose));
            Assert.Equal(ReportVerdict.Accepted,
                Judge(Shot(WeaponIds.RK44, in aimedBeside, swing: fiveDegrees), Claim(in chest), in pose));
        }

        [Fact]
        public void APointAwayFromTheRecordedBodyIsNotOnIt()
        {
            HitboxSet pose = BodyAt(3f, 50f);
            var emptyAir = new Vec3(0f, 1.2f, 50f);

            Assert.Equal(ReportVerdict.NotOnBody,
                Judge(Shot(WeaponIds.RK44, in emptyAir), Claim(in emptyAir), in pose));
        }

        [Fact]
        public void AMovingBodyIsDrawnAheadAndEarnsSlack()
        {
            // Past 100 m a body is drawn up to eight ticks past its newest sample: 1.5 m off its
            // recorded box is honest for a runner and not for a body standing still.
            HitboxSet pose = BodyAt(0f, 150f);
            Vec3 drawnAhead = pose.Torso.Center + new Vec3(1.5f + pose.Torso.Extents.X, 0f, 0f);
            ReportedShot shot = Shot(WeaponIds.SIGNAL_DMR, in drawnAhead);

            Assert.Equal(ReportVerdict.NotOnBody, Judge(in shot, Claim(in drawnAhead), in pose, speed: 0f));
            Assert.Equal(ReportVerdict.Accepted, Judge(in shot, Claim(in drawnAhead), in pose, speed: 6f));
        }

        [Fact]
        public void ALongShotHeldOverItsDropIsOnTheAim()
        {
            // CURVATURE: a 900 m headshot with the sniper held over the round's drop.
            WeaponConfig sniper = WeaponCatalog.For(WeaponIds.SL_DEFENDER);
            HitboxSet pose = BodyAt(0f, 900f);
            Vec3 head = pose.Head.Center;
            float drop = Math.Abs(sniper.Round.DropBelowSight(Vec3.Distance(in Eye, in head)));
            var heldOver = new Vec3(head.X, head.Y + drop, head.Z);

            Assert.Equal(ReportVerdict.Accepted,
                Judge(Shot(WeaponIds.SL_DEFENDER, in heldOver), Claim(in head, HitboxType.Head), in pose));

            // And a rifle that reaches 400 m does not make the same shot.
            Assert.Equal(ReportVerdict.OutOfRange,
                Judge(Shot(WeaponIds.RK44, in heldOver), Claim(in head, HitboxType.Head), in pose));
        }

        [Fact]
        public void AShortShotChecksOneLineAndALongOneItsArc()
        {
            WeaponConfig sniper = WeaponCatalog.For(WeaponIds.SL_DEFENDER);
            RoundBallistics round = sniper.Round;
            var into = new Vec3[9];

            var near = new Vec3(0f, 1.6f, 40f);
            Assert.Equal(2, ReportedHitJudge.Chords(in Eye, in near, in round, into));

            var far = new Vec3(0f, 1.6f, 800f);
            int count = ReportedHitJudge.Chords(in Eye, in far, in round, into);
            Assert.Equal(9, count);
            Assert.Equal(Eye.Y, into[0].Y, 3);
            Assert.Equal(far.Z, into[count - 1].Z, 3);
            Assert.True(into[count / 2].Y > far.Y, "the arc rises above the line between its ends");
        }

        // ------------------------------------------------------------------ the ledger

        [Fact]
        public void EachRoundOfAnAcceptedPullIsTakenOnce()
        {
            var ledger = new ReportedShotLedger();
            ledger.Record(Shot(WeaponIds.RK44, new Vec3(0f, 1.6f, 10f)));

            Assert.True(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10.1f, out _));
            Assert.False(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10.1f, out _));
        }

        [Fact]
        public void AReportFindsNoPullOfAnotherWeaponOrAnotherMoment()
        {
            var ledger = new ReportedShotLedger();
            ledger.Record(Shot(WeaponIds.RK44, new Vec3(0f, 1.6f, 10f)));

            Assert.False(ledger.TryTake(Shooter, 100, WeaponIds.SIND7, 0, 10.1f, out _));
            Assert.False(ledger.TryTake(Shooter, 100 + ReportedShotLedger.TickWindow + 1, WeaponIds.RK44, 0, 10.1f, out _));
            Assert.False(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10f + ReportedShotLedger.MaxAgeSeconds + 0.1f, out _));
            Assert.True(ledger.TryTake(Shooter, 100 + ReportedShotLedger.TickWindow, WeaponIds.RK44, 0, 10.1f, out _));
        }

        [Fact]
        public void AShotgunsPelletsAreEachTheirOwnRound()
        {
            var ledger = new ReportedShotLedger();
            ledger.Record(Shot(WeaponIds.EAGLE_76, new Vec3(0f, 1.6f, 10f), rounds: 20));

            for (byte pellet = 0; pellet < 20; pellet++)
                Assert.True(ledger.TryTake(Shooter, 100, WeaponIds.EAGLE_76, pellet, 10.1f, out _), $"pellet {pellet}");

            Assert.False(ledger.TryTake(Shooter, 100, WeaponIds.EAGLE_76, 20, 10.1f, out _));
            Assert.False(ledger.TryTake(Shooter, 100, WeaponIds.EAGLE_76, 3, 10.1f, out _));
        }

        [Fact]
        public void AReportTakesTheNearestPull()
        {
            var ledger = new ReportedShotLedger();
            ReportedShot early = Shot(WeaponIds.RK44, new Vec3(0f, 1.6f, 10f));
            early.InputTick = 96;
            ReportedShot late = Shot(WeaponIds.RK44, new Vec3(0f, 1.6f, 10f));
            late.InputTick = 99;
            ledger.Record(early);
            ledger.Record(late);

            Assert.True(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10.1f, out ReportedShot taken));
            Assert.Equal(99u, taken.InputTick);
            Assert.True(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10.1f, out taken));
            Assert.Equal(96u, taken.InputTick);
        }

        [Fact]
        public void AReportMayWaitOnlyForAPullThatCanStillCome()
        {
            var ledger = new ReportedShotLedger();
            Assert.True(ledger.MayStillArrive(Shooter, 100), "no pull yet: the frame may be on its way");

            ReportedShot shot = Shot(WeaponIds.RK44, new Vec3(0f, 1.6f, 10f));
            shot.InputTick = 99;
            ledger.Record(shot);
            Assert.True(ledger.MayStillArrive(Shooter, 100));

            shot.InputTick = 100 + ReportedShotLedger.TickWindow + 1;
            ledger.Record(shot);
            Assert.False(ledger.MayStillArrive(Shooter, 100), "a later pull was applied: the frame came and went");
        }

        [Fact]
        public void TheSwingIsTheWidestTurnAroundThePull()
        {
            var ledger = new ReportedShotLedger();
            var ahead = new Vec3(0f, 0f, 1f);
            Vec3 turned = RotateYaw(ahead, 0.1f);
            ledger.RecordAim(Shooter, 98, in turned);
            ledger.RecordAim(Shooter, 99, in ahead);
            ledger.RecordAim(Shooter, 100, in ahead);
            ledger.RecordAim(Shooter, 104, RotateYaw(ahead, 1f));   // outside the window

            Assert.Equal(0.1f, ledger.AimSwing(Shooter, 100, in ahead), 3);

            ReportedShot shot = Shot(WeaponIds.RK44, Eye + ahead);
            ledger.Record(shot);
            Assert.True(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10.1f, out ReportedShot taken));
            Assert.Equal(0.1f, taken.AimSwing, 3);
        }

        [Fact]
        public void ALeaverIsForgotten()
        {
            var ledger = new ReportedShotLedger();
            ledger.Record(Shot(WeaponIds.RK44, new Vec3(0f, 1.6f, 10f)));
            ledger.Forget(Shooter);

            Assert.False(ledger.TryTake(Shooter, 100, WeaponIds.RK44, 0, 10.1f, out _));
        }

        private static Vec3 RotateYaw(in Vec3 v, float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return new Vec3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
        }
    }
}
