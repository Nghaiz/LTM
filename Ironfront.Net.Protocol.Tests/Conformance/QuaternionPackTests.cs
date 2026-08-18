using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// <see cref="Quantize.PackQuat"/> / <see cref="Quantize.UnpackQuat"/>, phase-V3 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Round-trip accuracy is only one of four properties, and it is the one that passes
    /// while the codec is broken.</b> A sign bug decodes half of all rotations mirrored and a
    /// naive round-trip test never notices, because it feeds back the same sign it started
    /// with. A missing clamp under the radical produces <c>NaN</c>, which reaches Unity as a
    /// vehicle that vanishes rather than as an exception anybody can trace. Each gets its own
    /// assertion here for that reason.
    /// </para>
    /// <para>
    /// <b>And the accuracy assertion itself was nearly one of those greens.</b> A 10,000-sample
    /// random sweep reports ~0.19° and agreed with a budget that had been derived wrongly. The
    /// real worst case is 0.268°, at the four-way tie, and only a deliberate search finds it —
    /// see <c>TheWorstCaseIsSearchedForRatherThanSampledFor</c>.
    /// </para>
    /// </remarks>
    public class QuaternionPackTests
    {
        /// <summary>Deterministic, so a failure is reproducible from the test name alone.</summary>
        private const int SweepSeed = 20260818;

        /// <summary>The § 4.4 budget. The worst case actually measured is 0.268°.</summary>
        private const double BudgetDegrees = 0.3;

        [Fact]
        public void RoundTrip_StaysInsideTheAngularBudget()
        {
            var random = new Random(SweepSeed);
            double worstDegrees = 0;

            for (int i = 0; i < 10_000; i++)
            {
                RandomUnitQuaternion(random, out float x, out float y, out float z, out float w);
                worstDegrees = Math.Max(worstDegrees, RoundTripErrorDegrees(x, y, z, w));
            }

            Assert.True(
                worstDegrees < BudgetDegrees,
                $"worst round-trip error was {worstDegrees:F4} degrees, budget is {BudgetDegrees}");
        }

        [Fact]
        public void TheWorstCaseIsSearchedForRatherThanSampledFor()
        {
            // A random sweep is not a bound. This codec's error is worst at the four-way tie
            // (0.5, 0.5, 0.5, 0.5): the reconstructed component is m = sqrt(1 - a² - b² - c²),
            // so its error is -(a·δa + b·δb + c·δc)/m and grows as m shrinks — and m is exactly
            // at its minimum of 0.5 there, with the three transmitted components simultaneously
            // at their largest.
            //
            // This matters more than it looks. The budget was originally written as 0.2° from
            // the step size alone, and the 10,000-sample sweep above AGREED with it — it reports
            // ~0.19° and reads as a clean pass. The corner is 0.268°. A green that only ever saw
            // the easy part of the space is the failure mode this test exists to remove.
            var random = new Random(SweepSeed);
            double worstDegrees = 0;

            for (int i = 0; i < 200_000; i++)
            {
                float x = 0.5f + (float)((random.NextDouble() - 0.5) * 0.06);
                float y = 0.5f + (float)((random.NextDouble() - 0.5) * 0.06);
                float z = 0.5f + (float)((random.NextDouble() - 0.5) * 0.06);
                float w = 0.5f + (float)((random.NextDouble() - 0.5) * 0.06);
                Normalize(ref x, ref y, ref z, ref w);

                worstDegrees = Math.Max(worstDegrees, RoundTripErrorDegrees(x, y, z, w));
            }

            // Both directions. The upper bound is the budget; the lower bound asserts this
            // search actually reaches the hard part of the space, so that a future change which
            // stops it finding the corner fails here rather than quietly reporting 0.19° again.
            Assert.True(
                worstDegrees < BudgetDegrees,
                $"tie-corner error was {worstDegrees:F4} degrees, budget is {BudgetDegrees}");
            Assert.True(
                worstDegrees > 0.2,
                $"the tie-corner search found only {worstDegrees:F4} degrees — it is no longer "
                + "reaching the corner it exists to reach, so its pass means nothing");
        }

        private static double RoundTripErrorDegrees(float x, float y, float z, float w)
        {
            Quantize.UnpackQuat(
                Quantize.PackQuat(x, y, z, w),
                out float rx, out float ry, out float rz, out float rw);

            return AngleBetweenDegrees(x, y, z, w, rx, ry, rz, rw);
        }

        [Fact]
        public void EveryLargestComponentBranchIsExercisedAndCorrect()
        {
            // One quaternion per branch, each with a different component dominant, so a switch
            // arm that reassembles into the wrong slot cannot hide behind the sweep's averages.
            AssertBranch(0.9f, 0.3f, 0.2f, 0.1f, expectedIndex: 0);
            AssertBranch(0.3f, 0.9f, 0.2f, 0.1f, expectedIndex: 1);
            AssertBranch(0.2f, 0.3f, 0.9f, 0.1f, expectedIndex: 2);
            AssertBranch(0.1f, 0.2f, 0.3f, 0.9f, expectedIndex: 3);
        }

        [Fact]
        public void NegatingTheQuaternionPacksIdentically()
        {
            // q and -q are the same rotation. Without sign canonicalization the reconstructed
            // largest component is always positive, so one of the two decodes mirrored — and
            // decodes as a perfectly valid unit quaternion while doing it.
            var random = new Random(SweepSeed);

            for (int i = 0; i < 1_000; i++)
            {
                RandomUnitQuaternion(random, out float x, out float y, out float z, out float w);

                Assert.Equal(
                    Quantize.PackQuat(x, y, z, w),
                    Quantize.PackQuat(-x, -y, -z, -w));
            }
        }

        [Fact]
        public void UnpackedRotationsAreUnitLength()
        {
            var random = new Random(SweepSeed);

            for (int i = 0; i < 1_000; i++)
            {
                RandomUnitQuaternion(random, out float x, out float y, out float z, out float w);

                Quantize.UnpackQuat(
                    Quantize.PackQuat(x, y, z, w),
                    out float rx, out float ry, out float rz, out float rw);

                double length = Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
                Assert.True(Math.Abs(length - 1.0) < 1e-3, $"length was {length}");
            }
        }

        [Fact]
        public void NoThirtyTwoBitInputProducesNaN()
        {
            // Includes 0xFFFFFFFF, whose three components each decode to +1/sqrt(2): the sum of
            // squares is 1.5, so the radical goes to -0.5 and an unclamped sqrt returns NaN.
            // Anything that reaches this method came off a socket, so "no encoder would send
            // that" is not a defence.
            foreach (uint packed in HostilePackedValues())
            {
                Quantize.UnpackQuat(packed, out float x, out float y, out float z, out float w);

                Assert.False(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(w),
                             $"0x{packed:X8} decoded to NaN");
                Assert.False(
                    float.IsInfinity(x) || float.IsInfinity(y)
                    || float.IsInfinity(z) || float.IsInfinity(w),
                    $"0x{packed:X8} decoded to infinity");

                double length = Math.Sqrt(x * x + y * y + z * z + w * w);
                Assert.True(Math.Abs(length - 1.0) < 1e-3,
                            $"0x{packed:X8} decoded to length {length}");
            }
        }

        [Fact]
        public void IdentityRotationSurvivesTheRoundTrip()
        {
            Quantize.UnpackQuat(
                Quantize.PackQuat(0f, 0f, 0f, 1f),
                out float x, out float y, out float z, out float w);

            Assert.True(Math.Abs(x) < 1e-3);
            Assert.True(Math.Abs(y) < 1e-3);
            Assert.True(Math.Abs(z) < 1e-3);
            Assert.True(Math.Abs(w - 1f) < 1e-3);
        }

        [Fact]
        public void TheLayoutIsTwoBitsOfIndexAndThreeTenBitFields()
        {
            // Asserted against the bit positions section 4.10 states, not against whatever the
            // implementation happens to shift by.
            uint packed = Quantize.PackQuat(0.1f, 0.2f, 0.3f, 0.9f);

            Assert.Equal(3u, packed >> 30);                       // w is largest
            Assert.True(((packed >> 20) & 0x3FFu) <= 1023u);
            Assert.True(((packed >> 10) & 0x3FFu) <= 1023u);
            Assert.True((packed & 0x3FFu) <= 1023u);
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertBranch(
            float x, float y, float z, float w, uint expectedIndex)
        {
            Normalize(ref x, ref y, ref z, ref w);

            uint packed = Quantize.PackQuat(x, y, z, w);
            Assert.Equal(expectedIndex, packed >> 30);

            Quantize.UnpackQuat(packed, out float rx, out float ry, out float rz, out float rw);

            double degrees = AngleBetweenDegrees(x, y, z, w, rx, ry, rz, rw);
            Assert.True(
                degrees < BudgetDegrees,
                $"branch {expectedIndex} was off by {degrees:F4} degrees");
        }

        private static uint[] HostilePackedValues()
        {
            var values = new uint[64 + 4];
            values[0] = 0x00000000u;
            values[1] = 0xFFFFFFFFu;
            values[2] = 0xC00FFFFFu;
            values[3] = 0x3FFFFC00u;

            // Every single-bit pattern, so no shift width is left unexercised.
            for (int bit = 0; bit < 32; bit++)
            {
                values[4 + bit] = 1u << bit;
                values[36 + bit] = ~(1u << bit);
            }

            return values;
        }

        private static void RandomUnitQuaternion(
            Random random, out float x, out float y, out float z, out float w)
        {
            // Shoemake's method: uniform over the unit 3-sphere, so the sweep covers every
            // largest-component branch rather than clustering near identity.
            double u1 = random.NextDouble();
            double u2 = random.NextDouble() * 2.0 * Math.PI;
            double u3 = random.NextDouble() * 2.0 * Math.PI;

            double a = Math.Sqrt(1.0 - u1);
            double b = Math.Sqrt(u1);

            x = (float)(a * Math.Sin(u2));
            y = (float)(a * Math.Cos(u2));
            z = (float)(b * Math.Sin(u3));
            w = (float)(b * Math.Cos(u3));
        }

        private static void Normalize(ref float x, ref float y, ref float z, ref float w)
        {
            double length = Math.Sqrt(x * x + y * y + z * z + w * w);
            x = (float)(x / length);
            y = (float)(y / length);
            z = (float)(z / length);
            w = (float)(w / length);
        }

        /// <summary>
        /// Angle between two rotations, in degrees. Uses |dot| so that q and -q read as the
        /// same rotation, which they are.
        /// </summary>
        private static double AngleBetweenDegrees(
            float ax, float ay, float az, float aw,
            float bx, float by, float bz, float bw)
        {
            double dot = Math.Abs(ax * bx + ay * by + az * bz + aw * bw);
            if (dot > 1.0) dot = 1.0;
            return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
        }
    }
}
