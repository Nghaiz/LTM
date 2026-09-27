using System;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The attitude help a person flying a helicopter gets (<see cref="HelicopterFlightAssist"/>),
    /// flown through the original's control kick and angular drag.
    /// </summary>
    /// <remarks>
    /// Every behaviour below is paired with the same flight at zero authority, the unassisted
    /// original, so each test would also fail if the assist silently did nothing.
    /// </remarks>
    public sealed class HelicopterFlightAssistTests
    {
        private const float Dt = 1f / 60f;

        private static float Degrees(float radians) => radians * 180f / (float)Math.PI;

        private static float Radians(float degrees) => degrees * (float)Math.PI / 180f;

        /// <summary>
        /// A small-angle stand-in for <c>Helicopter.FixedUpdate</c>: the full-stick kick of
        /// <c>MANOUVERABILITY_SCALE</c> times the helicopter prefab's <c>manouverability</c> of 2
        /// per step, the assist's change on top, then the base angular drag of 0.2 per second,
        /// and the angles integrated from the rates.
        /// </summary>
        private sealed class Flight
        {
            private const float Kick = 0.0069999998f * 2f;
            private const float AngularDrag = 0.2f;

            private readonly HelicopterFlightAssist _assist = new HelicopterFlightAssist();

            public float NoseDown;
            public float BankRight;
            public float PitchRate;
            public float YawRate;
            public float RollRate;

            public void Fly(float seconds, float pitch, float yaw, float roll, float authority = 1f,
                Action<Flight>? eachStep = null)
            {
                int steps = (int)Math.Round(seconds / Dt);
                for (int i = 0; i < steps; i++)
                {
                    _assist.Step(pitch, yaw, roll, PitchRate, YawRate, RollRate, NoseDown, BankRight,
                        upright: true, authority, Dt, out float dPitch, out float dYaw, out float dRoll);

                    PitchRate += pitch * Kick + dPitch;
                    YawRate += yaw * Kick + dYaw;
                    RollRate += roll * Kick + dRoll;

                    float drag = 1f / (1f + Dt * AngularDrag);
                    PitchRate *= drag;
                    YawRate *= drag;
                    RollRate *= drag;

                    NoseDown += PitchRate * Dt;
                    BankRight += RollRate * Dt;

                    eachStep?.Invoke(this);
                }
            }
        }

        // ------------------------------------------------------------------ holding an attitude

        [Fact]
        public void AReleasedAxisStopsTurning()
        {
            var assisted = new Flight { PitchRate = 1f, YawRate = 1f, RollRate = 0.5f };
            var original = new Flight { PitchRate = 1f, YawRate = 1f, RollRate = 0.5f };

            assisted.Fly(1f, 0f, 0f, 0f);
            original.Fly(1f, 0f, 0f, 0f, authority: 0f);

            Assert.True(Math.Abs(assisted.PitchRate) < 0.02f, $"pitch still turning at {assisted.PitchRate:F3} rad/s");
            Assert.True(Math.Abs(assisted.YawRate) < 0.02f, $"yaw still turning at {assisted.YawRate:F3} rad/s");

            // The original keeps turning for seconds: this is the reported "it tilts on its own".
            Assert.True(original.PitchRate > 0.8f, $"unassisted pitch rate {original.PitchRate:F3}");
            Assert.True(original.YawRate > 0.8f, $"unassisted yaw rate {original.YawRate:F3}");
        }

        [Fact]
        public void ACommandedAxisIsLeftToThePilot()
        {
            var assist = new HelicopterFlightAssist();

            assist.Step(1f, -1f, 0.5f, 0.3f, -0.3f, 0.2f, 0f, 0f, true, 1f, Dt,
                out float pitch, out float yaw, out float roll);

            Assert.Equal(0f, pitch);
            Assert.Equal(0f, yaw);
            Assert.Equal(0f, roll);
        }

        [Fact]
        public void AShortGapInAMovementDoesNotBrakeIt()
        {
            var assist = new HelicopterFlightAssist();
            assist.Step(1f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, true, 1f, Dt, out _, out _, out _);

            // A mouse that did not report on a frame, or a network tick held one step longer.
            for (int i = 0; i < 5; i++)
            {
                assist.Step(0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, true, 1f, Dt, out float gap, out _, out _);
                Assert.Equal(0f, gap);
            }

            // Released for longer than the latch, the same axis is braked.
            float braked = 0f;
            for (int i = 0; i < 5; i++)
                assist.Step(0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, true, 1f, Dt, out braked, out _, out _);

            Assert.True(braked < 0f, $"a released pitch was not braked: {braked}");
        }

        // ------------------------------------------------------------------ levelling the bank

        [Fact]
        public void AReleasedBankReturnsToLevelWithoutOvershoot()
        {
            var assisted = new Flight { BankRight = Radians(30f) };
            var original = new Flight { BankRight = Radians(30f) };
            float lowest = float.MaxValue;

            assisted.Fly(3f, 0f, 0f, 0f, eachStep: f => lowest = Math.Min(lowest, f.BankRight));
            original.Fly(3f, 0f, 0f, 0f, authority: 0f);

            Assert.True(Math.Abs(Degrees(assisted.BankRight)) < 1f, $"still banked {Degrees(assisted.BankRight):F1} deg");
            Assert.True(Degrees(lowest) > -0.5f, $"overshot to {Degrees(lowest):F1} deg");
            Assert.Equal(30f, Degrees(original.BankRight), 3);
        }

        [Fact]
        public void AReleasedPitchIsHeldRatherThanLevelled()
        {
            // A mouse cannot hold a deflection, so a pitch that sprang back would make sustained
            // forward flight impossible for the default control style.
            var flight = new Flight { NoseDown = Radians(20f) };

            flight.Fly(3f, 0f, 0f, 0f);

            Assert.Equal(20f, Degrees(flight.NoseDown), 3);
        }

        // ------------------------------------------------------------------ the tilt limits

        [Fact]
        public void HoldingFullRollStopsAtTheBankLimit()
        {
            var assisted = new Flight();
            var original = new Flight();
            float steepest = 0f;

            assisted.Fly(10f, 0f, 0f, 1f, eachStep: f => steepest = Math.Max(steepest, f.BankRight));
            original.Fly(5f, 0f, 0f, 1f, authority: 0f);

            Assert.True(Degrees(steepest) <= HelicopterFlightAssist.MaxBankDegrees + 2f,
                $"banked to {Degrees(steepest):F1} deg");
            Assert.True(Degrees(assisted.BankRight) >= HelicopterFlightAssist.MaxBankDegrees - 2f,
                $"full stick only reached {Degrees(assisted.BankRight):F1} deg");

            // Unassisted, five seconds of full roll is a roll-over.
            Assert.True(Degrees(original.BankRight) > 90f, $"unassisted bank {Degrees(original.BankRight):F1} deg");
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void HoldingFullPitchStopsAtThePitchLimit(float stick)
        {
            var flight = new Flight();
            float steepest = 0f;

            flight.Fly(10f, stick, 0f, 0f, eachStep: f => steepest = Math.Max(steepest, Math.Abs(f.NoseDown)));

            Assert.True(Degrees(steepest) <= HelicopterFlightAssist.MaxPitchDegrees + 2f,
                $"pitched to {Degrees(steepest):F1} deg");
            Assert.Equal(Math.Sign(stick), Math.Sign(flight.NoseDown));
        }

        [Fact]
        public void ATiltPastTheLimitIsPulledBack()
        {
            // A collision or an explosion, not the stick, put it there.
            var flight = new Flight { NoseDown = Radians(60f) };

            flight.Fly(3f, 0f, 0f, 0f);

            Assert.True(Degrees(flight.NoseDown) <= HelicopterFlightAssist.MaxPitchDegrees + 1f,
                $"still at {Degrees(flight.NoseDown):F1} deg");
        }

        [Fact]
        public void UpsideDownItOnlyDamps()
        {
            // The angles cannot tell a bank from a roll-over once inverted.
            var assist = new HelicopterFlightAssist();
            float hold = 1f - (float)Math.Exp(-Dt / HelicopterFlightAssist.HoldSeconds);

            assist.Step(0f, 0f, 0f, 0.5f, 0f, 0f, Radians(80f), Radians(30f), false, 1f, Dt,
                out float pitch, out _, out float roll);

            Assert.Equal(-0.5f * hold, pitch, 6);
            Assert.Equal(0f, roll);
        }

        // ------------------------------------------------------------------ authority

        [Fact]
        public void ZeroAuthorityChangesNothingButStillTracksTheSticks()
        {
            var assist = new HelicopterFlightAssist();

            assist.Step(1f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, true, 0f, Dt, out float pitch, out float yaw, out float roll);
            Assert.Equal(0f, pitch);
            Assert.Equal(0f, yaw);
            Assert.Equal(0f, roll);

            // Released while grounded; already released when the assist takes over.
            for (int i = 0; i < 12; i++)
                assist.Step(0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, true, 0f, Dt, out _, out _, out _);

            assist.Step(0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, true, 1f, Dt, out pitch, out _, out _);
            Assert.True(pitch < 0f, $"a pitch released while grounded was not braked: {pitch}");
        }

        [Fact]
        public void AuthorityScalesTheHelp()
        {
            var full = new HelicopterFlightAssist();
            var half = new HelicopterFlightAssist();

            full.Step(0f, 0f, 0f, 0.4f, 0.2f, 0.1f, 0f, Radians(10f), true, 1f, Dt, out float fp, out float fy, out float fr);
            half.Step(0f, 0f, 0f, 0.4f, 0.2f, 0.1f, 0f, Radians(10f), true, 0.5f, Dt, out float hp, out float hy, out float hr);

            Assert.Equal(fp * 0.5f, hp, 6);
            Assert.Equal(fy * 0.5f, hy, 6);
            Assert.Equal(fr * 0.5f, hr, 6);
        }

        [Theory]
        [InlineData(float.NaN, 0.1f)]
        [InlineData(0.1f, float.NaN)]
        [InlineData(float.PositiveInfinity, 0.1f)]
        public void ANonFiniteBodyGetsNoHelp(float rate, float angle)
        {
            var assist = new HelicopterFlightAssist();

            assist.Step(0f, 0f, 0f, rate, rate, rate, angle, angle, true, 1f, Dt,
                out float pitch, out float yaw, out float roll);

            Assert.Equal(0f, pitch);
            Assert.Equal(0f, yaw);
            Assert.Equal(0f, roll);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-0.1f)]
        [InlineData(float.NaN)]
        public void ANonPositiveStepChangesNothing(float dt)
        {
            var assist = new HelicopterFlightAssist();

            assist.Step(0f, 0f, 0f, 1f, 1f, 1f, 0f, 0.3f, true, 1f, dt,
                out float pitch, out float yaw, out float roll);

            Assert.Equal(0f, pitch);
            Assert.Equal(0f, yaw);
            Assert.Equal(0f, roll);
        }
    }
}
