using System;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Water slows a body that walks through it, and stops it sprinting from the waist down.
    /// </summary>
    /// <remarks>
    /// Owner report 2026-10-01: "wading and swimming have to be told apart". Water up to the chest
    /// was walked through at full speed and sprinted through at 6.5 m/s, and the body then switched to
    /// a 2.4 m/s swim in one step. These pin the curve <see cref="MovementCore.WadingSpeed"/> draws,
    /// through <see cref="MovementCore.Step"/>, which a player's client predicts and its server replays.
    /// </remarks>
    [Collection(WaterSurfaceCollection.Name)]
    public sealed class WadingTests : IDisposable
    {
        private const float Dt = 1f / 30f;
        private const float Water = 10f;

        public WadingTests()
        {
            MovementCore.WaterHeight = Water;
        }

        public void Dispose()
        {
            MovementCore.WaterHeight = float.NegativeInfinity;
            MovementCore.BoundedWater = null;
        }

        [Fact]
        public void AnkleDeepWaterCostsNothing()
        {
            Assert.Equal(MovementCore.WalkSpeed, SpeedIn(depth: 0.2f, sprint: false), 3);
            Assert.Equal(MovementCore.RunSpeed, SpeedIn(depth: 0.2f, sprint: true), 3);
        }

        [Fact]
        public void DeeperWaterSlowsAWalkerSteadily()
        {
            float knee = SpeedIn(0.5f, sprint: false);
            float waist = SpeedIn(0.9f, sprint: false);
            float chest = SpeedIn(1.3f, sprint: false);

            Assert.True(knee < MovementCore.WalkSpeed, $"knee-deep walk {knee}");
            Assert.True(waist < knee, $"waist {waist} vs knee {knee}");
            Assert.True(chest < waist, $"chest {chest} vs waist {waist}");
            Assert.True(chest >= MovementCore.WalkSpeed * MovementCore.WadeSlowestFactor - 0.001f, $"chest {chest}");
        }

        [Fact]
        public void NobodySprintsFromTheWaistDown()
        {
            Assert.True(SpeedIn(0.5f, sprint: true) > SpeedIn(0.5f, sprint: false),
                "knee-deep water should still let a body run");
            Assert.Equal(SpeedIn(0.9f, sprint: false), SpeedIn(0.9f, sprint: true), 3);
        }

        [Fact]
        public void AtTheDeepestWadeSwimmingIsTheFasterWayAcross()
        {
            float deepest = SpeedIn(MovementCore.SwimStartDepth - 0.01f, sprint: true);

            Assert.True(deepest < MovementCore.SwimSpeed,
                $"a body wading chest-deep at {deepest} m/s outpaces a swimmer at {MovementCore.SwimSpeed}");
        }

        [Fact]
        public void DryLandAndLandOverTheWaterlineAreUnchanged()
        {
            Assert.Equal(MovementCore.RunSpeed, SpeedIn(depth: -3f, sprint: true), 3);

            MovementCore.WaterHeight = float.NegativeInfinity;
            Assert.Equal(MovementCore.RunSpeed, SpeedIn(depth: 1f, sprint: true), 3);
        }

        [Fact]
        public void TheDepthIsMeasuredFromTheFeetOfTheStanceTheBodyIsIn()
        {
            var standing = new Vec3(0f, Water - 1f + MovementCore.StandHeight * 0.5f, 0f);
            Assert.Equal(1f, MovementCore.WadeDepth(in standing, crouching: false), 3);

            var crouched = new Vec3(0f, Water - 1f + MovementCore.CrouchHeight * 0.5f, 0f);
            Assert.Equal(1f, MovementCore.WadeDepth(in crouched, crouching: true), 3);
        }

        [Fact]
        public void ABotWadesByThePlayersNumbers()
        {
            // A source scan: Assembly-CSharp is compiled by no test assembly.
            string actor = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "Actor.cs"));

            Assert.Contains("Vector3 vector = WadingVelocity(controller.Velocity());", actor, StringComparison.Ordinal);
            Assert.Contains("MovementCore.WadingSpeed(speed, depth)", actor, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            for (var d = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException("Ironfront.sln not found");
        }

        /// <summary>Horizontal speed, m/s, of one tick walking forward with <paramref name="depth"/> m of water over the feet.</summary>
        private static float SpeedIn(float depth, bool sprint)
        {
            MoveState state = MoveState.AtRest(
                new Vec3(0f, Water - depth + MovementCore.StandHeight * 0.5f, 0f), grounded: true);
            Assert.False(MovementCore.IsInWater(in state.Position), $"{depth} m of water is a swim, not a wade");

            var input = new MoveInput(0f, 1f, 0f, jump: false, sprint: sprint, crouch: false);
            Vec3 motion = MovementCore.Step(ref state, in input, Dt);
            return MathF.Sqrt(motion.X * motion.X + motion.Z * motion.Z) / Dt;
        }
    }
}
