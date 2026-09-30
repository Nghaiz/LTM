using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for how a networked match starts and how its HUD learns the flag counts.
    /// </summary>
    /// <remarks>
    /// Both seams live in Unity code that no netstandard test assembly can reference
    /// (<c>ServerTickLoop</c>, <c>NetClientObjectivePresenter</c>), so they are pinned on their
    /// source, like the neighbouring *SourceInvariantTests.
    /// </remarks>
    public sealed class MatchLifecycleSourceInvariantTests
    {
        /// <summary>
        /// Releasing a room resets its round, so the next room opens on a fresh match.
        /// </summary>
        /// <remarks>
        /// Measured 2026-09-27 on Island: room 22's players timed out mid-round, the server
        /// released the room and kept the match in Playing, bots fought alone for ~11 minutes, and
        /// room 23 joined THAT round at 52/39 with all five capture points already taken — both of
        /// its clients logged "5 of 5 capture point(s) start owned" — and no Warmup, no Playing and
        /// no bot-release gate.
        /// </remarks>
        [Fact]
        public void ReleasingTheRoomResetsItsRound()
        {
            string disconnect = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "private void OnClientDisconnected(ushort connectionId, DisconnectReason reason)");

            int release = disconnect.IndexOf("RoomIdentity.Release()", StringComparison.Ordinal);
            int reset = disconnect.IndexOf("ForceReset()", StringComparison.Ordinal);
            Assert.True(
                release >= 0 && reset > release,
                "OnClientDisconnected must reset the match when the last player releases the "
                + "room; otherwise the next room joins the abandoned round.");
        }

        /// <summary>
        /// A round's end keeps the room: only the last player leaving releases it.
        /// </summary>
        /// <remarks>
        /// The next round follows on this server with the same players still connected, and the
        /// master keeps the room in its match (<c>RoundEndKeepsTheRoomTests</c>). Releasing the room
        /// at <c>MatchEnded</c> let the next room allocated to this server have its tickets adopted
        /// into a round the first room's players were still playing.
        /// </remarks>
        [Fact]
        public void ARoundThatEndsKeepsItsRoom()
        {
            string ended = MethodBody(
                ReadScript("Net", "Server", "ServerMasterReporter.cs"), "ServerMasterReporter.cs",
                "private void OnMatchEnded(byte winningTeam)");

            Assert.Contains("Reporter.MatchEnded(RoomId", ended, StringComparison.Ordinal);
            Assert.DoesNotContain("RoomIdentity.Release()", ended, StringComparison.Ordinal);
        }

        /// <summary>
        /// The HUD's flag counts are pushed every frame, not only when a capture point changes.
        /// </summary>
        /// <remarks>
        /// The join replays every capture point before GameManager.StartGame builds the HUD, so an
        /// edge-triggered push landed on nothing, the view latched the values, and a point that
        /// never changed owner was never sent again: the loadout screen showed x0 / x0 with flags
        /// visibly owned. The push must come BEFORE the match-state early return, which is not
        /// satisfied until the first S_MATCH_STATE arrives.
        /// </remarks>
        [Fact]
        public void FlagCountsArePushedEveryFrame()
        {
            string update = MethodBody(
                ReadScript("Net", "Client", "NetClientObjectivePresenter.cs"),
                "NetClientObjectivePresenter.cs", "private void Update()");

            int push = update.IndexOf("RecomputeCapturePointCounts()", StringComparison.Ordinal);
            int waitForState = update.IndexOf("_model.HasState", StringComparison.Ordinal);
            Assert.True(
                push >= 0 && waitForState > push,
                "NetClientObjectivePresenter.Update must push the capture-point counts every frame, "
                + "ahead of the HasState early return.");
        }

        /// <summary>
        /// A player who is dead when the round resets can deploy in the next one.
        /// </summary>
        /// <remarks>
        /// Measured 2026-09-28 on the Azure Island server: the reset cleared the respawn gate
        /// while the player's body stayed dead, <c>ServerCombatBridge.TryRespawn</c> refused a
        /// death the gate no longer knew about, and three deploy requests from one client were
        /// dropped silently for a whole round. The behaviour is pinned by
        /// <c>RespawnAcrossRoundResetTests</c>; this pins that the reset uses it.
        /// </remarks>
        [Fact]
        public void TheRoundResetCarriesDeadPlayersOverAsReadyToDeploy()
        {
            string reset = MethodBody(
                ReadScript("Net", "Server", "ServerTickLoop.cs"), "ServerTickLoop.cs",
                "public void ResetForNewMatch()");

            Assert.Contains("_respawnGate.ResetForNewRound(", reset, StringComparison.Ordinal);
            Assert.DoesNotContain("_respawnGate.Reset();", reset, StringComparison.Ordinal);
            Assert.Contains("!body.IsAlive", reset, StringComparison.Ordinal);
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
