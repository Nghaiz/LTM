using System;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="MovementCore.WaterHeight"/> is one value per process, so every test that sets
    /// it runs alone: another test's movement must never see a surface it did not ask for.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class WaterSurfaceCollection
    {
        public const string Name = "MovementCore water surface";
    }

    /// <summary>
    /// A player's body swims (2026-09-29): it floats up to the surface and swims at the original's
    /// speed, and walks again where the water is shallow. Before this it walked the bottom.
    /// </summary>
    [Collection(WaterSurfaceCollection.Name)]
    public sealed class SwimmingTests : IDisposable
    {
        private const float Dt = 1f / 30f;
        private const float Water = 10f;

        public SwimmingTests()
        {
            MovementCore.WaterHeight = Water;
        }

        public void Dispose()
        {
            MovementCore.WaterHeight = float.NegativeInfinity;
            MovementCore.BoundedWater = null;
        }

        [Fact]
        public void AMapWithNoWaterHasNobodyInIt()
        {
            MovementCore.WaterHeight = float.NegativeInfinity;
            Assert.False(MovementCore.IsInWater(0f, -1000f, 0f));
        }

        [Fact]
        public void ABodyIsInWaterOnceHalfAMetreOverItsCentreIsUnder()
        {
            Assert.True(MovementCore.IsInWater(0f, Water - MovementCore.SwimSampleAbove, 0f));
            Assert.False(MovementCore.IsInWater(0f, Water - MovementCore.SwimSampleAbove + 0.01f, 0f));
        }

        [Fact]
        public void ASinkingBodyFloatsUpToTheSurfaceAndStaysThere()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - 5f, 0f), grounded: false);
            var idle = new MoveInput(0f, 0f, 0f, jump: false, sprint: false, crouch: false);

            for (int i = 0; i < 30 * 10; i++) state.Position += MovementCore.Step(ref state, in idle, Dt);

            Assert.Equal(Water - MovementCore.SwimFloatDepth, state.Position.Y, 2);
            Assert.True(MovementCore.IsInWater(in state.Position),
                "a body that floated up stopped swimming: it would fall back in and bob forever");
        }

        [Fact]
        public void ThereIsNoGravityInWater()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - MovementCore.SwimFloatDepth, 0f), grounded: false);
            var idle = new MoveInput(0f, 0f, 0f, jump: false, sprint: false, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in idle, Dt);
            Assert.Equal(0f, motion.Y, 4);
        }

        [Fact]
        public void ASwimmerMovesAtTheOriginalsSwimSpeedWhereItLooks()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - MovementCore.SwimFloatDepth, 0f), grounded: false);
            var forward = new MoveInput(0f, 1f, 90f, jump: false, sprint: true, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in forward, Dt);

            Assert.Equal(MovementCore.SwimSpeed * Dt, motion.X, 4);
            Assert.Equal(0f, motion.Z, 4);
        }

        [Fact]
        public void AJumpInWaterDoesNothing()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - MovementCore.SwimFloatDepth, 0f), grounded: true);
            var jump = new MoveInput(0f, 0f, 0f, jump: true, sprint: false, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in jump, Dt);
            Assert.Equal(0f, motion.Y, 4);
        }

        [Fact]
        public void ACrouchInWaterIsDropped()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - 2f, 0f), grounded: false);
            var crouch = new MoveInput(0f, 0f, 0f, jump: false, sprint: false, crouch: true);

            MovementCore.Step(ref state, in crouch, Dt);
            Assert.False(state.IsCrouching, "a crouch shrank the capsule of a body in water");
        }

        [Fact]
        public void ASwimmerPushingAgainstTheBankClimbsIt()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - MovementCore.SwimFloatDepth, 0f), grounded: false);
            state.IsBlockedSideways = true;
            var forward = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in forward, Dt);

            Assert.True(state.IsClimbingOut);
            Assert.Equal(MovementCore.ClimbOutSpeed * Dt, motion.Y, 4);
            Assert.Equal(MovementCore.SwimSpeed * Dt, motion.Z, 4);
        }

        [Fact]
        public void AClimbCarriesOnOutOfTheWaterUntilTheFeetClearTheSurface()
        {
            var forward = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);
            float halfHeight = MovementCore.StandHeight * 0.5f;

            // Out of the water by the swim test, still pushing up the bank: the climb goes on.
            float centre = Water - MovementCore.SwimSampleAbove + 0.3f;
            Assert.False(MovementCore.IsInWater(0f, centre, 0f));
            MoveState state = MoveState.AtRest(new Vec3(0f, centre, 0f), grounded: false);
            state.IsBlockedSideways = true;
            state.IsClimbingOut = true;
            Assert.Equal(MovementCore.ClimbOutSpeed * Dt, MovementCore.Step(ref state, in forward, Dt).Y, 4);

            // Feet past the lip: the climb is over and the body falls or walks again.
            state = MoveState.AtRest(new Vec3(0f, Water + MovementCore.ClimbOutLip + halfHeight + 0.01f, 0f), grounded: false);
            state.IsBlockedSideways = true;
            state.IsClimbingOut = true;
            Assert.True(MovementCore.Step(ref state, in forward, Dt).Y < 0f, "the climb went on past the lip");
            Assert.False(state.IsClimbingOut);
        }

        [Fact]
        public void ASwimmerThatStopsPushingFloatsOnInsteadOfClimbing()
        {
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - MovementCore.SwimFloatDepth, 0f), grounded: false);
            state.IsBlockedSideways = true;
            var idle = new MoveInput(0f, 0f, 0f, jump: false, sprint: false, crouch: false);

            Assert.Equal(0f, MovementCore.Step(ref state, in idle, Dt).Y, 4);
            Assert.False(state.IsClimbingOut);
        }

        [Fact]
        public void AWalkerInTheShallowsDoesNotClimbAWall()
        {
            // Too shallow to swim, walking into a rock: that is a wall, not a bank to climb out on.
            float centre = Water - MovementCore.SwimSampleAbove + 0.2f;
            MoveState state = MoveState.AtRest(new Vec3(0f, centre, 0f), grounded: true);
            state.IsBlockedSideways = true;
            var forward = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in forward, Dt);

            Assert.False(state.IsClimbingOut);
            Assert.Equal(-MovementCore.StickToGroundForce * Dt, motion.Y, 4);
        }

        [Fact]
        public void InShallowWaterTheBodyWalks()
        {
            // Standing on a bottom one metre down: the centre is 0.9 m over the feet, so half a
            // metre over the centre is above the surface.
            MoveState state = MoveState.AtRest(new Vec3(0f, Water - 1f + 0.9f, 0f), grounded: true);
            var forward = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);

            Vec3 motion = MovementCore.Step(ref state, in forward, Dt);

            Assert.Equal(-MovementCore.StickToGroundForce * Dt, motion.Y, 4);
            Assert.Equal(MovementCore.WalkSpeed * Dt, motion.Z, 4);
        }
    }
}
