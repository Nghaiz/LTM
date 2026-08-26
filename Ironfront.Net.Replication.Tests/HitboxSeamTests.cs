using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Ledger row <b>X-24</b> — the 3 cm vertical seam in the hitbox stack, the instrument that
    /// measures it, and the pin that keeps it closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two halves, in the order the row insists on. This file is the MEASUREMENT half
    /// was written and observed against the pre-fix <c>HitboxSet.Humanoid</c>, where a level ray
    /// at 1.560 m struck nothing and the instrument reported <c>+0.010 m</c> above the torso —
    /// the row's "which box, and by how much", answered from a run rather than from the
    /// arithmetic that produced the constants.
    /// </para>
    /// <para>
    /// The COVERAGE half — the pin that keeps the seam closed, and the balance statement that
    /// goes with it — lives in <see cref="HitboxCoverageTests"/>, because it ships with the FIX
    /// and this file ships before it.
    /// </para>
    /// </remarks>
    public sealed class HitboxSeamTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;

        /// <summary>
        /// A height inside the old seam. Not its midpoint: at exactly 1.565 m the torso's top
        /// and the head's bottom are equidistant, and a tie would make the test's answer depend
        /// on the order the boxes happen to be scanned in rather than on the geometry.
        /// </summary>
        private const float SeamHeight = 1.560f;

        // ------------------------------------------------------------------ the instrument

        [Fact]
        public void TheInstrumentReportsASignedVerticalOffsetOnARealMiss()
        {
            // A body whose boxes stop 3 cm short of the head, reconstructed from the pre-fix
            // numbers rather than from today's HitboxSet -- so this test keeps measuring the
            // instrument after the geometry is fixed, instead of quietly becoming a no-op.
            HitboxSet seamed = PreFixHumanoid(new Vec3(0f, 0f, 10f));

            var compensator = new LagCompensator(new HitboxHistory());

            HitResult result = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, seamed) },
                Shooter, new Vec3(0f, SeamHeight, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.False(result.Hit);

            HitboxMiss miss = compensator.LastNearestMiss;

            Assert.True(miss.Measured, "a level shot through a live body's seam was not measured");
            Assert.Equal(1, compensator.NearestMissesMeasured);
            Assert.Equal(Target, miss.ActorId);

            // The nearest box is the torso, and the ray passed ABOVE it: 1.560 - 1.550.
            Assert.Equal("torso", miss.BoxName);
            Assert.Equal(HitboxType.Body, miss.Type);
            Assert.True(miss.VerticalOffsetMetres > 0f,
                        $"expected the ray to be recorded ABOVE the torso, got "
                        + $"{miss.VerticalOffsetMetres:F4} m");
            Assert.Equal(0.010f, miss.VerticalOffsetMetres, 3);
            Assert.Equal(0.010f, miss.GapMetres, 3);

            // Non-zero, which is the row's own acceptance wording, and stated as a fact rather
            // than inferred from the constants.
            Assert.NotEqual(0f, miss.VerticalOffsetMetres);
        }

        [Fact]
        public void AShotUnderTheHeadIsSignedTheOtherWay()
        {
            // The other side of the same 3 cm. Both signs matter: one of them says "raise the
            // torso", the other says "lower the head", and a magnitude alone says neither.
            HitboxSet seamed = PreFixHumanoid(new Vec3(0f, 0f, 10f));
            var compensator = new LagCompensator(new HitboxHistory());

            // Aim just under the head's lower edge but clear of the torso top, from a muzzle at
            // the same height so the ray is level.
            compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, seamed) },
                Shooter, new Vec3(0f, 1.570f, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            HitboxMiss miss = compensator.LastNearestMiss;

            Assert.True(miss.Measured);
            Assert.Equal("head", miss.BoxName);
            Assert.True(miss.VerticalOffsetMetres < 0f,
                        $"expected the ray to be recorded BELOW the head, got "
                        + $"{miss.VerticalOffsetMetres:F4} m");
            // 1.570 - 1.580. Ten millimetres under the chin, and the sign says so.
            Assert.Equal(-0.010f, miss.VerticalOffsetMetres, 3);
        }

        [Fact]
        public void AHitLeavesTheMissInstrumentAlone()
        {
            // The freshness contract. A shot that lands must not write a miss, or the shot log
            // would print a miss for a hit -- the mirror of the X-20 leftover-occlusion trap.
            var compensator = new LagCompensator(new HitboxHistory());
            var target = new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f)));

            HitResult hit = compensator.ResolveHitscan(
                new[] { target }, Shooter, new Vec3(0f, 1.2f, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.True(hit.Hit);
            Assert.Equal(0, compensator.NearestMissesMeasured);
            Assert.False(compensator.LastNearestMiss.Measured);
        }

        [Fact]
        public void AnOccludedShotIsNotRecordedAsAMiss()
        {
            // Blocked is not missed. Conflating them is what made X-20 and X-24 one symptom.
            var compensator = new LagCompensator(new HitboxHistory())
            {
                Occlusion = (_, _, _) => true,
            };

            compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, new Vec3(0f, 1.2f, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.Equal(1, compensator.ShotsOccluded);
            Assert.Equal(0, compensator.NearestMissesMeasured);
        }

        [Fact]
        public void AMissWithNoLiveCandidateIsUnmeasuredRatherThanZero()
        {
            // `green-that-proves-nothing.md`: unknown must not render as good. A gap of 0.000
            // would read as "the shot grazed them".
            var compensator = new LagCompensator(new HitboxHistory());

            compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, false, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, new Vec3(0f, 1.2f, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.Equal(0, compensator.NearestMissesMeasured);
            Assert.False(compensator.LastNearestMiss.Measured);
            Assert.Equal("unmeasured", compensator.LastNearestMiss.Describe());
        }

        [Fact]
        public void TheDescriptionIsDatedAgainstTheCounterRatherThanReprinted()
        {
            var stale = new HitboxMiss(7, 1, HitboxType.Body, 0.015f, 0.015f, new Vec3(0f, 1.5f, 3f));

            // The counter did not move since the last logged shot: this shot missed nothing.
            Assert.Equal("none-this-shot", LagCompensator.NearestMissFor(4, 4, in stale));
            Assert.Equal("none-this-shot", LagCompensator.NearestMissFor(3, 4, in stale));

            // It moved: the description belongs to this shot.
            Assert.Contains("box=torso", LagCompensator.NearestMissFor(5, 4, in stale));
            Assert.Contains("vertical=+0.015m", LagCompensator.NearestMissFor(5, 4, in stale));
        }

        [Fact]
        public void TheNearestBoxWinsAcrossEveryCandidate()
        {
            // Two bodies, both missed; the instrument must name the one the ray came closest to,
            // not the first in the list.
            var compensator = new LagCompensator(new HitboxHistory());

            var targets = new[]
            {
                // Far off to the side.
                new HitscanTarget(5, true, HitboxSet.Humanoid(new Vec3(6f, 0f, 10f))),
                // A hand's width off the shoulder.
                new HitscanTarget(6, true, HitboxSet.Humanoid(new Vec3(0.5f, 0f, 10f))),
            };

            compensator.ResolveHitscan(
                targets, Shooter, new Vec3(0f, 1.25f, 0f), new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.Equal(6, compensator.LastNearestMiss.ActorId);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// The humanoid as it was built before X-24 was fixed: torso and arms stopping at
        /// 1.550 m under a head starting at 1.580 m.
        /// </summary>
        /// <remarks>
        /// A local copy of the OLD constants, deliberately. Measuring the instrument against
        /// today's <see cref="HitboxSet.Humanoid"/> would make these tests pass vacuously the
        /// moment the geometry is fixed — the seam they fire a ray through would no longer exist,
        /// and a green would then mean "there was nothing to measure" rather than "the
        /// measurement works".
        /// </remarks>
        private static HitboxSet PreFixHumanoid(in Vec3 feet)
        {
            float x = feet.X, baseY = feet.Y, z = feet.Z;
            Vec3 At(float y) => new Vec3(x, baseY + y, z);

            return new HitboxSet(
                head: Aabb.FromSize(At(1.70f), new Vec3(0.24f, 0.24f, 0.24f)),
                torso: Aabb.FromSize(At(1.20f), new Vec3(0.50f, 0.70f, 0.32f)),
                arms: Aabb.FromSize(At(1.25f), new Vec3(0.80f, 0.60f, 0.26f)),
                legs: Aabb.FromSize(At(0.45f), new Vec3(0.40f, 0.90f, 0.30f)));
        }
    }
}
