using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Ledger row <b>X-24</b>, second half — the pin that keeps the 3 cm vertical seam closed,
    /// and the balance statement the fix owes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Observed RED against the tree that shipped the instrument</b>, which is the commit
    /// immediately before this one: <c>vertical seam of 0.0300 m below head: nothing covers
    /// 1.5500..1.5800 m on a standing body at scale 1</c>, and the same at scale 0.85. Raw
    /// output: <c>plans/verdict-closure/reports/2026-08-26-r4-x24-observed-red.txt</c>.
    /// </para>
    /// <para>
    /// <b>The fix chosen, and what it costs.</b> The torso's top edge was raised to meet the
    /// head's bottom edge; the head box is untouched. The alternatives were to lower the head's
    /// bottom edge (which enlarges the one box carrying a damage multiplier by 12.5% and moves
    /// where a headshot begins) or to add a fifth neck box (a wire-visible change:
    /// <c>HitboxType</c> has three values and a neck would have to be one of them anyway). So
    /// the band now resolves as <c>Body</c>, headshot geometry is exactly where it was, and the
    /// mesh fit changes only in that the torso box now includes the neck — which is where a
    /// human aiming at the same band expects a body hit.
    /// </para>
    /// <para>
    /// The seam it closes is asserted over the UNION of the boxes rather than against named
    /// constants, so a future set with a different stacking is still graded on the only thing
    /// that matters: that a level ray at any height on a standing body finds something.
    /// </para>
    /// </remarks>
    public sealed class HitboxCoverageTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;

        /// <summary>A height inside the old seam, off its midpoint so no tie decides the answer.</summary>
        private const float SeamHeight = 1.560f;

        [Fact]
        public void NoVerticalBandOfAStandingBodyIsUncovered()
        {
            // X-24's pin, observed RED against the pre-fix HitboxSet.Humanoid:
            //   legs  0.000..0.900
            //   torso 0.850..1.550
            //   arms  0.950..1.550
            //   head  1.580..1.820   <- 0.030 m of nothing below it
            //
            // Asserted over the UNION of the four boxes rather than against named constants: a
            // future set with five boxes, or a different stacking order, is still graded on the
            // only thing that matters -- that a level ray at any height on a standing body finds
            // something.
            AssertContiguous(HitboxSet.Humanoid(Vec3.Zero), scale: 1f);
        }

        [Fact]
        public void TheCoverageHoldsAtEveryScale()
        {
            // Every box scales together, so a seam at scale 1 is a seam at every scale -- and a
            // seam that only appears when scaled would be a rounding fault worth catching.
            AssertContiguous(HitboxSet.Humanoid(new Vec3(3f, 12f, -7f), scale: 0.85f), scale: 0.85f);
            AssertContiguous(HitboxSet.Humanoid(new Vec3(-1f, 0f, 4f), scale: 1.15f), scale: 1.15f);
        }

        [Fact]
        public void ALevelShotThroughTheOldSeamNowLands()
        {
            // The end-to-end statement of the same fact, through the real resolver: what the
            // 2026-08-25 runs could not do.
            var compensator = new LagCompensator(new HitboxHistory());

            HitResult hit = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, new Vec3(0f, SeamHeight, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.True(hit.Hit, "the 1.560 m band still resolves as a miss");

            // And it lands as a BODY hit, not a headshot. That is the balance half of the fix:
            // the head box is untouched, so where a headshot starts has not moved.
            Assert.Equal(HitboxType.Body, hit.HitboxType);
        }

        [Fact]
        public void TheHeadBoxIsUnchangedByTheSeamFix()
        {
            // The balance pin. X-24's fix raises the TORSO's top edge to meet the head; lowering
            // the head's bottom edge instead would have enlarged the one box with a damage
            // multiplier by 12.5% and moved where a headshot begins. This test is what makes
            // that a decision rather than a drift.
            HitboxSet body = HitboxSet.Humanoid(Vec3.Zero);

            Assert.Equal(1.70f, body.Head.Center.Y, 4);
            Assert.Equal(0.12f, body.Head.Extents.Y, 4);
            Assert.Equal(1.58f, body.Head.Min.Y, 4);
            Assert.Equal(1.82f, body.Head.Max.Y, 4);
        }

        [Fact]
        public void TheTorsoStillContainsTheAimPointWithMargin()
        {
            // HitboxSet.HumanoidTorsoCenterHeight is what ScriptedAim aims at (X-25). Raising the
            // torso's top edge moved the box without moving that constant, so the margin has to
            // be re-stated rather than assumed: the aim point must stay well inside the box, or
            // X-25 quietly re-opens.
            HitboxSet body = HitboxSet.Humanoid(Vec3.Zero);
            float aim = HitboxSet.HumanoidTorsoCenterHeight;

            Assert.True(aim - body.Torso.Min.Y >= 0.30f,
                        $"aim point {aim} is only {aim - body.Torso.Min.Y:F3} m above the torso floor");
            Assert.True(body.Torso.Max.Y - aim >= 0.30f,
                        $"aim point {aim} is only {body.Torso.Max.Y - aim:F3} m below the torso ceiling");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Asserts the four boxes cover one unbroken vertical band from the feet to the crown.
        /// </summary>
        private static void AssertContiguous(in HitboxSet body, float scale)
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
                    + $"covers {reach:F4}..{spans[i].Min:F4} m on a standing body at scale {scale}. "
                    + "A ray through that band hits a live player for nothing (ledger X-24).");

                if (spans[i].Max > reach) reach = spans[i].Max;
            }

            // And the band is a whole body tall. Four boxes could be contiguous and still cover
            // only the shins, which would pass every assertion above.
            Assert.True(reach - spans[0].Min >= 1.80f * scale,
                        $"the covered band is only {reach - spans[0].Min:F3} m tall at scale "
                        + $"{scale}; a standing body is {1.82f * scale:F3} m");
        }

    }
}
