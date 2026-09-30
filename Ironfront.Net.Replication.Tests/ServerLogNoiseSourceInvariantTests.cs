using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for the two game-server log floods the 2026-09-30 live test found (B4).
    /// </summary>
    /// <remarks>
    /// Both live in Unity code no netstandard test assembly can reference, so they are pinned on
    /// their source, like the neighbouring *SourceInvariantTests.
    /// </remarks>
    public sealed class ServerLogNoiseSourceInvariantTests
    {
        /// <summary>
        /// The per-instance <c>OnGUI</c> methods are not compiled into the dedicated server.
        /// </summary>
        /// <remarks>
        /// IMGUI is stripped from the server build, and Unity then logs "OnGUI function detected
        /// on MonoBehaviour, but not called" once per instance: 402 lines in one 100-bot match,
        /// because every bot carries an <c>AiActorController</c>. These three are on every bot,
        /// every scoped weapon and every vehicle; scene singletons log once and are left alone.
        /// </remarks>
        [Theory]
        [InlineData("AiActorController.cs")]
        [InlineData("ScopedWeapon.cs")]
        [InlineData("Vehicle.cs")]
        public void PerInstanceOnGuiIsNotCompiledIntoTheServer(string file)
        {
            string source = ReadScript("Assembly-CSharp", file);

            int method = source.IndexOf("private void OnGUI()", StringComparison.Ordinal);
            Assert.True(method >= 0, $"{file} no longer declares OnGUI; drop it from this test.");

            int guard = source.LastIndexOf("#if", method, StringComparison.Ordinal);
            Assert.True(
                guard >= 0 && source.Substring(guard).StartsWith("#if !UNITY_SERVER", StringComparison.Ordinal),
                $"{file}: OnGUI must sit inside #if !UNITY_SERVER, or the server logs once per instance.");

            int end = source.IndexOf("#endif", method, StringComparison.Ordinal);
            string body = MethodBody(source, file, "private void OnGUI()");
            int bodyEnd = source.IndexOf(body, method, StringComparison.Ordinal) + body.Length;
            Assert.True(end >= bodyEnd, $"{file}: the #endif must close after OnGUI's body.");
        }

        /// <summary>
        /// The empty-vehicle-table warning is about a join, not about every snapshot.
        /// </summary>
        /// <remarks>
        /// <c>AnnounceNewVehicles</c> runs for every client on every snapshot, and a round reset
        /// empties the table for a moment: the warning fired every tick for every client there,
        /// hundreds of lines in one second, while every vehicle came back a moment later.
        /// </remarks>
        [Fact]
        public void TheEmptyVehicleTableWarningIsAboutAJoin()
        {
            string source = ReadScript("Net", "Server", "ServerTickLoop.cs");

            string announce = MethodBody(
                source, "ServerTickLoop.cs",
                "private void AnnounceNewVehicles(ClientSession session, bool warnIfEmpty)");
            Assert.Contains("liveCount == 0 && warnIfEmpty", announce, StringComparison.Ordinal);

            int call = source.IndexOf(
                "AnnounceNewVehicles(session, warnIfEmpty: !_players[i].VehicleTableChecked);",
                StringComparison.Ordinal);
            int mark = source.IndexOf("_players[i].VehicleTableChecked = true;", StringComparison.Ordinal);
            Assert.True(
                call >= 0 && mark > call,
                "the catch-up must warn only on a player's first pass, then remember it has run");
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
