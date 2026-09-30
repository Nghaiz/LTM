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

        /// <summary>
        /// Every page of the table goes out, each as its own broadcast (2026-09-30). One write of
        /// the whole table did not frame past 87 rows, and the scoreboard stopped.
        /// </summary>
        [Fact]
        public void TheBroadcastSendsEveryPageOfTheTable()
        {
            string emit = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "private void EmitPlayerScores()");

            Assert.Contains("PlayerScoresMessage.PageCountFor(count)", emit, StringComparison.Ordinal);
            Assert.Matches(
                new System.Text.RegularExpressions.Regex(
                    @"for\s*\(\s*int\s+page\s*=\s*0\s*;\s*page\s*<\s*pages\s*;\s*page\+\+\s*\)\s*\{"
                    + @"[\s\S]*?WritePlayerScores\([^)]*,\s*page\)[\s\S]*?BroadcastReliable\("),
                emit);
        }

        /// <summary>
        /// The name half of the scoreboard: a server takes no more connections than S_PLAYER_LIST
        /// can name (2026-09-30), now that the list is bounded by connections instead of by
        /// MAX_ACTORS. Above it, the extra players would play unnamed; the bootstrap refuses to
        /// start instead, on the same path as every other rejected setting.
        /// </summary>
        [Fact]
        public void TheServerRefusesMoreConnectionsThanTheNameListCanName()
        {
            string resolve = MethodBody(
                ReadScript("Net", "Server", "NetServerBootstrap.cs"), "NetServerBootstrap.cs",
                "private void ResolveConfiguration()");

            Assert.Matches(
                new System.Text.RegularExpressions.Regex(
                    @"try\s*\{[\s\S]*?if\s*\(\s*Config\.MaxConnections\s*>\s*PlayerListMessage\.MaxEntries\s*\)"
                    + @"\s*\{\s*throw\s+new\s+InvalidOperationException\("),
                resolve);
            Assert.Contains("catch (InvalidOperationException ex)", resolve, StringComparison.Ordinal);

            string loop = ReadScript("Net", "Server", "ServerTickLoop.cs");
            Assert.Contains("new PlayerListEntry[PlayerListMessage.MaxEntries]", loop, StringComparison.Ordinal);
        }

        [Fact]
        public void AParkedPlayerSlotHasNoRow()
        {
            string fill = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "internal static int FillScoreRows(");

            Assert.Contains("if (!IsAnnounceable(actor)) continue;", fill, StringComparison.Ordinal);
        }

        /// <summary>
        /// A player who joins into a leaver's slot starts at 0/0 rather than wearing the
        /// leaver's row. The reset itself is <c>MatchScoreTallyTests</c>' to test; this pins that
        /// the join path calls it, after the claim that decides which id is being reused.
        /// </summary>
        [Fact]
        public void AClaimedSlotStartsFromZero()
        {
            string join = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "private void OnClientConnected(ushort connectionId, ConnectionInfo info)");

            int claim = join.IndexOf("TryClaimPlayerSlot(", StringComparison.Ordinal);
            int forget = join.IndexOf("_scoreTally.Forget(actor.ActorId);", StringComparison.Ordinal);

            Assert.True(claim >= 0, "OnClientConnected no longer claims a player slot.");
            Assert.True(
                forget > claim,
                "OnClientConnected must zero the claimed body's tally after the claim; without it "
                + "a player who joins into a leaver's slot inherits the leaver's kills and deaths.");
        }

        /// <summary>
        /// A room's bots enter the match by registering, not through a connection, so no join
        /// path marks the table dirty for them; the flush checks the registry's revision itself.
        /// Found in the v1.1.0 release test: the board listed two humans in a match of fourteen
        /// until the first death.
        /// </summary>
        [Fact]
        public void ANewActorSetMarksTheTableDirtyBeforeTheFlush()
        {
            string source = ReadScript("Net", "Server", "ServerTickLoop.cs");

            int revision = source.IndexOf("ServerActorRegistry.Instance.Revision", StringComparison.Ordinal);
            int flush = source.IndexOf("if (_scoresDirty) EmitPlayerScores();", StringComparison.Ordinal);

            Assert.True(revision >= 0, "ServerTickLoop no longer reads the registry's revision.");
            Assert.True(
                flush > revision,
                "the registry's revision must be checked before the score flush, or a bot release "
                + "waits for the first death to reach the board.");
            Assert.Contains(
                "_scoresDirty = true;", source.Substring(revision, flush - revision), StringComparison.Ordinal);
        }

        [Fact]
        public void TheRegistryRevisionMovesOnEveryAddAndRemove()
        {
            string registry = ReadScript("Net", "Server", "ServerActorRegistry.cs");

            string register = MethodBody(registry, "ServerActorRegistry.cs", "public void Register(NetServerActor actor)");
            Assert.Contains("_actors.Add(actor);", register, StringComparison.Ordinal);
            Assert.Contains("Revision++;", register, StringComparison.Ordinal);

            string unregister = MethodBody(registry, "ServerActorRegistry.cs", "public void Unregister(NetServerActor actor)");
            Assert.Contains("if (_actors.Remove(actor)) Revision++;", unregister, StringComparison.Ordinal);
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
