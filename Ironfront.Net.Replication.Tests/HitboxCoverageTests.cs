using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// What a shot at a standing or crouched soldier resolves to, against the boxes measured
    /// from the character every client draws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Playtest 2026-09-28, bug 5: "a clean headshot does not kill, and it takes most of an
    /// AK magazine to drop anyone".</b> The set before it was authored rather than measured: a
    /// head box at 1.58..1.82 m over a head drawn at 1.31..1.72 m, so a shot through the face
    /// resolved as a body hit for 35 instead of a headshot for 140; and a torso 0.50 x 0.32 m
    /// fixed to the world axes, against the original's 0.70 x 0.55 m body box that turns with the
    /// soldier.
    /// </para>
    /// <para>
    /// <b>Ledger X-24 still holds</b> and is still asserted over the union of the boxes: a
    /// level ray at any height of a standing body finds something. The seam it closed was
    /// 1.550..1.580 m; that band is the face now, and lands as a headshot.
    /// </para>
    /// <para>
    /// The numbers themselves are pinned against the prefab by the EditMode suite
    /// (<c>HitboxGeometryTests</c>), which runs the character's Animator and reads its colliders.
    /// This suite pins what the resolver does with them.
    /// </para>
    /// </remarks>
    public sealed class HitboxCoverageTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;

        [Fact]
        public void NoVerticalBandOfAStandingBodyIsUncovered()
        {
            AssertContiguous(HitboxSet.Humanoid(Vec3.Zero), scale: 1f);
        }

        [Fact]
        public void TheCoverageHoldsAtEveryScaleHeadingAndStance()
        {
            AssertContiguous(HitboxSet.Humanoid(new Vec3(3f, 12f, -7f), scale: 0.85f), scale: 0.85f);
            AssertContiguous(HitboxSet.Humanoid(new Vec3(-1f, 0f, 4f), scale: 1.15f), scale: 1.15f);
            AssertContiguous(HitboxSet.Humanoid(Vec3.Zero, 37f, crouching: false), scale: 1f);
            AssertContiguous(HitboxSet.Humanoid(Vec3.Zero, 250f, crouching: true), scale: 1f,
                minimumHeight: 1.25f);
        }

        [Theory]
        [InlineData(1.40f)]   // the chin
        [InlineData(1.52f)]   // the eyes
        [InlineData(1.56f)]   // X-24's old seam, inside the face now
        [InlineData(1.68f)]   // the top of the helmet
        public void ALevelShotAtTheDrawnHeadIsAHeadshot(float height)
        {
            Assert.Equal(HitboxType.Head, LevelShot(height).HitboxType);
        }

        [Theory]
        [InlineData(0.10f)]   // the boots
        [InlineData(0.60f)]   // the hips
        [InlineData(1.20f)]   // the chest
        public void ALevelShotAtTheDrawnBodyIsABodyHit(float height)
        {
            // Legs included: the original's body box runs from the boots to the chin at x1, so a
            // leg shot is a body shot, and the legs box sits inside it where it never wins a ray.
            HitResult hit = LevelShot(height);

            Assert.True(hit.Hit, $"a level shot at {height} m missed a standing body");
            Assert.Equal(HitboxType.Body, hit.HitboxType);
        }

        [Theory]
        [InlineData(0f)]      // facing away from the shooter
        [InlineData(180f)]    // facing the shooter, rifle raised
        public void AShotAtTheChestFromInFrontOrBehindIsABodyHit(float targetYaw)
        {
            // The arms box is wider than the body and no deeper, so from the front and from
            // behind the chest resolves as the body it is.
            HitResult hit = LevelShot(1.20f, targetYaw);

            Assert.True(hit.Hit);
            Assert.Equal(HitboxType.Body, hit.HitboxType);
        }

        [Fact]
        public void FromTheSideTheArmCoversTheChestAndNotTheHips()
        {
            // Side on, the arm hangs over the ribs, so that band is a limb, as it is in the
            // original rig; below it the hips are body.
            Assert.Equal(HitboxType.Limb, LevelShot(1.20f, targetYaw: 90f).HitboxType);
            Assert.Equal(HitboxType.Body, LevelShot(0.60f, targetYaw: 90f).HitboxType);
        }

        [Fact]
        public void TheHeadBoxIsWhereTheCharactersHeadIsDrawn()
        {
            HitboxSet body = HitboxSet.Humanoid(Vec3.Zero);

            Assert.Equal(1.52f, body.Head.Center.Y, 4);
            Assert.Equal(1.31f, body.Head.Min.Y, 4);
            Assert.Equal(1.73f, body.Head.Max.Y, 4);
            Assert.Equal(0.17f, body.Head.Extents.X, 4);
            Assert.Equal(0.17f, body.Head.Extents.Z, 4);
        }

        [Fact]
        public void TheBodyTurnsWithTheSoldier()
        {
            // Facing +Z the body is 0.70 m across the shoulders along X and 0.55 m deep along Z;
            // turned to face +X the two swap. A footprint fixed to the world axes was right for
            // one of these and wrong for the other.
            HitboxSet north = HitboxSet.Humanoid(Vec3.Zero, 0f, crouching: false);
            HitboxSet east = HitboxSet.Humanoid(Vec3.Zero, 90f, crouching: false);

            Assert.Equal(0.35f, north.Torso.Extents.X, 3);
            Assert.Equal(0.275f, north.Torso.Extents.Z, 3);
            Assert.Equal(0.275f, east.Torso.Extents.X, 3);
            Assert.Equal(0.35f, east.Torso.Extents.Z, 3);

            // Turned half-way, the box around the turned body is wider than either face.
            HitboxSet diagonal = HitboxSet.Humanoid(Vec3.Zero, 45f, crouching: false);
            Assert.True(diagonal.Torso.Extents.X > 0.44f);
        }

        [Fact]
        public void ACrouchedSoldiersHeadIsLowerAndForward()
        {
            HitboxSet standing = HitboxSet.Humanoid(Vec3.Zero, 0f, crouching: false);
            HitboxSet crouched = HitboxSet.Humanoid(Vec3.Zero, 0f, crouching: true);

            Assert.Equal(1.11f, crouched.Head.Center.Y, 3);
            Assert.True(crouched.Head.Max.Y < standing.Head.Min.Y + 0.05f,
                "a crouched head must sit well below where a standing one starts");

            // Forward is +Z facing north; turned to face +X the offset turns with the body.
            Assert.True(crouched.Head.Center.Z > 0.1f);
            HitboxSet crouchedEast = HitboxSet.Humanoid(Vec3.Zero, 90f, crouching: true);
            Assert.True(crouchedEast.Head.Center.X > 0.1f);
        }

        [Fact]
        public void TheTorsoStillContainsTheAimPointWithMargin()
        {
            // HitboxSet.HumanoidTorsoCenterHeight is what ScriptedAim aims at (X-25): the aim
            // point must stay well inside the box, or X-25 quietly re-opens.
            HitboxSet body = HitboxSet.Humanoid(Vec3.Zero);
            float aim = HitboxSet.HumanoidTorsoCenterHeight;

            Assert.True(aim - body.Torso.Min.Y >= 0.30f,
                        $"aim point {aim} is only {aim - body.Torso.Min.Y:F3} m above the torso floor");
            Assert.True(body.Torso.Max.Y - aim >= 0.30f,
                        $"aim point {aim} is only {body.Torso.Max.Y - aim:F3} m below the torso ceiling");
        }

        // ------------------------------------------------------------------ poses
        //
        // Bug 5 audit, the same playtest. The client draws a remote body in the pose its snapshot
        // bits give it -- moving, sprinting, crouched, crouch-walking, seated -- and each moves the
        // head a measured distance. Boxes shaped as a standing idle scored a headshot on anyone
        // running as a body hit, and missed the top half of a crouch-walker outright.

        [Theory]
        [InlineData(HumanoidPose.Standing, 1.75f)]
        [InlineData(HumanoidPose.Moving, 1.65f)]
        [InlineData(HumanoidPose.Sprinting, 1.55f)]
        [InlineData(HumanoidPose.Crouched, 1.35f)]
        [InlineData(HumanoidPose.CrouchMoving, 1.55f)]
        [InlineData(HumanoidPose.Seated, 1.70f)]
        [InlineData(HumanoidPose.SeatedQuad, 1.75f)]
        public void EveryPoseCoversTheBodyWithoutASeam(HumanoidPose pose, float minimumHeight)
        {
            AssertContiguous(HitboxSet.Humanoid(Vec3.Zero, 0f, pose, 0f, 0f), 1f, minimumHeight);
            AssertContiguous(HitboxSet.Humanoid(new Vec3(4f, 2f, -3f), 137f, pose, 2f, -3f), 1f, minimumHeight);
        }

        [Theory]
        [InlineData(HumanoidPose.Standing, 0f, 1.52f, 0f)]
        [InlineData(HumanoidPose.Moving, 0f, 1.39f, 0f)]
        [InlineData(HumanoidPose.Sprinting, 0.10f, 1.32f, 0.21f)]
        [InlineData(HumanoidPose.Crouched, 0.13f, 1.11f, 0.13f)]
        [InlineData(HumanoidPose.CrouchMoving, 0.07f, 1.28f, 0.13f)]
        [InlineData(HumanoidPose.Seated, 0.01f, 0.95f, 0.155f)]
        [InlineData(HumanoidPose.SeatedQuad, 0.014f, 0.914f, 0.434f)]
        public void AShotAtWhereTheHeadIsDrawnIsAHeadshotInEveryPose(
            HumanoidPose pose, float right, float height, float forward)
        {
            // The measured mean of the drawn head, in the actor's frame; the target faces the
            // shooter's -Z so its forward is toward the shooter.
            HitResult hit = PoseShot(pose, targetYaw: 180f, right: right, height: height, forward: forward);

            Assert.True(hit.Hit, $"a shot at the drawn head of a {pose} target missed");
            Assert.Equal(HitboxType.Head, hit.HitboxType);
        }

        [Fact]
        public void ACrouchWalkersHeadIsWhereItIsDrawnNotWhereTheCrouchWas()
        {
            // The crouch-walk is upright: its head is drawn at 1.28 m, 0.17 m above the still
            // crouch's. At 1.45 m -- the top of that head -- the still crouch's boxes hold nothing.
            Assert.False(PoseShot(HumanoidPose.Crouched, 180f, 0.07f, 1.45f, 0.13f).Hit,
                "precondition: the still crouch ends below 1.45 m");
            Assert.Equal(HitboxType.Head,
                PoseShot(HumanoidPose.CrouchMoving, 180f, 0.07f, 1.45f, 0.13f).HitboxType);
        }

        [Fact]
        public void AMovingHeadLeadsTheTravel()
        {
            HitboxSet forward = HitboxSet.Humanoid(Vec3.Zero, 0f, HumanoidPose.Moving, 0f, 3.5f);
            HitboxSet backward = HitboxSet.Humanoid(Vec3.Zero, 0f, HumanoidPose.Moving, 0f, -3.5f);
            HitboxSet strafing = HitboxSet.Humanoid(Vec3.Zero, 0f, HumanoidPose.Moving, 3.5f, 0f);

            Assert.Equal(HitboxSet.HumanoidMovingHeadLead, forward.Head.Center.Z, 3);
            Assert.Equal(-HitboxSet.HumanoidMovingHeadLead, backward.Head.Center.Z, 3);
            Assert.Equal(HitboxSet.HumanoidMovingHeadLead, strafing.Head.Center.X, 3);

            // In the actor's frame: facing east, running east leads east.
            HitboxSet east = HitboxSet.Humanoid(Vec3.Zero, 90f, HumanoidPose.Moving, 3.5f, 0f);
            Assert.Equal(HitboxSet.HumanoidMovingHeadLead, east.Head.Center.X, 3);
        }

        [Theory]
        [InlineData(true, false, 0f, false, HumanoidPose.Seated)]
        [InlineData(true, true, 5f, true, HumanoidPose.Seated)]
        [InlineData(false, true, 0f, false, HumanoidPose.Crouched)]
        [InlineData(false, true, 1.5f, false, HumanoidPose.CrouchMoving)]
        [InlineData(false, false, 6.5f, true, HumanoidPose.Sprinting)]
        [InlineData(false, false, 3.5f, false, HumanoidPose.Moving)]
        [InlineData(false, false, 0.05f, false, HumanoidPose.Standing)]
        public void ThePoseIsTheOneTheClientsAnimatorResolves(
            bool seated, bool crouching, float speed, bool sprinting, HumanoidPose expected)
        {
            Assert.Equal(expected, HitboxSet.PoseFor(seated, crouching, speed, sprinting));
        }

        [Theory]
        [InlineData(true, true, false, HumanoidPose.SeatedQuad)]
        [InlineData(true, false, false, HumanoidPose.Seated)]
        [InlineData(true, true, true, HumanoidPose.SeatedQuad)]
        [InlineData(false, true, false, HumanoidPose.Standing)]
        public void AstrideIsTheSeatsPoseAndOnlyASeatsPose(
            bool seated, bool astride, bool crouching, HumanoidPose expected)
        {
            // The animator reads `seated type` only in the seated state, as the seat decides it.
            Assert.Equal(expected, HitboxSet.PoseFor(seated, astride, crouching, 0f, false));
        }

        [Fact]
        public void TheChairsBoxesMissTheQuadRidersDrawnHead()
        {
            // Leftover from the 2026-09-28 audit: the quad bike's driver was boxed -- and drawn --
            // in the chair pose. Astride, the head is drawn 0.28 m further forward. From the side
            // (target turned 90 degrees, so "forward" runs across the shot) a shot at that head
            // through the chair's boxes is not a headshot; through the astride boxes it is.
            HitResult throughTheChair = PoseShot(HumanoidPose.Seated, targetYaw: 90f,
                right: 0.014f, height: 0.914f, forward: 0.434f);

            Assert.False(throughTheChair.Hit && throughTheChair.HitboxType == HitboxType.Head,
                "precondition: the chair's head box reaches the quad rider's drawn head");
            Assert.Equal(HitboxType.Head, PoseShot(HumanoidPose.SeatedQuad, targetYaw: 90f,
                right: 0.014f, height: 0.914f, forward: 0.434f).HitboxType);
        }

        [Fact]
        public void TheOldOverloadsAreTheStandingAndCrouchedPoses()
        {
            Assert.Equal(
                HitboxSet.Humanoid(Vec3.Zero, 30f, HumanoidPose.Crouched, 0f, 0f).Head.Center,
                HitboxSet.Humanoid(Vec3.Zero, 30f, crouching: true).Head.Center);
            Assert.Equal(
                HitboxSet.Humanoid(Vec3.Zero, 30f, HumanoidPose.Standing, 0f, 0f).Torso.Center,
                HitboxSet.Humanoid(Vec3.Zero, 30f, crouching: false).Torso.Center);
        }

        // ------------------------------------------------------------------ helpers

        private static HitResult LevelShot(float height, float targetYaw = 0f)
        {
            var compensator = new LagCompensator(new HitboxHistory());
            HitboxSet target = HitboxSet.Humanoid(new Vec3(0f, 0f, 10f), targetYaw, crouching: false);

            return compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, target) },
                Shooter, new Vec3(0f, height, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);
        }

        /// <summary>
        /// A level shot along +Z at a still target 10 m away in <paramref name="pose"/>, aimed at
        /// the point <paramref name="right"/>, <paramref name="height"/>, <paramref name="forward"/>
        /// of the target's own frame.
        /// </summary>
        private static HitResult PoseShot(
            HumanoidPose pose, float targetYaw, float right, float height, float forward)
        {
            var feet = new Vec3(0f, 0f, 10f);
            HitboxSet target = HitboxSet.Humanoid(feet, targetYaw, pose, 0f, 0f);

            float radians = targetYaw * (float)(Math.PI / 180.0);
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);
            float x = right * cos + forward * sin;

            var compensator = new LagCompensator(new HitboxHistory());
            return compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, target) },
                Shooter, new Vec3(x, height, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);
        }

        /// <summary>
        /// Asserts the four boxes cover one unbroken vertical band from the feet to the crown.
        /// </summary>
        private static void AssertContiguous(in HitboxSet body, float scale, float minimumHeight = 1.75f)
        {
            var spans = new (float Min, float Max, string Name)[HitboxSet.Count];
            string[] names = { "head", "torso", "arms", "legs" };

            for (int i = 0; i < HitboxSet.Count; i++)
            {
                Aabb box = body[i];
                Assert.False(box.IsEmpty, $"{names[i]} is degenerate");
                spans[i] = (box.Min.Y, box.Max.Y, names[i]);
            }

            Array.Sort(spans, (a, b) => a.Min.CompareTo(b.Min));

            float reach = spans[0].Max;
            for (int i = 1; i < spans.Length; i++)
            {
                Assert.True(
                    spans[i].Min <= reach + 1e-4f,
                    $"vertical seam of {spans[i].Min - reach:F4} m below {spans[i].Name}: nothing "
                    + $"covers {reach:F4}..{spans[i].Min:F4} m at scale {scale}. "
                    + "A ray through that band hits a live player for nothing (ledger X-24).");

                if (spans[i].Max > reach) reach = spans[i].Max;
            }

            // And the band is a whole body tall. Four boxes could be contiguous and still cover
            // only the shins, which would pass every assertion above. The drawn character stands
            // 1.72..1.78 m in its idles.
            Assert.True(reach - spans[0].Min >= minimumHeight * scale,
                        $"the covered band is only {reach - spans[0].Min:F3} m tall at scale "
                        + $"{scale}; the character is taller than {minimumHeight * scale:F3} m");
        }
    }
}
