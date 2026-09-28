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
