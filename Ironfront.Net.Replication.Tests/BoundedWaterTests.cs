using System;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A map's lakes (P30, Forest Lake): water that covers part of the map at its own height, beside
    /// or instead of a sea. The sea-only map is pinned by <see cref="SwimmingTests"/>.
    /// </summary>
    [Collection(WaterSurfaceCollection.Name)]
    public sealed class BoundedWaterTests : IDisposable
    {
        private const float Dt = 1f / 30f;
        private const float Sea = 10f;
        private const float Lake = 20f;

        /// <summary>A lake over x and z in [0, 100], its surface at <see cref="Lake"/>.</summary>
        private sealed class SquareLake : IBoundedWater
        {
            public float SurfaceAt(float x, float z)
                => x >= 0f && x <= 100f && z >= 0f && z <= 100f ? Lake : float.NegativeInfinity;
        }

        public BoundedWaterTests()
        {
            MovementCore.WaterHeight = Sea;
            MovementCore.BoundedWater = new SquareLake();
        }

        public void Dispose()
        {
            MovementCore.WaterHeight = float.NegativeInfinity;
            MovementCore.BoundedWater = null;
        }

        [Fact]
        public void ALakeIsWaterOnlyWhereItLies()
        {
            float centre = Lake - MovementCore.SwimSampleAbove - 0.01f;
            Assert.True(MovementCore.IsInWater(50f, centre, 50f));
            Assert.False(MovementCore.IsInWater(150f, centre, 50f),
                "a body beside the lake, at the lake's depth, was put in water that is not there");
        }

        [Fact]
        public void DryGroundLowerThanALakeIsNotWater()
        {
            // The case a single sea level gets wrong: a valley below the lake's surface, away from it.
            Assert.False(MovementCore.IsInWater(-40f, 15f, 50f));
        }

        [Fact]
        public void TheSeaStillCountsBesideALake()
        {
            Assert.True(MovementCore.IsInWater(-40f, Sea - MovementCore.SwimSampleAbove - 0.01f, 50f));
        }

        [Fact]
        public void TheHigherSurfaceWinsWhereALakeLiesOverTheSea()
        {
            Assert.Equal(Lake, MovementCore.SurfaceAt(50f, 50f));
            Assert.Equal(Sea, MovementCore.SurfaceAt(150f, 50f));
        }

        [Fact]
        public void WithNoBoundedWaterTheSeaIsTheSurfaceEverywhere()
        {
            MovementCore.BoundedWater = null;
            Assert.Equal(Sea, MovementCore.SurfaceAt(50f, 50f));
            Assert.Equal(Sea, MovementCore.SurfaceAt(-4000f, 9000f));
        }

        [Fact]
        public void ASwimmerInALakeFloatsAtTheLakesSurface()
        {
            MoveState state = MoveState.AtRest(new Vec3(50f, Lake - 5f, 50f), grounded: false);
            var idle = new MoveInput(0f, 0f, 0f, jump: false, sprint: false, crouch: false);

            for (int i = 0; i < 30 * 10; i++) state.Position += MovementCore.Step(ref state, in idle, Dt);

            Assert.Equal(Lake - MovementCore.SwimFloatDepth, state.Position.Y, 2);
            Assert.True(MovementCore.IsInWater(in state.Position));
        }

        [Fact]
        public void ABodyBesideALakeFallsInsteadOfSwimming()
        {
            // At the lake's floating depth but outside it, over dry ground: gravity, not a swim.
            MoveState state = MoveState.AtRest(new Vec3(150f, Lake - MovementCore.SwimFloatDepth, 50f), grounded: false);
            var idle = new MoveInput(0f, 0f, 0f, jump: false, sprint: false, crouch: false);

            Assert.True(MovementCore.Step(ref state, in idle, Dt).Y < 0f, "a body beside the lake floated");
        }

        [Fact]
        public void ASwimmerClimbsOutOfALakeUpToItsOwnSurface()
        {
            var forward = new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);
            float halfHeight = MovementCore.StandHeight * 0.5f;

            MoveState state = MoveState.AtRest(new Vec3(50f, Lake - MovementCore.SwimFloatDepth, 50f), grounded: false);
            state.IsBlockedSideways = true;
            Assert.Equal(MovementCore.ClimbOutSpeed * Dt, MovementCore.Step(ref state, in forward, Dt).Y, 4);
            Assert.True(state.IsClimbingOut);

            // Past the lake's lip the climb is over, even though the sea is far below.
            state = MoveState.AtRest(new Vec3(50f, Lake + MovementCore.ClimbOutLip + halfHeight + 0.01f, 50f), grounded: false);
            state.IsBlockedSideways = true;
            state.IsClimbingOut = true;
            Assert.True(MovementCore.Step(ref state, in forward, Dt).Y < 0f, "the climb went on past the lake's lip");
            Assert.False(state.IsClimbingOut);
        }
    }
}
