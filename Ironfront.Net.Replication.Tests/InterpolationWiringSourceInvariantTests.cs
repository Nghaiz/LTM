using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for how the Unity client draws remote bodies at the render clock.
    /// </summary>
    /// <remarks>
    /// The clock and the samplers are graded in <see cref="InterpolationClockTests"/> and the
    /// interpolation suites. What those cannot see is whether the MonoBehaviours use them: these
    /// files compile into the Unity net assemblies, which no dotnet test project references, so
    /// the wiring is pinned on its source.
    /// </remarks>
    public sealed class InterpolationWiringSourceInvariantTests
    {
        /// <summary>
        /// The bootstrap brings the render clock to the frame before it polls the transport.
        /// </summary>
        /// <remarks>
        /// Polled first, each snapshot's lead would be measured against the previous frame's
        /// render time, and the clock would settle a frame further behind than the delay the lag
        /// compensator assumes — by a different amount at every frame rate.
        /// </remarks>
        [Fact]
        public void TheClockIsAdvancedBeforeTheTransportIsPolled()
        {
            string source = ReadScript("Net", "Client", "NetClientBootstrap.cs");
            string update = MethodBody(source, "NetClientBootstrap.cs", "private void Update()");

            int advance = update.IndexOf("Router.Clock.AdvanceTo(", StringComparison.Ordinal);
            int poll = update.IndexOf(".Poll()", StringComparison.Ordinal);
            Assert.True(advance >= 0 && poll > advance,
                "NetClientBootstrap.Update must advance Router.Clock before polling the transport.");
        }

        /// <summary>
        /// Both readers draw at the router's clock, and neither builds a render tick from the
        /// prediction clock's Alpha again.
        /// </summary>
        /// <remarks>
        /// The 2026-09-27 report's jerky bots and vehicles: <c>newestTick + Alpha - DelayTicks</c>
        /// stepped back a tick every 100 ms, because Alpha wraps at 30 Hz and snapshots land at 20.
        /// </remarks>
        [Theory]
        [InlineData("RemoteActorRegistry.cs", "private void Update()")]
        [InlineData("ClientVehicleStage.cs", "private void DrawRemoteVehicles()")]
        public void RemoteBodiesAreDrawnAtTheRoutersClock(string file, string signature)
        {
            string source = ReadScript("Net", "Client", file);
            string body = MethodBody(source, file, signature);

            Assert.Contains("Router.Clock.AdvanceTo(", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".Alpha", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// A remote vehicle's pose reaches its Transform the frame it is sampled.
        /// </summary>
        /// <remarks>
        /// A <c>Rigidbody</c> write alone reaches the Transform only at the next physics step
        /// (measured in the Editor on 2026-09-27), so every remote vehicle was drawn at the 60 Hz
        /// fixed rate from a pose one step old.
        /// </remarks>
        [Fact]
        public void ARemoteVehiclePoseIsWrittenToItsTransform()
        {
            string source = ReadScript("Net", "Client", "NetClientVehicle.cs");
            string body = MethodBody(source, "NetClientVehicle.cs", "internal void ApplyRemote(in VehiclePose pose)");

            int transform = body.IndexOf("Transform.SetPositionAndRotation(", StringComparison.Ordinal);
            Assert.True(transform >= 0, "NetClientVehicle.ApplyRemote must write the vehicle's Transform.");

            int bodyWrite = body.IndexOf("_rigidbody.position", StringComparison.Ordinal);
            Assert.True(bodyWrite < 0 || transform < bodyWrite,
                "NetClientVehicle.ApplyRemote must write the Transform before the body.");
            Assert.DoesNotContain("else", body, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        private static string ReadScript(params string[] relativeParts)
        {
            var parts = new List<string> { RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts" };
            parts.AddRange(relativeParts);

            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"Expected to find a script at {path}.");
            return File.ReadAllText(path);
        }

        private static string MethodBody(string source, string fileName, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{fileName} no longer declares '{signature}'.");

            int open = source.IndexOf('{', start);
            Assert.True(open >= 0, $"{fileName}: '{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{fileName}: '{signature}' has an unbalanced body.");
            return string.Empty;
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
