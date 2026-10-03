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
        /// The team readout and the flag capture indicator share one row in the top-right corner,
        /// side by side, never one over the other.
        /// </summary>
        /// <remarks>
        /// Owner request 2026-10-03: the top-left corner went to the round radar, and the flag and
        /// the team plate moved right, on one row. Live test 2026-09-30 is why "never over": the
        /// readout once sat inside the indicator, so standing in a capture zone drew the flag over
        /// "BLUE TEAM".
        /// </remarks>
        [Fact]
        public void TheTeamReadoutAndTheFlagIndicatorShareTheTopRightRow()
        {
            string yaml = File.ReadAllText(Path.Combine(RepoRoot(), PrefabPath));

            RectFacts flag = RectOf(yaml, "Flag Capture Indicator Edge");
            RectFacts readout = RectOf(yaml, "Team Readout");

            foreach (RectFacts rect in new[] { flag, readout })
            {
                Assert.Equal(1f, rect.AnchorMin.X, 3);
                Assert.Equal(1f, rect.AnchorMin.Y, 3);
                Assert.Equal(1f, rect.AnchorMax.X, 3);
                Assert.Equal(1f, rect.AnchorMax.Y, 3);
                Assert.Equal(1f, rect.Pivot.X, 3);
                Assert.Equal(1f, rect.Pivot.Y, 3);
            }

            // One row: the same top edge and the same height.
            Assert.Equal(flag.Anchored.Y, readout.Anchored.Y, 3);
            Assert.Equal(flag.Size.Y, readout.Size.Y, 3);

            // The flag holds the corner; the readout ends to the left of the flag's left edge.
            float flagLeft = flag.Anchored.X - flag.Size.X;
            Assert.True(readout.Anchored.X <= flagLeft - 4f,
                $"the readout's right edge ({readout.Anchored.X}) must clear the flag's left edge ({flagLeft})");
        }

        /// <summary>
        /// The top-right row ends above the killfeed, which starts lower down the same corner.
        /// </summary>
        [Fact]
        public void TheTopRightRowEndsAboveTheKillfeed()
        {
            string yaml = File.ReadAllText(Path.Combine(RepoRoot(), PrefabPath));

            RectFacts readout = RectOf(yaml, "Team Readout");
            RectFacts killfeed = RectOf(yaml, "Killfeed");

            Assert.Equal(1f, killfeed.AnchorMin.Y, 3);
            Assert.Equal(1f, killfeed.Pivot.Y, 3);

            float rowBottom = readout.Anchored.Y - readout.Size.Y;
            Assert.True(rowBottom > killfeed.Anchored.Y + 8f,
                $"the row's bottom ({rowBottom}) must sit above the killfeed's top ({killfeed.Anchored.Y})");
        }

        // ------------------------------------------------------------------ helpers

        private readonly struct Pair
        {
            public Pair(float x, float y)
            {
                X = x;
                Y = y;
            }

            public float X { get; }

            public float Y { get; }
        }

        private readonly struct RectFacts
        {
            public RectFacts(Pair anchorMin, Pair anchorMax, Pair pivot, Pair anchored, Pair size)
            {
                AnchorMin = anchorMin;
                AnchorMax = anchorMax;
                Pivot = pivot;
                Anchored = anchored;
                Size = size;
            }

            public Pair AnchorMin { get; }

            public Pair AnchorMax { get; }

            public Pair Pivot { get; }

            public Pair Anchored { get; }

            public Pair Size { get; }
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
                    Read(document.Body, "m_AnchorMin"),
                    Read(document.Body, "m_AnchorMax"),
                    Read(document.Body, "m_Pivot"),
                    Read(document.Body, "m_AnchoredPosition"),
                    Read(document.Body, "m_SizeDelta"));
            }

            Assert.Fail($"'{name}' has no RectTransform in {PrefabPath}");
            return default;
        }

        private static Pair Read(string body, string field)
        {
            RegexMatch match = Regex.Match(body, field + @": \{x: ([-0-9.eE]+), y: ([-0-9.eE]+)\}");
            Assert.True(match.Success, $"no {field} in the RectTransform");
            return new Pair(
                float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
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
