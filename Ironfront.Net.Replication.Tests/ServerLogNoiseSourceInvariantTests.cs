using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for game-server log noise found in live tests: the two floods of
    /// 2026-09-30 (B4) and the zero look direction of 2026-10-01.
    /// </summary>
    /// <remarks>
    /// Both live in Unity code no netstandard test assembly can reference, so they are pinned on
    /// their source, like the neighbouring *SourceInvariantTests.
    /// </remarks>
    public sealed class ServerLogNoiseSourceInvariantTests
    {
        /// <summary>
        /// The <c>OnGUI</c> methods of the components a map brings up are not compiled into the
        /// dedicated server.
        /// </summary>
        /// <remarks>
        /// IMGUI is stripped from the server build, and Unity then logs "OnGUI function detected
        /// on MonoBehaviour, but not called" once per instance: 402 lines in one 100-bot match,
        /// because every bot carries an <c>AiActorController</c>. The first three are on every
        /// bot, every scoped weapon and every vehicle. <c>AstarPath</c> is the scene singleton
        /// every map carries, and after those three it was the line's only source: one per map
        /// load (a server build of 2026-10-01 logged none once it was guarded).
        /// </remarks>
        [Theory]
        [InlineData("AiActorController.cs")]
        [InlineData("ScopedWeapon.cs")]
        [InlineData("Vehicle.cs")]
        [InlineData("AstarPath.cs")]
        public void OnGuiIsNotCompiledIntoTheServer(string file)
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

        /// <summary>
        /// A bot never turns to look at the point it stands on.
        /// </summary>
        /// <remarks>
        /// <c>LookAt</c> of the bot's own position hands <c>Quaternion.LookRotation</c> a zero
        /// vector: the bot turns to face world north and Unity logs "Look rotation viewing vector
        /// is zero". A v3.0.0 Island server logged it 12 times in one match. Stack traces from an
        /// offline Island match put every one on two callers: a delayed hail that fires after the
        /// bot has become its squad's leader, and damage that carries no direction.
        /// </remarks>
        [Fact]
        public void BotsDoNotLookAtThePointTheyStandOn()
        {
            string source = ReadScript("Assembly-CSharp", "AiActorController.cs");

            AssertGuardedLook(
                MethodBody(source, "AiActorController.cs", "public void EmoteHailLeader()"),
                "!IsSquadLeader()",
                "EmoteHailLeader must not hail when the bot leads its squad: the hail is delayed, and a rogue split can make the bot leader of a squad of one before it fires.");
            AssertGuardedLook(
                MethodBody(source, "AiActorController.cs", "public override void ReceivedDamage("),
                "direction != Vector3.zero",
                "ReceivedDamage must not turn for damage with no direction: the offline ragdoll timeout deals it at the bot's own position.");
        }

        /// <summary>
        /// No scene or prefab carries a component with no script at all.
        /// </summary>
        /// <remarks>
        /// Unity logs "The referenced script on this Behaviour ... is missing!" each time such a
        /// component loads. Island's "Relevant Graph" carried one from the decompiled import, next
        /// to the RelevantGraphSurface it really has; the recovered original has only the latter.
        /// A script deleted later keeps its guid, so this checks only the reference that was
        /// never a script.
        /// </remarks>
        [Fact]
        public void NoSceneOrPrefabCarriesAComponentWithNoScript()
        {
            string assets = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets");
            var offenders = new List<string>();
            foreach (string path in Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories))
            {
                if (!path.EndsWith(".unity", StringComparison.Ordinal) && !path.EndsWith(".prefab", StringComparison.Ordinal))
                    continue;
                if (File.ReadAllText(path).Contains("m_Script: {fileID: 0}", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(assets, path));
            }

            Assert.True(offenders.Count == 0, "components with no script: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// A dedicated server build switches the terrain's renderer off, so the ground bots stand
        /// on must not come from the list of enabled terrains.
        /// </summary>
        /// <remarks>
        /// Bringing a <c>Terrain</c> up on the server logged three "Trying to access a shader"
        /// lines per map load, so <c>ServerBuildSceneStrip</c> turns it off in server builds.
        /// <c>Terrain.GetActiveTerrains</c> lists only enabled terrains: a <c>TerrainSurface</c>
        /// reading it would find no ground on the server, and a bot that fell into a hillside
        /// would no longer be stood back on it. Its EditMode tests say the same, but those do not
        /// run in CI; this does. Neither half may change without the other.
        /// </remarks>
        [Fact]
        public void ServerTerrainsHaveNoRendererAndBotsReadTheirCollider()
        {
            string strip = ReadScript("..", "Editor", "ServerBuildSceneStrip.cs");
            Assert.Contains("terrain.enabled = false", strip, StringComparison.Ordinal);

            string surface = ReadScript("Net", "Shared", "TerrainSurface.cs");
            Assert.DoesNotContain("GetActiveTerrains(", surface, StringComparison.Ordinal);
            Assert.DoesNotContain("Terrain.activeTerrain", surface, StringComparison.Ordinal);
            Assert.Contains("FindObjectsByType<TerrainCollider>", surface, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertGuardedLook(string body, string guard, string message)
        {
            int look = body.IndexOf("LookAt(", StringComparison.Ordinal);
            Assert.True(look >= 0, $"the method no longer calls LookAt; revisit this test. Body: {body}");

            int at = body.IndexOf(guard, StringComparison.Ordinal);
            Assert.True(at >= 0 && at < look, message);
        }

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
