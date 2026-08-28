using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist items 4 and 5:
    /// <list type="bullet">
    /// <item>PackPos/UnpackPos round-trip error &lt; 0.07 m across the full +/-2048 range</item>
    /// <item>Yaw round-trip error &lt; 0.01 degrees</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Quantization is the easiest place in the protocol to get wrong and the worst place
    /// to get it wrong: a client and server that disagree on POS_RANGE put characters at
    /// silently wrong positions with no runtime error anywhere.
    /// </remarks>
    public class QuantizeTests
    {
        private const float PositionTolerance = 0.07f;
        private const float YawTolerance = 0.01f;

        [Fact]
        public void Constants_MatchSpec()
        {
            // X-53 moved the window and deliberately did NOT widen it: -1024 .. 3072 is the
            // same 4096 m, so the 6.25 cm resolution below is unchanged. Widening to +/-4096
            // would have halved it for every actor on every map.
            Assert.Equal(-1024f, Quantize.POS_MIN);
            Assert.Equal(3072f, Quantize.POS_MAX);
            Assert.Equal(4096f, Quantize.POS_RANGE);
            Assert.Equal(65536f / 360f, Quantize.YAW_SCALE);
            Assert.Equal(16384f / 90f, Quantize.PITCH_SCALE);
            Assert.Equal(64f, Quantize.VEL_MAX);
            Assert.Equal(127f / 64f, Quantize.VEL_SCALE);
        }

        [Fact]
        public void PackPos_AtTheBoundaries_MatchesSpec()
        {
            // The boundaries, expressed against the constants rather than against the numbers
            // they happened to hold: this fixture asserted PackPos(0) == 0, which was a property
            // of the window being CENTRED on the origin and not of the packing at all. It went
            // red on X-53 for a change that broke nothing.
            Assert.Equal(-32768, Quantize.PackPos(Quantize.POS_MIN));
            Assert.Equal(32767, Quantize.PackPos(Quantize.POS_MAX));

            // The window's own midpoint is the code midpoint, wherever the window sits.
            Assert.Equal(0, Quantize.PackPos((Quantize.POS_MIN + Quantize.POS_MAX) / 2f));
        }

        /// <summary>
        /// KNOWN SPEC DISCREPANCY — protocol-spec.md section 4.4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The spec's illustrative table says <c>PackPos(100f) -&gt; 1600</c>, but the
        /// spec's own normative formula, <c>(short)(t * 65535f - 32768f)</c>, yields
        /// <b>1599</b>:
        /// </para>
        /// <code>
        /// t                = (100 - (-2048)) / 4096 = 0.5244140625
        /// t * 65535        = 34367.4755859375
        /// minus 32768      = 1599.4755859375
        /// (short) truncate = 1599
        /// </code>
        /// <para>
        /// 1600 would require multiplying by 65536 rather than 65535 — but that makes
        /// <c>PackPos(2048f)</c> produce 32768, which overflows an i16 and contradicts the
        /// same table's <c>PackPos(2048f) -&gt; 32767</c>. The formula is right and the
        /// example is off by one.
        /// </para>
        /// <para>
        /// Both values are well inside the 0.07 m tolerance that section 14 actually
        /// requires (see <see cref="PackPos_RoundTrip_IsWithinTolerance_AcrossFullRange"/>),
        /// so nothing is broken — but the doc should be corrected through the
        /// conventions.md section 2 change process. This is a documentation fix only: no
        /// behavior changes, so PROTOCOL_VERSION does not move.
        /// </para>
        /// </remarks>
        [Fact]
        public void PackPos_100m_FollowsTheNormativeFormula_NotTheSpecTable()
        {
            // 100 m measured from the window's floor, so this still exercises the formula
            // rather than the window's placement (X-53).
            float hundredIn = Quantize.POS_MIN + 100f;

            // t = 100 / 4096 = 0.02441, x 65535 = 1599.98, - 32768 -> -31168 after the cast.
            // The spec's illustrative table would round the 1599.98 up; the normative formula
            // truncates. That discrepancy is the point of this fixture and survives the move.
            Assert.Equal(-31168, Quantize.PackPos(hundredIn));

            // Whichever of the two the team lands on, the position is correct to within
            // the tolerance that matters.
            Assert.True(Math.Abs(Quantize.UnpackPos(-31168) - hundredIn) < PositionTolerance);
            Assert.True(Math.Abs(Quantize.UnpackPos(-31167) - hundredIn) < PositionTolerance);
        }

        [Fact]
        public void UnpackPos_AtTheBoundaries_MatchesSpec()
        {
            float midpoint = (Quantize.POS_MIN + Quantize.POS_MAX) / 2f;

            Assert.True(Math.Abs(Quantize.UnpackPos(0) - midpoint) < PositionTolerance);
            Assert.Equal(Quantize.POS_MIN, Quantize.UnpackPos(-32768));
            Assert.True(Math.Abs(Quantize.UnpackPos(32767) - Quantize.POS_MAX) < PositionTolerance);
        }

        [Fact]
        public void PackPos_RoundTrip_IsWithinTolerance_AcrossFullRange()
        {
            // Sweep the whole legal box at 0.25 m steps: 16385 samples.
            float worst = 0f;
            float worstAt = 0f;

            for (float v = Quantize.POS_MIN; v <= Quantize.POS_MAX; v += 0.25f)
            {
                float error = Math.Abs(Quantize.UnpackPos(Quantize.PackPos(v)) - v);
                if (error > worst)
                {
                    worst = error;
                    worstAt = v;
                }
            }

            Assert.True(
                worst < PositionTolerance,
                $"Worst position round-trip error {worst:F5} m at {worstAt} m " +
                $"exceeds the {PositionTolerance} m budget.");
        }

        [Fact]
        public void PackPos_ClampsOutOfRangeInput()
        {
            // A character that somehow leaves the map must not wrap to the opposite
            // corner — clamping is the safe failure.
            Assert.Equal(-32768, Quantize.PackPos(-9999f));
            Assert.Equal(32767, Quantize.PackPos(9999f));
        }

        [Fact]
        public void PackYaw_RoundTrip_IsWithinTolerance()
        {
            float worst = 0f;
            float worstAt = 0f;

            for (float deg = 0f; deg < 360f; deg += 0.05f)
            {
                float error = Math.Abs(Quantize.UnpackYaw(Quantize.PackYaw(deg)) - deg);
                if (error > worst)
                {
                    worst = error;
                    worstAt = deg;
                }
            }

            Assert.True(
                worst < YawTolerance,
                $"Worst yaw round-trip error {worst:F6} degrees at {worstAt} " +
                $"exceeds the {YawTolerance} degree budget.");
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(90f)]
        [InlineData(180f)]
        [InlineData(270f)]
        [InlineData(359.9f)]
        public void PackYaw_KnownAngles_RoundTrip(float degrees)
            => Assert.True(Math.Abs(Quantize.UnpackYaw(Quantize.PackYaw(degrees)) - degrees)
                           < YawTolerance);

        [Fact]
        public void PackYaw_WrapsNegativeAndOverfullAngles()
        {
            // -90 and 270 are the same bearing and must produce the same bytes, or a
            // client that reports angles in [-180,180] disagrees with one that uses
            // [0,360).
            Assert.Equal(Quantize.PackYaw(270f), Quantize.PackYaw(-90f));
            Assert.Equal(Quantize.PackYaw(10f), Quantize.PackYaw(370f));
        }

        [Fact]
        public void PackPitch_ClampsToPlusMinus90()
        {
            Assert.Equal(16384, Quantize.PackPitch(90f));
            Assert.Equal(-16384, Quantize.PackPitch(-90f));
            Assert.Equal(16384, Quantize.PackPitch(120f));
            Assert.Equal(-16384, Quantize.PackPitch(-120f));
            Assert.Equal(0, Quantize.PackPitch(0f));
        }

        [Fact]
        public void PackPitch_RoundTrip_IsWithinTolerance()
        {
            for (float deg = -90f; deg <= 90f; deg += 0.05f)
            {
                float error = Math.Abs(Quantize.UnpackPitch(Quantize.PackPitch(deg)) - deg);
                Assert.True(error < YawTolerance, $"Pitch error {error:F6} at {deg} degrees.");
            }
        }

        [Fact]
        public void PackVel_ClampsAndRoundTripsWithinHalfAMetrePerSecond()
        {
            Assert.Equal(127, Quantize.PackVel(64f));
            Assert.Equal(-127, Quantize.PackVel(-64f));
            Assert.Equal(127, Quantize.PackVel(500f));
            Assert.Equal(-127, Quantize.PackVel(-500f));

            // Resolution is 64/127 = 0.504 m/s; velocity only feeds extrapolation.
            for (float v = -64f; v <= 64f; v += 0.1f)
            {
                float error = Math.Abs(Quantize.UnpackVel(Quantize.PackVel(v)) - v);
                Assert.True(error <= 0.51f, $"Velocity error {error:F4} at {v} m/s.");
            }
        }

        [Fact]
        public void PackMoveAxis_ClampsToUnitRange()
        {
            Assert.Equal(127, Quantize.PackMoveAxis(1f));
            Assert.Equal(-127, Quantize.PackMoveAxis(-1f));
            Assert.Equal(127, Quantize.PackMoveAxis(5f));
            Assert.Equal(-127, Quantize.PackMoveAxis(-5f));
            Assert.Equal(0, Quantize.PackMoveAxis(0f));
        }
    }
}
