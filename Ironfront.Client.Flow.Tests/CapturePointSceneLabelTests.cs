using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Every capture point on the three shipped maps carries its name (owner request 2026-10-09),
    /// read straight off the scene files, so a scene rebuild or a re-authored point that drops its
    /// <c>mapLabel</c> fails here instead of quietly drawing an unnamed flag.
    /// </summary>
    /// <remarks>
    /// The names are asserted by identity, both ways: a point that lost its label, a label that
    /// changed, and a point added without one each fail with the map and the difference spelled out.
    /// </remarks>
    public sealed class CapturePointSceneLabelTests
    {
        public static IEnumerable<object[]> Maps()
        {
            yield return new object[] { "ForestLake", new[] { "Ford", "Island", "Valley Camp", "Ridge Camp", "Lakeshore", "Quarry", "Meadow", "Lumber Camp" } };
            yield return new object[] { "Dustbowl", new[] { "Fortress", "Bridge", "Town", "Oasis", "Outpost", "Mine" } };
            yield return new object[] { "Island", new[] { "Backside", "Landing", "Farm", "Fort", "Beach" } };
        }

        [Theory]
        [MemberData(nameof(Maps))]
        public void EveryPointOnTheMapIsNamed(string scene, string[] expected)
        {
            List<string?> labels = AuthoredLabels(scene);

            Assert.True(labels.Count > 0, $"{scene}: found no CapturePoint in the scene; has the script's guid changed?");
            Assert.All(labels, label => Assert.NotNull(CapturePointLabelRules.Wording(label)));

            string[] found = labels.Select(label => label!.Trim()).ToArray();
            string[] missing = expected.Except(found).ToArray();
            string[] unexpected = found.Except(expected).ToArray();
            Assert.True(missing.Length == 0 && unexpected.Length == 0,
                $"{scene}: point names changed. Missing: [{string.Join(", ", missing)}]. "
                + $"Not expected: [{string.Join(", ", unexpected)}]. A rename is fine; update this list with it.");
            Assert.Equal(found.Length, found.Distinct().Count());
        }

        /// <summary>Each CapturePoint component's <c>mapLabel</c> in the scene, null where it has none.</summary>
        private static List<string?> AuthoredLabels(string scene)
        {
            string assets = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets");
            string meta = File.ReadAllText(Path.Combine(assets, "Scripts", "Assembly-CSharp", "CapturePoint.cs.meta"));
            string guid = Regex.Match(meta, @"guid: ([0-9a-f]{32})").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(guid), "CapturePoint.cs.meta carries no guid");

            string text = File.ReadAllText(Path.Combine(assets, "Scenes", scene + ".unity"));
            var labels = new List<string?>();
            foreach (string document in Regex.Split(text, @"\n--- !u!"))
            {
                if (!document.StartsWith("114 ", StringComparison.Ordinal) || !document.Contains("guid: " + guid))
                {
                    continue;
                }
                Match label = Regex.Match(document, @"\n  mapLabel: ?([^\r\n]*)");
                labels.Add(label.Success ? label.Groups[1].Value : null);
            }
            return labels;
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }
            throw new InvalidOperationException("Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
