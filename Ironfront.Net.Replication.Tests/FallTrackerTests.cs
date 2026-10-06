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

        [Fact]
        public void AKerbCostsNothingThoughTheStickPullReadsTenMetresASecond()
        {
            // The velocity at this landing is about -10.4 m/s, which FallDamage would charge a
            // fifth of a soldier's health for. The height says 3 m/s.
            float landed = WalkOffAnEdge(0.5f);

            Assert.InRange(landed, 3.0f, 3.6f);
            Assert.Equal(0f, FallDamage.ForImpact(landed));
        }

        [Fact]
        public void ALongFallLandsAtTheSpeedOfItsHeight()
        {
            float landed = WalkOffAnEdge(40f);

            Assert.Equal(FallTracker.ImpactSpeed(40f, 0f), landed, 0);
            Assert.True(FallDamage.ForImpact(landed) > FallDamage.FullHealth);
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

            Assert.True(landed > FallTracker.ImpactSpeed(4f, 0f));
        }

        [Fact]
        public void ABailOutIsMeasuredFromTheSeat()
        {
            var tracker = new FallTracker();
            tracker.Observe(true, 2f, 0f);       // walked to the helicopter
            tracker.Rebase(42f);                  // left its seat 40 m above that ground
            tracker.Observe(false, 30f, -15f);

            Assert.Equal(FallTracker.ImpactSpeed(40f, 0f), tracker.Observe(true, 2f, -30f), 3);
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
        public void LandingHigherThanTheTakeoffPaysOnlyTheTakeoff()
        {
            Assert.Equal(MovementCore.JumpSpeed, FallTracker.ImpactSpeed(-1f, MovementCore.JumpSpeed), 3);
        }
    }
}
