using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The player settings the P31 client-performance work chose, each for a measured reason.
    /// </summary>
    /// <remarks>
    /// Measured on Forest Lake with 100 bots, focused client, VSync off (RTX 4060 Laptop, Ryzen 9
    /// 7945HX), 2026-10-02. See <c>plans/phases/phase-p31-client-performance.md</c>. These are read
    /// straight out of <c>ProjectSettings.asset</c>: a setting flipped back in the Editor's Player
    /// Settings window goes red here instead of quietly costing frames.
    /// </remarks>
    public sealed class ReleasePlayerSettingsTests
    {
        [Fact]
        public void WindowsRunsLegacyGraphicsJobs()
        {
            // Mid-match, 100 bots: jobs off 20.3 fps / 49.4 ms / 461 hitches a minute; Legacy jobs
            // 24.5 fps / 40.8 ms / 154. DX11 offers only Legacy; Split needs DX12.
            string settings = PlayerSettings();
            Assert.Matches(new Regex(@"- m_BuildTarget: WindowsStandaloneSupport\r?\n\s+m_GraphicsJobs: 1"), settings);
            Assert.Matches(new Regex(@"- m_BuildTarget: WindowsStandaloneSupport\r?\n\s+m_GraphicsJobMode: 1"), settings);
        }

        [Fact]
        public void SoldiersAreSkinnedOnTheGpuInBatches()
        {
            Assert.Contains("  meshDeformation: 2", PlayerSettings(), StringComparison.Ordinal);
        }

        [Fact]
        public void MeshCollidersAreCookedAtBuildTime()
        {
            Assert.Contains("  bakeCollisionMeshes: 1", PlayerSettings(), StringComparison.Ordinal);
        }

        [Fact]
        public void LogAndWarningCarryNoStackTraceButErrorsDo()
        {
            // Six little-endian ints in LogType order: Error, Assert, Warning, Log, Exception, (all).
            Assert.Contains("  m_StackTraceTypes: 010000000100000000000000000000000100000001000000",
                PlayerSettings(), StringComparison.Ordinal);
        }

        [Fact]
        public void TheReleaseBuildsIl2CppForSpeedWithLineNumbers()
        {
            string settings = PlayerSettings();
            Assert.Matches(new Regex(@"il2cppCompilerConfiguration:\r?\n\s+Standalone: 1"), settings);   // Release
            Assert.Matches(new Regex(@"il2cppCodeGeneration:\r?\n\s+Standalone: 0"), settings);          // faster runtime
            Assert.Matches(new Regex(@"il2cppStacktraceInformation:\r?\n\s+Standalone: 1"), settings);   // file and line
        }

        private static string PlayerSettings()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Ironfront_Reborn", "ProjectSettings", "ProjectSettings.asset"));

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
