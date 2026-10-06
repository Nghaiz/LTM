using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A ragdoll's impact is the speed it loses hitting something, whatever made it that fast:
    /// owner ruling 2026-10-06, "being thrown by a big force at high speed must hurt more than a
    /// free fall".
    /// </summary>
    public sealed class ImpactDetectorTests
    {
        private const float Dt = 1f / 60f;
        private const float G = 9.81f;
        private static readonly float Threshold = FallDamage.SafeImpactSpeed(G);

        /// <summary>Feeds samples one physics step apart and returns the total reported.</summary>
        private static float Run(ImpactDetector detector, ref float time, Vec3 velocity, int steps)
        {
            float reported = 0f;
            for (int i = 0; i < steps; i++)
            {
                reported += detector.Observe(velocity, time, Threshold);
                time += Dt;
            }
            return reported;
        }

        [Fact]
        public void AFreeFallThatStopsOnTheGroundReportsItsLandingSpeed()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            float reported = 0f;
            float speed = 0f;
            // Two seconds of free fall: 19.6 m, landing at 19.6 m/s.
            for (int i = 0; i < 120; i++)
            {
                reported += detector.Observe(new Vec3(0f, -speed, 0f), time, Threshold);
                speed += G * Dt;
                time += Dt;
            }
            Assert.Equal(0f, reported);
            float landing = speed - G * Dt;   // the last speed fed in

            // Stopped over three steps, legs first.
            reported += detector.Observe(new Vec3(0f, -landing * 0.5f, 0f), time, Threshold); time += Dt;
            reported += detector.Observe(new Vec3(0f, -landing * 0.1f, 0f), time, Threshold); time += Dt;
            reported += Run(detector, ref time, Vec3.Zero, 20);

            Assert.Equal(landing, reported, 2);
        }

        [Fact]
        public void ABlastThatThrowsABodyIsNotItselfAnImpact()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            Run(detector, ref time, Vec3.Zero, 5);

            Assert.Equal(0f, Run(detector, ref time, new Vec3(18f, 9f, 0f), 30));
        }

        [Fact]
        public void AThrownBodyHittingAWallReportsTheSpeedItLost()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            Run(detector, ref time, new Vec3(20f, 0f, 0f), 10);

            Assert.Equal(20f, Run(detector, ref time, Vec3.Zero, 20), 2);
        }

        [Fact]
        public void ASlideThatSlowsDownOverASecondIsNoImpact()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            float reported = 0f;
            for (int i = 0; i <= 60; i++)
            {
                reported += detector.Observe(new Vec3(12f * (1f - i / 60f), 0f, 0f), time, Threshold);
                time += Dt;
            }

            Assert.Equal(0f, reported);
        }

        [Fact]
        public void ABounceCountsTheSpeedThatTurnedRound()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            Run(detector, ref time, new Vec3(0f, -15f, 0f), 10);

            Assert.Equal(20f, Run(detector, ref time, new Vec3(0f, 5f, 0f), 20), 2);
        }

        [Fact]
        public void OneImpactIsReportedOnce()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            Run(detector, ref time, new Vec3(0f, -16f, 0f), 10);

            float reported = Run(detector, ref time, Vec3.Zero, 120);

            Assert.Equal(16f, reported, 2);
        }

        [Fact]
        public void ATumbleUnderTheThresholdReportsNothing()
        {
            var detector = new ImpactDetector();
            float time = 0f;
            Run(detector, ref time, new Vec3(0f, -5f, 0f), 10);

            Assert.Equal(0f, Run(detector, ref time, Vec3.Zero, 30));
        }
    }
}
