using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the occlusion line the shot log prints for ledger row <b>X-20</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The measurement, not the fix. The 2026-08-23 run was the first in which any shot reached
    /// a hitbox at all — <c>resolved=30 occluded=20 hits=0</c>, victim on 100 health — and two
    /// readings survive it: either the linecast is right and there is geometry between the pair
    /// (they held at 10.1 m against a programmed 6.0 m), or the endpoint lands inside the
    /// victim's OWN capsule, which mask <c>-2049</c> does not exclude.
    /// </para>
    /// <para>
    /// <c>Physics.Linecast</c>'s bool overload discarded the one fact that separates them, so no
    /// artifact could. <c>ServerTickLoop.DescribeOcclusion</c> is the pure half of the fix and
    /// this pins its shape; the <c>Physics</c> call itself is not tested here and could not
    /// usefully be, which is exactly why the formatting was extracted.
    /// </para>
    /// </remarks>
    public sealed class OcclusionDescriptionTests
    {
        [Test]
        public void AMidRayHitPrintsAFractionWellBelowOne()
        {
            // Reading 1: a wall stands between the two players.
            string line = ServerTickLoop.DescribeOcclusion("Building_04", 0, 3.5f, 10f);

            Assert.That(line, Does.Contain("frac=0.350"));
            Assert.That(line, Does.Contain("d=3.50m of 10.00m"));
        }

        [Test]
        public void AHitAtTheEndpointPrintsAFractionNearOne()
        {
            // Reading 2: the victim's own capsule blocked the shot that hit it. A 0.5 m radius
            // at 10 m is the shape this produces, and it is what tells the two readings apart
            // when read beside the collider name.
            string line = ServerTickLoop.DescribeOcclusion("PlayerCapsule", 10, 9.5f, 10f);

            Assert.That(line, Does.Contain("frac=0.950"));
        }

        [Test]
        public void TheColliderNameSurvivesVerbatim()
        {
            // The name IS the discriminator - the victim's own body versus terrain or a
            // building. Truncating or normalising it would throw away the answer.
            string line = ServerTickLoop.DescribeOcclusion("Dustbowl_Terrain_NE", 8, 1f, 2f);

            Assert.That(line, Does.Contain("collider=Dustbowl_Terrain_NE"));
            Assert.That(line, Does.Contain("layer=8"));
        }

        [Test]
        public void AZeroLengthRayPrintsNoFractionRatherThanNaN()
        {
            // A NaN or an infinity in the one artifact that is supposed to settle X-20 would be
            // worse than the line saying it cannot tell.
            string line = ServerTickLoop.DescribeOcclusion("Whatever", 0, 0f, 0f);

            Assert.That(line, Does.Contain("frac=n/a"));
            Assert.That(line, Does.Not.Contain("NaN"));
            Assert.That(line, Does.Not.Contain("Infinity"));
        }

        [Test]
        public void AnUnnamedColliderIsNamedRatherThanLeftBlank()
        {
            // "collider= layer=0" reads as a formatting bug and would send the next reader
            // looking in the wrong place.
            Assert.That(
                ServerTickLoop.DescribeOcclusion(null, 0, 1f, 2f), Does.Contain("collider=<unnamed>"));
            Assert.That(
                ServerTickLoop.DescribeOcclusion("", 0, 1f, 2f), Does.Contain("collider=<unnamed>"));
        }

        // ---- freshness: the description must belong to THIS shot ------------------------

        [Test]
        public void AShotThatNothingBlockedSaysSoRatherThanReprintingTheLastOne()
        {
            // The counter did not move, so no linecast wrote a description for this shot. The
            // previous shot's collider is still sitting in LastOcclusion, and printing it would
            // read as a wall blocking a shot that nothing blocked - in the single artifact this
            // whole row exists to make readable.
            Assert.AreEqual(
                "none-this-shot",
                ServerTickLoop.OcclusionFor(7L, 7L, "collider=Building_04 layer=0 d=3.50m of 10.00m frac=0.350"));
        }

        [Test]
        public void AShotThatWasBlockedPrintsTheDescription()
        {
            const string described = "collider=PlayerCapsule layer=10 d=9.50m of 10.00m frac=0.950";

            Assert.AreEqual(described, ServerTickLoop.OcclusionFor(8L, 7L, described));
        }

        [Test]
        public void ManyOcclusionsInOneShotStillCountAsThisShot()
        {
            // The compensator may test several candidates for one trigger frame, so the counter
            // can jump by more than one. Only "did it move" is the question.
            Assert.AreEqual("d", ServerTickLoop.OcclusionFor(12L, 7L, "d"));
        }

        [Test]
        public void ACounterThatWentBackwardsIsNotTreatedAsFresh()
        {
            // LagCompensator.ResetCounters sets ShotsOccluded to 0. A shot logged after that
            // must not resurrect the description recorded before it.
            Assert.AreEqual("none-this-shot", ServerTickLoop.OcclusionFor(0L, 7L, "stale"));
        }

        [Test]
        public void NothingHasBlockedAShotUntilSomethingHas()
        {
            // The default must not read as a measurement. "none" in an artifact means the
            // linecast never rejected anything, which is itself a finding.
            Assert.AreEqual("none", ServerTickLoop.LastOcclusion);
        }
    }
}
