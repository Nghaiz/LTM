using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Pins the movement port against the values actually in the shipped project.
    /// </summary>
    /// <remarks>
    /// These tests are the reason phase-00's shadow-comparison stage can be short: most of
    /// what a playtest would catch is caught here, deterministically, before anyone opens the
    /// Editor. What they cannot catch is collision and slope response — see
    /// docs/movement-analysis.md § 5.
    /// </remarks>
    public sealed class MovementCoreTests
    {
        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        // ------------------------------------------------------------------ the constants

        [Fact]
        public void ConstantsMatchThePlayerPrefab()
        {
            // Assets/Prefab/Player Fps Actor.prefab, the FirstPersonController MonoBehaviour.
            // If any of these change in the prefab, the server silently disagrees with what
            // the player feels. This test is the tripwire.
            Assert.Equal(3.5f, MovementCore.WalkSpeed);
            Assert.Equal(6.5f, MovementCore.RunSpeed);
            Assert.Equal(5f, MovementCore.JumpSpeed);
            Assert.Equal(10f, MovementCore.StickToGroundForce);
            Assert.Equal(1.2f, MovementCore.GravityMultiplier);
            Assert.Equal(1.8f, MovementCore.StandHeight);
            Assert.Equal(0.5f, MovementCore.CrouchHeight);
        }

        [Fact]
        public void GravityIsProjectSettingsGravityTimesTheMultiplier()
        {
            // ProjectSettings/DynamicsManager.asset: m_Gravity.y = -9.81.
            Assert.Equal(-9.81f, MovementCore.BaseGravity);
            Assert.Equal(-9.81f * 1.2f, MovementCore.Gravity, 5);
        }

        [Fact]
        public void CrouchingDoesNotChangeSpeed()
        {
            // Not an oversight — the shipped game has no crouch speed at all. See
            // MovementCore.SpeedFor for why inventing one would cause rubber-banding.
            var walking  = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);
            var crouched = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: true);

            Assert.Equal(MovementCore.SpeedFor(in walking), MovementCore.SpeedFor(in crouched));
            Assert.Equal(MovementCore.WalkSpeed, MovementCore.SpeedFor(in crouched));
        }

        [Fact]
        public void SprintSelectsRunSpeed()
        {
            var sprinting = new MoveInput(0f, 1f, 0f, jump: false, sprint: true, crouch: false);
            Assert.Equal(MovementCore.RunSpeed, MovementCore.SpeedFor(in sprinting));
        }

        // ------------------------------------------------------------------ direction

        [Theory]
        [InlineData(0f, 0f, 1f)]     // yaw 0 faces +Z
        [InlineData(90f, 1f, 0f)]    // yaw 90 faces +X
        [InlineData(180f, 0f, -1f)]
        [InlineData(270f, -1f, 0f)]
        public void ForwardFromYawMatchesUnityHandedness(float yaw, float expectedX, float expectedZ)
        {
            Vec3 forward = MovementCore.ForwardFromYaw(yaw);

            Assert.Equal(expectedX, forward.X, 4);
            Assert.Equal(0f, forward.Y, 6);
            Assert.Equal(expectedZ, forward.Z, 4);
        }

        [Fact]
        public void MovesAlongFacingWhenPushingForward()
        {
            var state = MoveState.AtRest(Vec3.Zero);
            var input = new MoveInput(0f, 1f, yawDegrees: 90f, jump: false, sprint: false, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in input, Dt);

            // Facing +X at walk speed.
            Assert.Equal(MovementCore.WalkSpeed * Dt, motion.X, 4);
            Assert.Equal(0f, motion.Z, 4);
        }

        [Fact]
        public void StrafeIsPerpendicularAndToTheRight()
        {
            var state = MoveState.AtRest(Vec3.Zero);
            var input = new MoveInput(1f, 0f, yawDegrees: 0f, jump: false, sprint: false, crouch: false);

            MovementCore.Step(ref state, in input, Dt);

            // Facing +Z, strafing right means +X.
            Assert.Equal(MovementCore.WalkSpeed, state.Velocity.X, 4);
            Assert.Equal(0f, state.Velocity.Z, 4);
        }

        [Fact]
        public void DiagonalInputDoesNotMoveFasterThanForwardInput()
        {
            // The classic exploit, and the property that kills it: the wish direction is
            // normalized, so a longer input vector buys nothing.
            var straight = MoveState.AtRest(Vec3.Zero);
            var diagonal = MoveState.AtRest(Vec3.Zero);

            var forwardOnly = new MoveInput(0f, 1f, 0f, false, false, false);
            var bothAxes    = new MoveInput(1f, 1f, 0f, false, false, false);

            MovementCore.Step(ref straight, in forwardOnly, Dt);
            MovementCore.Step(ref diagonal, in bothAxes, Dt);

            float straightSpeed = HorizontalSpeed(straight.Velocity);
            float diagonalSpeed = HorizontalSpeed(diagonal.Velocity);

            Assert.Equal(straightSpeed, diagonalSpeed, 4);
        }

        [Fact]
        public void PartialInputStillProducesFullSpeed()
        {
            // Faithful to the original: FirstPersonController normalizes the wish vector
            // AFTER scaling, so a half-deflected stick walks at full speed. Documented so
            // nobody "fixes" it into a desync.
            var state = MoveState.AtRest(Vec3.Zero);
            var half  = new MoveInput(0f, 0.5f, 0f, false, false, false);

            MovementCore.Step(ref state, in half, Dt);

            Assert.Equal(MovementCore.WalkSpeed, HorizontalSpeed(state.Velocity), 4);
        }

        [Fact]
        public void NoInputProducesNoHorizontalMotionAndNoNaN()
        {
            var state = MoveState.AtRest(Vec3.Zero);
            var idle  = new MoveInput(0f, 0f, 0f, false, false, false);

            Vec3 motion = MovementCore.Step(ref state, in idle, Dt);

            Assert.Equal(0f, state.Velocity.X, 6);
            Assert.Equal(0f, state.Velocity.Z, 6);
            Assert.False(float.IsNaN(motion.X) || float.IsNaN(motion.Y) || float.IsNaN(motion.Z));
        }

        // ------------------------------------------------------------------ vertical

        [Fact]
        public void GroundedActorIsPinnedDownwardByStickToGroundForce()
        {
            var state = MoveState.AtRest(Vec3.Zero, grounded: true);
            var idle  = new MoveInput(0f, 0f, 0f, false, false, false);

            MovementCore.Step(ref state, in idle, Dt);

            Assert.Equal(-MovementCore.StickToGroundForce, state.Velocity.Y, 4);
        }

        [Fact]
        public void JumpSetsUpwardVelocityOnlyWhenGrounded()
        {
            var grounded = MoveState.AtRest(Vec3.Zero, grounded: true);
            var airborne = MoveState.AtRest(Vec3.Zero, grounded: false);
            var jump     = new MoveInput(0f, 0f, 0f, jump: true, sprint: false, crouch: false);

            MovementCore.Step(ref grounded, in jump, Dt);
            MovementCore.Step(ref airborne, in jump, Dt);

            Assert.Equal(MovementCore.JumpSpeed, grounded.Velocity.Y, 4);

            // In the air the jump button does nothing; gravity keeps accruing.
            Assert.Equal(MovementCore.Gravity * Dt, airborne.Velocity.Y, 4);
        }

        [Fact]
        public void GravityAccumulatesWhileAirborne()
        {
            var state = MoveState.AtRest(Vec3.Zero, grounded: false);
            var idle  = new MoveInput(0f, 0f, 0f, false, false, false);

            MovementCore.Step(ref state, in idle, Dt);
            MovementCore.Step(ref state, in idle, Dt);
            MovementCore.Step(ref state, in idle, Dt);

            Assert.Equal(MovementCore.Gravity * Dt * 3f, state.Velocity.Y, 3);
        }

        [Fact]
        public void AJumpArcRisesThenFalls()
        {
            var state = MoveState.AtRest(Vec3.Zero, grounded: true);
            var jump  = new MoveInput(0f, 0f, 0f, jump: true, sprint: false, crouch: false);
            var idle  = new MoveInput(0f, 0f, 0f, false, false, false);

            Vec3 first = MovementCore.Step(ref state, in jump, Dt);
            Assert.True(first.Y > 0f, "the jump tick must move the actor upward");

            state.IsGrounded = false;

            float height = first.Y;
            float peak = height;
            for (int tick = 0; tick < 60; tick++)
            {
                height += MovementCore.Step(ref state, in idle, Dt).Y;
                if (height > peak) peak = height;
            }

            Assert.True(peak > 0.9f, $"peak jump height {peak:F3} m looks wrong for a 5 m/s launch");
            Assert.True(height < 0f, "after 2 seconds of gravity the actor must be below the launch height");
        }

        // ------------------------------------------------------------------ determinism

        [Fact]
        public void IsDeterministicAcrossRunsWithTheSameInput()
        {
            // The property client prediction lives on: identical inputs from an identical
            // start must produce a bit-identical trajectory, or every predicted tick
            // mispredicts.
            Vec3 first  = RunTrajectory();
            Vec3 second = RunTrajectory();

            Assert.Equal(first.X, second.X);
            Assert.Equal(first.Y, second.Y);
            Assert.Equal(first.Z, second.Z);
        }

        private static Vec3 RunTrajectory()
        {
            var state = MoveState.AtRest(Vec3.Zero, grounded: true);
            Vec3 position = Vec3.Zero;

            for (int tick = 0; tick < 200; tick++)
            {
                var input = new MoveInput(
                    (float)Math.Sin(tick * 0.1),
                    (float)Math.Cos(tick * 0.07),
                    tick * 3f,
                    jump: tick % 37 == 0,
                    sprint: tick % 11 < 5,
                    crouch: false);

                position += MovementCore.Step(ref state, in input, Dt);
                state.IsGrounded = position.Y <= 0f;
            }

            return position;
        }

        private static float HorizontalSpeed(in Vec3 velocity)
            => (float)Math.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
    }
}
