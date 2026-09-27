using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for how often a bot moves on the server.
    /// </summary>
    /// <remarks>
    /// <c>Actor</c> compiles into <c>Assembly-CSharp</c>, which no test assembly can reference
    /// (ledger E-11b), and the behaviour turns on <c>Renderer.isVisible</c>, which is false for
    /// every renderer in a -nographics process and cannot be faked in an EditMode test — so the
    /// guard is pinned on its source.
    /// </remarks>
    public sealed class BotMotionSourceInvariantTests
    {
        /// <summary>
        /// A server never puts a bot on the 5 Hz low-quality update path.
        /// </summary>
        /// <remarks>
        /// The 2026-09-27 Island report: bots moved in jumps. Island ships an enabled camera
        /// tagged MainCamera, so the <c>Camera.main == null</c> guard never fired on the headless
        /// server, <c>isVisible</c> was false for every bot, and every bot ran
        /// <c>UpdateFacing</c>/<c>UpdateMovement</c> once per 0.2 s. The jumps were in the
        /// authoritative positions, where no client interpolation can reach them.
        /// </remarks>
        [Fact]
        public void AServerNeverUpdatesABotAtLowQuality()
        {
            string source = ReadScript("Assembly-CSharp", "Actor.cs");
            string body = MethodBody(source, "Actor.cs", "public bool IsLowQuality()");

            int guard = body.IndexOf("if (NetContext.IsServer)", StringComparison.Ordinal);
            Assert.True(guard >= 0, "Actor.IsLowQuality must answer false on a server.");

            int answer = body.IndexOf("return", guard, StringComparison.Ordinal);
            Assert.True(
                answer >= 0
                && string.CompareOrdinal(body, answer, "return false;", 0, "return false;".Length) == 0,
                "Actor.IsLowQuality's server guard must return false, not true.");

            // ".isVisible", not "isVisible": the guard's own comment names the property.
            int visibility = body.IndexOf(".isVisible", StringComparison.Ordinal);
            Assert.True(
                visibility < 0 || guard < visibility,
                "Actor.IsLowQuality must leave a server before any visibility test: nothing renders there.");
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
