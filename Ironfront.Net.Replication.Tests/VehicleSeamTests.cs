using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Behavioural tests over the engine-free vehicle seam. Unlike
    /// <see cref="VehicleSourceInvariantTests"/>, these run the real arithmetic and prove
    /// behaviour rather than shape.
    /// </summary>
    public sealed class VehicleSeamTests
    {
        // ------------------------------------------------------------------ turret slew

        /// <summary>
        /// Design-doc acceptance criterion 4, graded in CI rather than by two Editor sessions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why 90 deg/s rather than the shipped 300.</b> The tolerance is about the
        /// algorithm being dt-scaled, but the measurement also carries float summation noise
        /// that grows with rate x steps: at 300 deg/s over 144 steps that noise alone can
        /// approach 1e-3, so a 1e-4 assertion there would be measuring float addition, not the
        /// integrator. At 90 deg/s the per-step delta is exactly representable at 144 Hz
        /// (0.625) and the noise sits two orders below the tolerance. The shipped rate is
        /// covered immediately below at a tolerance sized to its own accumulation.
        /// </para>
        /// </remarks>
        [Fact]
        public void TurretSlewIsFramerateIndependent()
        {
            const float Rate = 90f;

            float low = TraverseForOneSecond(Rate, 1f / 30f, 30);
            float high = TraverseForOneSecond(Rate, 1f / 144f, 144);

            Assert.Equal(Rate, low, 4);
            Assert.Equal(Rate, high, 4);
            Assert.True(System.Math.Abs(low - high) < 1e-4f, $"30 Hz traversed {low}, 144 Hz traversed {high}");
        }

        /// <summary>
        /// The same property at both rates the turrets actually ship with — TankTurret's 300
        /// and MountedTurret's 600 — so the prefab defaults are covered and not just the
        /// mechanism.
        /// </summary>
        /// <remarks>
        /// Asserted as a RELATIVE bound rather than criterion 4's absolute 1e-4, because
        /// neither 300/144 nor 600/144 is exactly representable and the residual here is float
        /// summation, not framerate. 1e-6 relative is four orders below the 2.4x divergence the
        /// phase removed. The deviation from the criterion's literal wording is recorded in the
        /// phase file § 7.
        /// </remarks>
        [Theory]
        [InlineData(300f)]
        [InlineData(600f)]
        public void TurretSlewIsFramerateIndependentAtTheShippedRates(float rate)
        {
            float low = TraverseForOneSecond(rate, 1f / 30f, 30);
            float high = TraverseForOneSecond(rate, 1f / 144f, 144);

            float relative = System.Math.Abs(low - high) / rate;
            Assert.True(relative < 1e-6f,
                $"{rate} deg/s: 30 Hz traversed {low}, 144 Hz traversed {high} (relative {relative}).");
        }

        /// <summary>
        /// The property D3 exists for: the field is the source of truth, so setting it and
        /// stepping with no elapsed time leaves it exactly as set. An accumulate-into-the-joint
        /// design cannot honour this -- it round-trips through Quaternion.eulerAngles, which is
        /// not injective.
        /// </summary>
        [Fact]
        public void TurretAimIsDrivenFromTheFieldNotAccumulated()
        {
            TurretAimLimits limits = Limits(300f, -10f, 20f);
            TurretAimState state = default;

            state.Yaw = 137.25f;
            state.Pitch = -3.5f;

            TurretAimCore.Step(ref state, 1f, 1f, in limits, 0f);

            Assert.Equal(137.25f, state.Yaw);
            Assert.Equal(-3.5f, state.Pitch);
        }

        [Fact]
        public void TurretPitchClampsToTheLimits()
        {
            TurretAimLimits limits = Limits(600f, -40f, 15f);
            TurretAimState state = default;

            for (int i = 0; i < 200; i++)
                TurretAimCore.Step(ref state, 0f, 1f, in limits, 1f / 60f);
            Assert.Equal(15f, state.Pitch);

            for (int i = 0; i < 400; i++)
                TurretAimCore.Step(ref state, 0f, -1f, in limits, 1f / 60f);
            Assert.Equal(-40f, state.Pitch);
        }

        [Fact]
        public void TurretYawWrapsWithoutADiscontinuity()
        {
            TurretAimLimits limits = Limits(360f, -40f, 15f);
            TurretAimState state = default;
            state.Yaw = 359f;

            // +2 degrees: crosses 360 and lands just past zero, not at 361.
            TurretAimCore.Step(ref state, 1f, 0f, in limits, 2f / 360f);
            Assert.Equal(1f, state.Yaw, 4);

            // And back the other way, through zero into the high end of the range.
            state.Yaw = 1f;
            TurretAimCore.Step(ref state, -1f, 0f, in limits, 2f / 360f);
            Assert.Equal(359f, state.Yaw, 4);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(360f, 0f)]
        [InlineData(-1f, 359f)]
        [InlineData(720.5f, 0.5f)]
        [InlineData(-0.0000001f, 0f)]   // the reason WrapDegrees re-tests >= 360 at the end
        public void WrapDegreesStaysInTheHalfOpenRange(float input, float expected)
        {
            float wrapped = TurretAimCore.WrapDegrees(input);

            Assert.Equal(expected, wrapped, 4);
            Assert.True(wrapped >= 0f && wrapped < 360f, $"{input} wrapped to {wrapped}");
        }

        /// <summary>
        /// A NaN axis must not be able to poison the authoritative aim. Mathf.Clamp would have
        /// passed it straight through.
        /// </summary>
        [Fact]
        public void NonFiniteAimInputLeavesTheStateIntact()
        {
            TurretAimLimits limits = Limits(300f, -10f, 20f);
            TurretAimState state = default;
            state.Yaw = 42f;
            state.Pitch = 5f;

            TurretAimCore.Step(ref state, float.NaN, float.PositiveInfinity, in limits, 1f / 60f);

            Assert.Equal(42f, state.Yaw);
            Assert.Equal(5f, state.Pitch);
        }

        // ------------------------------------------------------------------ input clamp

        [Theory]
        [InlineData(float.NaN, 0f)]
        [InlineData(float.PositiveInfinity, 0f)]
        [InlineData(float.NegativeInfinity, 0f)]
        [InlineData(10f, 1f)]
        [InlineData(-10f, -1f)]
        [InlineData(-0.5f, -0.5f)]
        [InlineData(1f, 1f)]
        [InlineData(0f, 0f)]
        public void ClampRejectsNonFinite(float input, float expected)
        {
            Assert.Equal(expected, VehicleInputClamp.Axis(input));
        }

        [Fact]
        public void MagnitudeBoundsThePairJointlyAndRejectsNonFinite()
        {
            float x;
            float y;

            // Inside the bound: untouched.
            VehicleInputClamp.Magnitude(0.3f, -0.4f, 1f, out x, out y);
            Assert.Equal(0.3f, x, 5);
            Assert.Equal(-0.4f, y, 5);

            // Outside: scaled to exactly the bound, direction preserved.
            VehicleInputClamp.Magnitude(3f, 4f, 1f, out x, out y);
            Assert.Equal(1f, (float)System.Math.Sqrt(x * x + y * y), 5);
            Assert.Equal(0.6f, x, 5);
            Assert.Equal(0.8f, y, 5);

            // A single NaN component must not take the whole pair with it into NaN.
            VehicleInputClamp.Magnitude(float.NaN, 0.5f, 1f, out x, out y);
            Assert.Equal(0f, x);
            Assert.Equal(0.5f, y, 5);
        }

        // ------------------------------------------------------------------ tick timer

        [Fact]
        public void TickTimerFiresExactlyOnceOnTheArmedTick()
        {
            TickTimer timer = default;
            timer.Arm(25);

            for (int i = 1; i < 25; i++)
                Assert.False(timer.Tick(), $"fired early on tick {i}");

            Assert.True(timer.Tick());

            // And never again.
            for (int i = 0; i < 5; i++)
                Assert.False(timer.Tick());
        }

        [Fact]
        public void TickTimerArmedWithZeroNeverFires()
        {
            TickTimer timer = default;
            timer.Arm(0);
            Assert.False(timer.IsArmed);

            for (int i = 0; i < 60; i++)
                Assert.False(timer.Tick());

            timer.Arm(-5);
            Assert.False(timer.Tick());
        }

        [Fact]
        public void TickTimerCancelStopsAPendingFire()
        {
            TickTimer timer = default;
            timer.Arm(25);
            timer.Tick();
            timer.Cancel();

            Assert.False(timer.IsArmed);
            for (int i = 0; i < 60; i++)
                Assert.False(timer.Tick());
        }

        [Fact]
        public void TickTimerReArmReplacesTheRemainingCount()
        {
            TickTimer timer = default;
            timer.Arm(25);
            for (int i = 0; i < 20; i++) timer.Tick();

            timer.Arm(3);
            Assert.False(timer.Tick());
            Assert.False(timer.Tick());
            Assert.True(timer.Tick());
        }

        // ------------------------------------------------------------------ explosion ranges

        [Fact]
        public void ExplosionDamageStopsAtDamageRange()
        {
            ExplosionRanges ranges = new ExplosionRanges(6f, 9f);
            float t;

            // At and beyond the damage radius: no damage AT ALL. This is the bug -- the
            // shipped Clamp01 gave an actor at 8 m the same damage as one at 6.001 m.
            Assert.False(ranges.TryGetDamageT(6f, out t));
            Assert.False(ranges.TryGetDamageT(8f, out t));
            Assert.False(ranges.TryGetDamageT(9f, out t));
            Assert.False(ranges.TryGetDamageT(float.NaN, out t));

            // Inside: strictly increasing, and in [0, 1).
            float previous = -1f;
            for (float d = 0f; d < 6f; d += 0.5f)
            {
                Assert.True(ranges.TryGetDamageT(d, out t));
                Assert.True(t > previous, $"t did not increase at {d} m");
                Assert.InRange(t, 0f, 0.999999f);
                previous = t;
            }
        }

        [Fact]
        public void ExplosionBalanceNormalizesOverBalanceRange()
        {
            ExplosionRanges ranges = new ExplosionRanges(6f, 9f);

            Assert.Equal(0f, ranges.GetBalanceT(0f), 5);
            Assert.Equal(0.5f, ranges.GetBalanceT(4.5f), 5);

            // Saturating IS correct here: the caller already restricted the query to
            // balanceRange, so the endpoint is the boundary rather than a plateau past one.
            Assert.Equal(1f, ranges.GetBalanceT(9f), 5);
            Assert.Equal(1f, ranges.GetBalanceT(30f), 5);
        }

        // ------------------------------------------------------------------ helpers

        private static TurretAimLimits Limits(float rate, float pitchMin, float pitchMax)
        {
            return new TurretAimLimits
            {
                YawRateDegPerSec = rate,
                PitchRateDegPerSec = rate,
                PitchMin = pitchMin,
                PitchMax = pitchMax
            };
        }

        /// <summary>Yaw traversed by one second of full-deflection input at the given step.</summary>
        private static float TraverseForOneSecond(float rate, float dt, int steps)
        {
            TurretAimLimits limits = Limits(rate, -90f, 90f);
            TurretAimState state = default;

            for (int i = 0; i < steps; i++)
                TurretAimCore.Step(ref state, 1f, 0f, in limits, dt);

            return state.Yaw;
        }
    }
}
