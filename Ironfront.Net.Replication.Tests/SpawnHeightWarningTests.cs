using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The spawn snap's warning names a real authoring mistake and not the original maps' own
    /// spawn heights (2026-09-30).
    /// </summary>
    /// <remarks>
    /// <c>SpawnPoint.SnappedContainerChildPosition</c> puts every authored spawn child on its
    /// ground (ledger X-81) and warns when the correction exceeds a threshold. At 1 m it reported
    /// all 218 spawn children of Dustbowl and Island, which the original game authored 1.0 to
    /// 3.1 m up, as "a scene defect the level author should fix". The threshold must sit above
    /// those heights and below the fault X-81 was about, a child 42.6 m in the air.
    /// </remarks>
    public sealed class SpawnHeightWarningTests
    {
        private const float HighestOriginalSpawnMetres = 3.1f;
        private const float X81FaultMetres = 42.6f;

        [Fact]
        public void TheWarningSitsAboveTheOriginalSpawnHeightsAndBelowARealFault()
        {
            string source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "SpawnPoint.cs"));

            var m = Regex.Match(source, @"ContainerSnapWarnDistanceMetres\s*=\s*([0-9.]+)f\s*;");
            Assert.True(m.Success, "no ContainerSnapWarnDistanceMetres constant in SpawnPoint.cs");

            float threshold = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.True(threshold > HighestOriginalSpawnMetres,
                $"{threshold} m reports the original maps' own spawn heights (up to {HighestOriginalSpawnMetres} m) as defects");
            Assert.True(threshold < X81FaultMetres,
                $"{threshold} m would stay silent about a child {X81FaultMetres} m in the air, the fault X-81 found");
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException("no Ironfront.sln above the working directory");
        }
    }
}
