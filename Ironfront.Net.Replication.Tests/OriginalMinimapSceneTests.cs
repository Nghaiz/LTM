using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Island and Dustbowl keep the original game's own minimap; only Forest Lake, which the original
    /// never had, draws a baked one.
    /// </summary>
    /// <remarks>
    /// Owner, 2026-10-11 (list item 2): the baked pictures #609 gave Island and Dustbowl were "drawn by
    /// hand", ugly and wrong, and the maps must be the ones v4.6.0 and earlier showed: the original's
    /// live shot of the level from above. Their night is graded from that shot at run time
    /// (<c>MinimapNightGrade</c>), so Night Mode still has a map. Read off the scene files: the
    /// component lives in Assembly-CSharp, which no test assembly here can load.
    /// </remarks>
    public sealed class OriginalMinimapSceneTests
    {
        [Theory]
        [InlineData("Island")]
        [InlineData("Dustbowl")]
        public void TheOriginalMapsKeepTheirLiveMinimap(string scene)
        {
            string block = MinimapBlock(scene);

            Assert.Contains("bakedPicture: {fileID: 0}", block, StringComparison.Ordinal);
            Assert.Contains("bakedNightPicture: {fileID: 0}", block, StringComparison.Ordinal);
            Assert.Contains("originalLivePicture: 1", block, StringComparison.Ordinal);
        }

        [Fact]
        public void ForestLakeKeepsItsBakedPictures()
        {
            string block = MinimapBlock("ForestLake");

            Assert.Matches(new Regex(@"bakedPicture: \{fileID: 2800000, guid: [0-9a-f]{32}"), block);
            Assert.Matches(new Regex(@"bakedNightPicture: \{fileID: 2800000, guid: [0-9a-f]{32}"), block);
            Assert.DoesNotContain("originalLivePicture: 1", block, StringComparison.Ordinal);
        }

        /// <summary>The serialized MinimapCamera of <paramref name="scene"/>: from its frame margin to the next object.</summary>
        private static string MinimapBlock(string scene)
        {
            string path = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scenes", scene + ".unity");
            Assert.True(File.Exists(path), $"Expected the scene at {path}.");
            string text = File.ReadAllText(path);

            int start = text.IndexOf("  frameMargin: ", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{scene} has no MinimapCamera (frameMargin).");
            Assert.Equal(-1, text.IndexOf("  frameMargin: ", start + 1, StringComparison.Ordinal));
            int end = text.IndexOf("--- !u!", start, StringComparison.Ordinal);
            return text.Substring(start, (end < 0 ? text.Length : end) - start);
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
