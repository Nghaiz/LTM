using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Layout facts about the in-match HUD, read from the prefab the game actually loads.
    /// </summary>
    /// <remarks>
    /// The prefab is written by <c>BuildMatchHud</c>, an Editor script no netstandard test can
    /// run, so the asset it produced is what is checked -- the thing a player sees.
    /// </remarks>
    public sealed class HudLayoutAssetTests
    {
        private const string PrefabPath = "Ironfront_Reborn/Assets/Prefab/Ingame UI Container.prefab";

        /// <summary>
        /// The team readout sits under the original HUD's flag capture indicator, not inside it.
        /// </summary>
        /// <remarks>
        /// Live test 2026-09-30: the readout was anchored at the top-left corner (36, -36), inside
        /// the capture indicator's area, so standing in a capture zone drew the flag over
        /// "BLUE TEAM". The two live on Canvases that scale differently, so the only placement
        /// that holds at every resolution is the indicator's own bottom anchor line.
        /// </remarks>
        [Fact]
        public void TheTeamReadoutSitsUnderTheFlagCaptureIndicator()
        {
            string yaml = File.ReadAllText(Path.Combine(RepoRoot(), PrefabPath));

            RectFacts flag = RectOf(yaml, "Flag Capture Indicator Edge");
            RectFacts readout = RectOf(yaml, "Team Readout");

            Assert.Equal(flag.AnchorMinY, readout.AnchorMinY, 3);
            Assert.Equal(flag.AnchorMinY, readout.AnchorMaxY, 3);
            Assert.Equal(1f, readout.PivotY, 3);
            Assert.True(readout.AnchoredY <= 0f, "the readout must hang below the indicator's bottom edge");
        }

        // ------------------------------------------------------------------ helpers

        private readonly struct RectFacts
        {
            public RectFacts(float anchorMinY, float anchorMaxY, float pivotY, float anchoredY)
            {
                AnchorMinY = anchorMinY;
                AnchorMaxY = anchorMaxY;
                PivotY = pivotY;
                AnchoredY = anchoredY;
            }

            public float AnchorMinY { get; }

            public float AnchorMaxY { get; }

            public float PivotY { get; }

            public float AnchoredY { get; }
        }

        /// <summary>The RectTransform of the one GameObject called <paramref name="name"/>.</summary>
        private static RectFacts RectOf(string yaml, string name)
        {
            var documents = new Dictionary<string, (string Class, string Body)>();
            foreach (string document in Regex.Split(yaml, @"\n(?=--- !u!)"))
            {
                RegexMatch header = Regex.Match(document, @"^--- !u!(\d+) &(-?\d+)");
                if (header.Success) documents[header.Groups[2].Value] = (header.Groups[1].Value, document);
            }

            string? gameObject = null;
            foreach (KeyValuePair<string, (string Class, string Body)> entry in documents)
            {
                if (entry.Value.Class != "1") continue;
                if (Regex.IsMatch(entry.Value.Body, @"\n  m_Name: " + Regex.Escape(name) + @"\r?\n"))
                {
                    Assert.True(gameObject == null, $"more than one GameObject is called '{name}'");
                    gameObject = entry.Key;
                }
            }

            Assert.True(gameObject != null, $"no GameObject called '{name}' in {PrefabPath}");

            foreach ((string Class, string Body) document in documents.Values)
            {
                if (document.Class != "224") continue;
                if (!document.Body.Contains("m_GameObject: {fileID: " + gameObject + "}", StringComparison.Ordinal)) continue;

                return new RectFacts(
                    Y(document.Body, "m_AnchorMin"),
                    Y(document.Body, "m_AnchorMax"),
                    Y(document.Body, "m_Pivot"),
                    Y(document.Body, "m_AnchoredPosition"));
            }

            Assert.Fail($"'{name}' has no RectTransform in {PrefabPath}");
            return default;
        }

        private static float Y(string body, string field)
        {
            RegexMatch match = Regex.Match(body, field + @": \{x: [-0-9.eE]+, y: ([-0-9.eE]+)\}");
            Assert.True(match.Success, $"no {field} in the RectTransform");
            return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
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
