using System;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase P32: vehicles and supplies scattered at random each match, never two in one place.
    /// </summary>
    public class FieldSupplyLayoutTests
    {
        [Fact]
        public void AZeroWeight_IsNeverChosen_AndNoWeightChoosesNothing()
        {
            var random = new Random(7);
            float[] weights = { 0f, 3f, -1f, 1f };
            for (int i = 0; i < 2000; i++)
            {
                int pick = FieldSupplyLayout.Weighted(weights, random);
                Assert.True(pick == 1 || pick == 3, $"picked {pick}");
            }

            Assert.Equal(-1, FieldSupplyLayout.Weighted(new[] { 0f, -2f }, random));
            Assert.Equal(-1, FieldSupplyLayout.Weighted(ReadOnlySpan<float>.Empty, random));
        }

        [Fact]
        public void WeightsAreHonouredInProportion()
        {
            var random = new Random(11);
            float[] weights = { 3f, 1f };
            int first = 0;
            const int rolls = 20000;
            for (int i = 0; i < rolls; i++)
            {
                if (FieldSupplyLayout.Weighted(weights, random) == 0) first++;
            }

            Assert.InRange(first / (double)rolls, 0.73, 0.77);
        }

        [Fact]
        public void APickIsNeverCloserThanTheSpacingToAnythingTaken()
        {
            var random = new Random(3);
            var candidates = new Vec3[200];
            for (int i = 0; i < candidates.Length; i++)
            {
                candidates[i] = new Vec3((float)random.NextDouble() * 1000f, 50f, (float)random.NextDouble() * 1000f);
            }

            var taken = new Vec3[12];
            int count = 0;
            for (int n = 0; n < taken.Length; n++)
            {
                int pick = FieldSupplyLayout.PickSpread(candidates, new ReadOnlySpan<Vec3>(taken, 0, count), 150f, random);
                if (pick < 0) break;
                taken[count++] = candidates[pick];
            }

            Assert.True(count >= 6, $"only {count} spread picks fitted in a 1 km square");
            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    float dx = taken[a].X - taken[b].X, dz = taken[a].Z - taken[b].Z;
                    Assert.True(dx * dx + dz * dz >= 150f * 150f);
                }
            }
        }

        [Fact]
        public void HeightDoesNotCountTowardTheSpacing()
        {
            Vec3[] candidates = { new Vec3(0f, 200f, 0f) };
            Vec3[] taken = { new Vec3(0f, 0f, 10f) };
            Assert.Equal(-1, FieldSupplyLayout.PickSpread(candidates, taken, 50f, new Random(1)));
        }

        [Fact]
        public void TheSameSeedGivesTheSameLayout_AndAnotherMatchAnother()
        {
            Vec3[] candidates = new Vec3[50];
            for (int i = 0; i < candidates.Length; i++) candidates[i] = new Vec3(i * 40f, 0f, 0f);

            int seedA = FieldSupplyLayout.MatchSeed(638000000000000000L, 1, 5);
            int seedB = FieldSupplyLayout.MatchSeed(638000000000000000L, 2, 5);
            Assert.NotEqual(seedA, seedB);

            Assert.Equal(
                FieldSupplyLayout.PickSpread(candidates, ReadOnlySpan<Vec3>.Empty, 0f, new Random(seedA)),
                FieldSupplyLayout.PickSpread(candidates, ReadOnlySpan<Vec3>.Empty, 0f, new Random(seedA)));
        }
    }
}
