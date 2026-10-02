using System;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The per-vehicle and per-bot debug overlays are not compiled into a release client.
    /// </summary>
    /// <remarks>
    /// They draw only with <c>ActorManager.debug</c> on, but Unity calls an <c>OnGUI</c> twice a
    /// frame for every instance that has one: 63 IMGUI passes a frame and 0.6 ms on Forest Lake
    /// (development build profile, 2026-10-02), for nothing on screen. The Editor and development
    /// builds keep them. A source scan: both are <c>Assembly-CSharp</c>.
    /// </remarks>
    public sealed class ReleaseDebugOverlayTests
    {
        private const string Guard = "#if !UNITY_SERVER && (UNITY_EDITOR || DEVELOPMENT_BUILD)";

        [Theory]
        [InlineData("Vehicle.cs")]
        [InlineData("AiActorController.cs")]
        public void TheDebugOverlayIsCompiledOnlyIntoTheEditorAndDevelopmentBuilds(string file)
        {
            string source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", file));

            int method = source.IndexOf("private void OnGUI()", StringComparison.Ordinal);
            Assert.True(method >= 0, $"{file} no longer declares OnGUI; drop it from this test.");

            int guard = source.LastIndexOf("#if", method, StringComparison.Ordinal);
            string line = source.Substring(guard, source.IndexOf('\n', guard) - guard).TrimEnd('\r');
            Assert.Equal(Guard, line);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
