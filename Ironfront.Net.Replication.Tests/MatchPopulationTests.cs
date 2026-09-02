using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// How many bodies a match actually contains, checked against the number the V9 load
    /// criteria are written for. Ledger <b>B-16</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every V9 bandwidth figure on record was taken at the wrong population, and nothing
    /// said so.</b> The criteria specify 16 clients and 32 bots. P7 measured 16 clients and
    /// <i>40</i> bots four times, reported the overshoot honestly in prose, and graded B-16
    /// against the number anyway: worst client 6,251 B/s against a 5,120 B/s budget.
    /// </para>
    /// <para>
    /// The 40 was not the map's, which is why it survived being looked for there.
    /// <c>ActorManager</c> declares <c>team0Bots = 16</c> / <c>team1Bots = 16</c> in C#, and
    /// <c>Assets/Resources/_Managers.prefab</c> overrode both to 20. That prefab is loaded by
    /// <c>LevelTester.Awake</c> on every networked run — where the menu that would otherwise
    /// write these fields never executes — so its serialized values ARE the match settings, for
    /// both shipping maps at once.
    /// </para>
    /// <para>
    /// This is data, not code, and no compiler was ever going to notice it drift. So it is
    /// pinned here, beside the criterion it decides.
    /// </para>
    /// </remarks>
    public sealed class MatchPopulationTests
    {
        /// <summary>The V9 load criteria's bot count, split evenly across the two teams.</summary>
        private const int DesignBotsPerTeam = 16;

        [Theory]
        [InlineData("team0Bots")]
        [InlineData("team1Bots")]
        public void TheManagersPrefabAuthorsTheBotCountTheCriteriaAreWrittenFor(string field)
        {
            string prefab = ReadRepoFile("Ironfront_Reborn/Assets/Resources/_Managers.prefab");

            System.Text.RegularExpressions.Match m = Regex.Match(prefab, $@"^\s*{field}:\s*(-?\d+)\s*$", RegexOptions.Multiline);
            Assert.True(m.Success, $"no '{field}' in _Managers.prefab — did the component change?");

            int authored = int.Parse(m.Groups[1].Value);

            Assert.True(
                authored == DesignBotsPerTeam,
                $"_Managers.prefab authors {field}: {authored}, but the V9 load criteria are "
                + $"written for {DesignBotsPerTeam} per team ({DesignBotsPerTeam * 2} bots). "
                + "This prefab decides the population of EVERY networked match, so a change "
                + "here silently re-scopes every bandwidth and tick figure taken afterwards. "
                + "If the design target itself moved, change DesignBotsPerTeam and re-measure "
                + "B-16 — do not change only the prefab.");
        }

        /// <summary>
        /// The C# default and the serialized value agree.
        /// </summary>
        /// <remarks>
        /// The companion to the pin above, and the check that would have caught the original
        /// drift: the prefab sat at 20 while the field it overrides declared 16, and neither
        /// side was wrong on its own terms. Only the disagreement was.
        /// </remarks>
        [Theory]
        [InlineData("team0Bots")]
        [InlineData("team1Bots")]
        public void TheSerializedBotCountAgreesWithTheFieldsOwnDefault(string field)
        {
            string source = ReadRepoFile(
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorManager.cs");
            string prefab = ReadRepoFile("Ironfront_Reborn/Assets/Resources/_Managers.prefab");

            System.Text.RegularExpressions.Match declared = Regex.Match(source, $@"public\s+int\s+{field}\s*=\s*(-?\d+)\s*;");
            Assert.True(declared.Success, $"no 'public int {field}' in ActorManager.cs");

            System.Text.RegularExpressions.Match authored = Regex.Match(
                prefab, $@"^\s*{field}:\s*(-?\d+)\s*$", RegexOptions.Multiline);
            Assert.True(authored.Success, $"no '{field}' in _Managers.prefab");

            Assert.True(
                declared.Groups[1].Value == authored.Groups[1].Value,
                $"ActorManager declares {field} = {declared.Groups[1].Value} and "
                + $"_Managers.prefab serializes {authored.Groups[1].Value}. The prefab wins at "
                + "runtime, so the C# default is documentation that lies. Make them agree.");
        }

        private static string ReadRepoFile(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing file: {path}");
            return File.ReadAllText(path);
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
