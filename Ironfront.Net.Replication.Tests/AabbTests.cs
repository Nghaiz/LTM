using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The ray/box maths every hit in phase 02 is decided by. Small, and the one place an
    /// error would be invisible everywhere else: a subtly wrong slab test does not crash, it
    /// just makes the hit rate a few percent off in a way no other test would attribute.
    /// </summary>
    public sealed class AabbTests
    {
        private static readonly Aabb UnitBox =
            Aabb.FromSize(new Vec3(0f, 0f, 10f), new Vec3(2f, 2f, 2f));

        [Fact]
        public void ExtentsAreHalfTheSize()
        {
            // Matching UnityEngine.Bounds. Storing full size where Unity stores half would
            // double every hitbox and read as an unaccountably generous hit rate.
            Aabb box = Aabb.FromSize(Vec3.Zero, new Vec3(2f, 4f, 6f));

            Assert.Equal(1f, box.Extents.X, 5);
            Assert.Equal(2f, box.Extents.Y, 5);
            Assert.Equal(3f, box.Extents.Z, 5);
        }

        [Fact]
        public void NegativeExtentsAreNormalized()
        {
            var box = new Aabb(Vec3.Zero, new Vec3(-1f, -2f, -3f));

            Assert.Equal(1f, box.Extents.X, 5);
            Assert.False(box.IsEmpty);
        }

        [Fact]
        public void MinAndMaxBracketTheCentre()
        {
            Assert.Equal(-1f, UnitBox.Min.X, 5);
            Assert.Equal(1f, UnitBox.Max.X, 5);
            Assert.Equal(9f, UnitBox.Min.Z, 5);
            Assert.Equal(11f, UnitBox.Max.Z, 5);
        }

        [Fact]
        public void ARayDownTheCentreHitsAtTheNearFace()
        {
            bool hit = UnitBox.Raycast(
                Vec3.Zero, new Vec3(0f, 0f, 1f), maxDistance: 100f, out float distance);

            Assert.True(hit);
            Assert.Equal(9f, distance, 4);
        }

        [Fact]
        public void ARayPointingAwayMisses()
        {
            Assert.False(UnitBox.Raycast(
                Vec3.Zero, new Vec3(0f, 0f, -1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void ARayStoppingShortMisses()
        {
            Assert.False(UnitBox.Raycast(
                Vec3.Zero, new Vec3(0f, 0f, 1f), maxDistance: 5f, out _));
        }

        [Fact]
        public void ARayStartingInsideHitsAtZeroDistance()
        {
            // A muzzle already overlapping a torso is constant in close quarters, and reporting
            // a miss there would make point-blank shots pass through people.
            bool hit = UnitBox.Raycast(
                new Vec3(0f, 0f, 10f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out float distance);

            Assert.True(hit);
            Assert.Equal(0f, distance, 5);
        }

        [Fact]
        public void ARayParallelToAnAxisIsNotABlindSpot()
        {
            // The zero-direction-component case. Guarding it with a branch instead of letting
            // IEEE division produce infinity is the classic way to make shots straight down a
            // corridor silently miss.
            var wall = Aabb.FromSize(new Vec3(0f, 0f, 10f), new Vec3(100f, 100f, 1f));

            Assert.True(wall.Raycast(
                Vec3.Zero, new Vec3(0f, 0f, 1f), maxDistance: 50f, out float distance));
            Assert.Equal(9.5f, distance, 4);
        }

        [Fact]
        public void ARayGrazingExactlyAlongASlabPlaneDoesNotReturnNaN()
        {
            // Origin sits on the box's own min-Y plane with no Y component: (0 - 0) * infinity
            // is NaN, and every comparison against NaN is false, so an unguarded test keeps an
            // interval it should have rejected.
            var box = Aabb.FromSize(new Vec3(0f, 0f, 10f), new Vec3(2f, 2f, 2f));

            bool hit = box.Raycast(
                new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out float distance);

            Assert.True(hit);
            Assert.False(float.IsNaN(distance));
        }

        [Fact]
        public void ANaNDirectionMissesRatherThanHittingEveryBoxAtZeroDistance()
        {
            // The free-headshot primitive. Every comparison against NaN is false, so an
            // interval test that cannot reject a NaN has to skip the axis — and skipping all
            // three reports a hit at distance 0 on every box of every target. Boxes are ordered
            // head-first and ties keep the first, so the result is a guaranteed headshot on an
            // arbitrary actor at any range. Not reachable from the wire, where aim is quantized
            // and comes back through trig; very reachable from a Unity Transform.forward.
            Assert.False(UnitBox.Raycast(
                Vec3.Zero, new Vec3(float.NaN, 0f, 1f), maxDistance: 100f, out _));
            Assert.False(UnitBox.Raycast(
                new Vec3(float.NaN, 0f, 0f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void AnInfiniteDirectionMisses()
        {
            Assert.False(UnitBox.Raycast(
                Vec3.Zero, new Vec3(float.PositiveInfinity, 0f, 1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void ANaNMaxDistanceMisses()
        {
            Assert.False(UnitBox.Raycast(
                Vec3.Zero, new Vec3(0f, 0f, 1f), maxDistance: float.NaN, out _));
        }

        [Fact]
        public void ARayOnASlabPlaneParallelToItStillResolvesCorrectly()
        {
            // The case that produced the NaN: origin exactly on a slab plane with a zero
            // direction component. Grazing the box's own min-Y face along +Z must still hit,
            // and the same ray one micron below it must still miss.
            var box = Aabb.FromSize(new Vec3(0f, 1f, 10f), new Vec3(2f, 2f, 2f));

            Assert.True(box.Raycast(
                new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out _));
            Assert.False(box.Raycast(
                new Vec3(0f, -0.001f, 0f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void ARayBesideTheBoxMisses()
        {
            Assert.False(UnitBox.Raycast(
                new Vec3(5f, 0f, 0f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void AnEmptyBoxNeverHits()
        {
            var degenerate = new Aabb(new Vec3(0f, 0f, 10f), Vec3.Zero);

            Assert.True(degenerate.IsEmpty);
            Assert.False(degenerate.Raycast(
                Vec3.Zero, new Vec3(0f, 0f, 1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void ExpandGrowsEverySideEqually()
        {
            // The phase-02 contingency lever: drop lag compensation, widen hitboxes ~15%.
            Aabb widened = UnitBox.Expand(0.15f);

            Assert.Equal(1.15f, widened.Extents.X, 4);
            Assert.Equal(UnitBox.Center.Z, widened.Center.Z, 5);
            Assert.True(widened.Raycast(
                new Vec3(1.1f, 0f, 0f), new Vec3(0f, 0f, 1f), maxDistance: 100f, out _));
        }

        [Fact]
        public void ADiagonalRayHitsAtTheCorrectDistance()
        {
            var box = Aabb.FromSize(new Vec3(10f, 0f, 10f), new Vec3(2f, 2f, 2f));
            var direction = new Vec3(1f, 0f, 1f).Normalized;

            Assert.True(box.Raycast(Vec3.Zero, direction, maxDistance: 100f, out float distance));

            // Enters the x=9 slab at t*cos45 = 9, so t = 9 / (1/sqrt2) = 12.728.
            Assert.Equal(12.728f, distance, 2);
        }

        [Fact]
        public void HumanoidHitboxesStackHeadAboveTorsoAboveLegs()
        {
            HitboxSet set = HitboxSet.Humanoid(Vec3.Zero);

            Assert.True(set.Head.Center.Y > set.Torso.Center.Y);
            Assert.True(set.Torso.Center.Y > set.Legs.Center.Y);
            Assert.True(set.Head.Max.Y < 2.0f);   // a person, not a lamppost
            Assert.True(set.Legs.Min.Y >= -0.1f);
        }

        [Fact]
        public void HitboxIndexingMatchesTheDamageClasses()
        {
            Assert.Equal(Protocol.HitboxType.Head, HitboxSet.TypeOf(0));
            Assert.Equal(Protocol.HitboxType.Body, HitboxSet.TypeOf(1));

            // Arms and legs both report Limb: the wire enum has three values, not four, so the
            // server must not claim a distinction the client cannot receive.
            Assert.Equal(Protocol.HitboxType.Limb, HitboxSet.TypeOf(2));
            Assert.Equal(Protocol.HitboxType.Limb, HitboxSet.TypeOf(3));
        }

        [Fact]
        public void TranslatingAHitboxSetMovesEveryBox()
        {
            HitboxSet set = HitboxSet.Humanoid(Vec3.Zero);
            HitboxSet moved = set.Translated(new Vec3(5f, 0f, 0f));

            Assert.Equal(set.Head.Center.X + 5f, moved.Head.Center.X, 4);
            Assert.Equal(set.Legs.Center.X + 5f, moved.Legs.Center.X, 4);
            Assert.Equal(set.Torso.Extents.Y, moved.Torso.Extents.Y, 5);
        }
    }
}
