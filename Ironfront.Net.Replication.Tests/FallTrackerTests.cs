using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A landing is paid on the height fallen since the body last stood, not on a velocity that
    /// carries the 10 m/s stick-to-ground pull (owner request 2026-10-06: fall damage).
    /// </summary>
    public sealed class FallTrackerTests
    {
        private const float Dt = 1f / 30f;

        /// <summary>
        /// Walks a body off an edge <paramref name="height"/> metres high through
        /// <see cref="MovementCore.Step"/>, observing it as the server does each tick, and returns
        /// the landing speed the tracker reports.
        /// </summary>
        private static float WalkOffAnEdge(float height, bool jump = false)
        {
            var tracker = new FallTracker();
            var state = MoveState.AtRest(new Vec3(0f, height, 0f), grounded: true);
            var walk = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);
            var hop = new MoveInput(0f, 1f, 0f, jump: true, sprint: false, crouch: false);

            // Standing on the edge, then the step that leaves it.
            Assert.Equal(0f, tracker.Observe(true, state.Position.Y, state.Velocity.Y));
            state.Position += MovementCore.Step(ref state, jump ? hop : walk, Dt);
            state.IsGrounded = false;

            float landed = 0f;
            for (int tick = 0; tick < 300 && landed == 0f; tick++)
            {
                bool grounded = state.Position.Y <= 0f;
                if (grounded) state.Position = new Vec3(state.Position.X, 0f, state.Position.Z);
                state.IsGrounded = grounded;
                landed = tracker.Observe(grounded, state.Position.Y, state.Velocity.Y);
                if (grounded) break;
                state.Position += MovementCore.Step(ref state, walk, Dt);
            }
            return landed;
        }

        private static float G => -MovementCore.Gravity;

        [Fact]
        public void AKerbCostsNothingThoughTheStickPullReadsTenMetresASecond()
        {
            // The velocity at this landing is about -10.4 m/s, the speed of a 4.6 m fall. The
            // height says 3.4 m/s.
            float landed = WalkOffAnEdge(0.5f);

            Assert.Equal(FallDamage.LandingSpeed(0.5f, 0f, G), landed, 2);
            Assert.Equal(0f, FallDamage.ForImpact(landed, G));
        }

        [Fact]
        public void ALongFallLandsFromItsHeight()
        {
            float landed = WalkOffAnEdge(40f);

            Assert.Equal(FallDamage.LandingSpeed(40f, 0f, G), landed, 2);
            Assert.True(FallDamage.ForImpact(landed, G) > FallDamage.FullHealth);
        }

        [Fact]
        public void AJumpInPlaceLandsAtItsOwnTakeoffSpeed()
        {
            var tracker = new FallTracker();
            tracker.Observe(true, 0f, -MovementCore.StickToGroundForce);
            tracker.Observe(false, 0.1f, MovementCore.JumpSpeed);
            tracker.Observe(false, 1f, 0f);

            Assert.Equal(MovementCore.JumpSpeed, tracker.Observe(true, 0f, -3f), 3);
        }

        [Fact]
        public void AJumpOffARoofAddsTheTakeoffToTheDrop()
        {
            float landed = WalkOffAnEdge(4f, jump: true);

            Assert.Equal(FallDamage.LandingSpeed(4f, MovementCore.JumpSpeed, G), landed, 2);
        }

        [Fact]
        public void ABailOutIsMeasuredFromTheSeat()
        {
            var tracker = new FallTracker();
            tracker.Observe(true, 2f, 0f);       // walked to the helicopter
            tracker.Rebase(42f);                  // left its seat 40 m above that ground
            tracker.Observe(false, 30f, -15f);

            Assert.Equal(FallDamage.LandingSpeed(40f, 0f, G), tracker.Observe(true, 2f, -30f), 3);
        }

        [Fact]
        public void AfterForgettingNothingIsPaidUntilTheBodyHasStoodAgain()
        {
            var tracker = new FallTracker();
            tracker.Observe(true, 100f, 0f);
            tracker.Forget();                     // died up there, respawned down here

            Assert.Equal(0f, tracker.Observe(true, 5f, 0f));
            Assert.Equal(5f, tracker.OriginY);
        }

        [Fact]
        public void TheLandingKeepsTheSpeedAcrossOfTheLastStepInTheAir()
        {
            var tracker = new FallTracker();
            tracker.Observe(true, 10f, 0f, 6.5f, 0f);
            tracker.Observe(false, 10.2f, MovementCore.JumpSpeed, 6.5f, 1f);
            tracker.Observe(false, 8f, -8f, 6f, 2f);

            float landed = tracker.Observe(true, 6f, -10f, 0f, 0f);   // the landing step has stopped

            Assert.True(landed > 0f);
            Assert.Equal(6f, tracker.LandingHorizontalX);
            Assert.Equal(2f, tracker.LandingHorizontalZ);
        }

        [Fact]
        public void LandingHigherThanTheTakeoffPaysOnlyTheTakeoff()
        {
            Assert.Equal(MovementCore.JumpSpeed, FallDamage.LandingSpeed(-1f, MovementCore.JumpSpeed, G), 3);
        }
    }
}
