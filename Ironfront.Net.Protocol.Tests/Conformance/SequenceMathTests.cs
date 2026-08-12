using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist items 2 and 3:
    /// <list type="bullet">
    /// <item>IsNewer(0, 65535) = true; IsNewer(65535, 0) = false</item>
    /// <item>IsNewer(5, 65530) = true (wrapped)</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The bug these guard against only appears after ~36 minutes of uptime at 30 Hz, so
    /// it will never show up in a short manual test — which is exactly why the boundary
    /// cases are mandatory rather than nice-to-have (protocol-spec.md section 2.3).
    /// </remarks>
    public class SequenceMathTests
    {
        [Fact]
        public void IsNewer_AcrossWrapBoundary_MatchesSpec()
        {
            // The two cases the spec calls out by name.
            Assert.True(SequenceMath.IsNewer(0, 65535));
            Assert.False(SequenceMath.IsNewer(65535, 0));
        }

        [Fact]
        public void IsNewer_WrappedByFive_IsNewer()
        {
            Assert.True(SequenceMath.IsNewer(5, 65530));
            Assert.False(SequenceMath.IsNewer(65530, 5));
        }

        [Theory]
        // Ordinary, no wrap involved.
        [InlineData(1, 0, true)]
        [InlineData(0, 1, false)]
        [InlineData(1000, 999, true)]
        [InlineData(999, 1000, false)]
        // Straddling the wrap.
        [InlineData(0, 65535, true)]
        [InlineData(65535, 0, false)]
        [InlineData(5, 65530, true)]
        [InlineData(65530, 5, false)]
        [InlineData(2, 65534, true)]
        [InlineData(65534, 2, false)]
        public void IsNewer_Cases(ushort a, ushort b, bool expected)
            => Assert.Equal(expected, SequenceMath.IsNewer(a, b));

        [Fact]
        public void IsNewer_Equal_IsFalse()
        {
            // Equal is not "newer" — a packet is not newer than itself, which matters for
            // the duplicate-input check on the server.
            Assert.False(SequenceMath.IsNewer(0, 0));
            Assert.False(SequenceMath.IsNewer(12345, 12345));
            Assert.False(SequenceMath.IsNewer(65535, 65535));
        }

        [Fact]
        public void IsNewer_IsAntisymmetric_AcrossTheWholeRange()
        {
            // For every pair that is not equal and not exactly half the space apart,
            // exactly one direction must be "newer". A naive '>' fails this at the wrap.
            for (int a = 0; a < 65536; a += 997)          // 997 is prime: hits varied residues
            {
                for (int delta = 1; delta < 32768; delta += 991)
                {
                    ushort x = (ushort)a;
                    ushort y = (ushort)(a + delta);
                    Assert.True(SequenceMath.IsNewer(y, x));
                    Assert.False(SequenceMath.IsNewer(x, y));
                }
            }
        }

        [Theory]
        [InlineData(0, 65535, 1)]     // wrapped forward by one
        [InlineData(65535, 0, -1)]
        [InlineData(5, 65530, 11)]
        [InlineData(1000, 990, 10)]
        [InlineData(990, 1000, -10)]
        public void Distance_IsSignedAndWrapCorrect(ushort a, ushort b, int expected)
            => Assert.Equal(expected, SequenceMath.Distance(a, b));

        [Fact]
        public void IsNewerOrEqual_IncludesEquality()
        {
            Assert.True(SequenceMath.IsNewerOrEqual(7, 7));
            Assert.True(SequenceMath.IsNewerOrEqual(8, 7));
            Assert.False(SequenceMath.IsNewerOrEqual(6, 7));
            Assert.True(SequenceMath.IsNewerOrEqual(0, 65535));
        }
    }
}
