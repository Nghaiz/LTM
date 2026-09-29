using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for who gets a row in S_PLAYER_SCORES, the table every scoreboard draws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found live on 2026-09-29.</b> The sixteen player slots <c>ServerPlayerSlotPool</c>
    /// parks at startup are all registered actors, and the table listed every one: the board
    /// showed fourteen 0/0 players named "actor 3" to "actor 16" beside two humans and read
    /// "28 PLAYERS" in a match of 14.
    /// </para>
    /// <para>
    /// The behaviour is tested in the EditMode suite (<c>ScoreRowsTests</c>), which CI does not
    /// run; these pin the two lines that carry it, on their source, like the neighbouring
    /// *SourceInvariantTests.
    /// </para>
    /// </remarks>
    public sealed class ScoreTableSourceInvariantTests
    {
        [Fact]
        public void TheBroadcastBuildsItsRowsThroughTheOneRule()
        {
            string emit = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "private void EmitPlayerScores()");

            Assert.Contains("FillScoreRows(", emit, StringComparison.Ordinal);
        }

        [Fact]
        public void AParkedPlayerSlotHasNoRow()
        {
            string fill = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "internal static int FillScoreRows(");

            Assert.Contains("if (!IsAnnounceable(actor)) continue;", fill, StringComparison.Ordinal);
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
