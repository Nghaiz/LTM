using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for the practice match's killfeed and Tab board (owner's run of
    /// 2026-10-12: practice shows them as online, and offline play needs no server and no internet).
    /// </summary>
    /// <remarks>
    /// The behaviour is tested in the EditMode suite (<c>PracticeMatchReadoutTests</c>), which CI
    /// does not run; these pin, on their source, the two properties that make it the online
    /// readout and keep it offline.
    /// </remarks>
    public sealed class PracticeHudSourceInvariantTests
    {
        /// <summary>
        /// One implementation draws both feeds and both boards: a second copy for practice would
        /// drift from the online one the first time either changed.
        /// </summary>
        [Fact]
        public void PracticeAndOnlineDrawWithTheSameCode()
        {
            string online = ReadScript("Net", "Client", "NetClientCombatPresenter.cs");
            string practice = ReadScript("Net", "Client", "Hud", "PracticeMatchReadout.cs");

            foreach (string shared in new[] { "KillfeedLines.For(", "_columns.Write(", "ScoreboardHeader.Build(" })
            {
                Assert.Contains(shared, online, StringComparison.Ordinal);
                Assert.Contains(shared, practice, StringComparison.Ordinal);
            }

            // The rows are the server's projection of its own tally, offline as on the wire.
            Assert.Contains("_tally.RowFor(", practice, StringComparison.Ordinal);
            Assert.Contains("tally.RowFor(", ReadScript("Net", "Server", "ServerTickLoop.cs"), StringComparison.Ordinal);
        }

        /// <summary>
        /// Nothing on the practice path opens a connection. A text guard, so it names the network's
        /// entry points rather than proving an absence; the EditMode suite drives the readout with
        /// no client, no master and no server registered at all.
        /// </summary>
        [Fact]
        public void ThePracticePathOpensNoConnection()
        {
            string[] network = { "NetClientBootstrap", "MasterClient", "UnityWebRequest", "HttpClient", "Socket", "Transport" };

            foreach (string[] file in new[]
                     {
                         new[] { "Net", "Client", "Hud", "PracticeMatchReadout.cs" },
                         new[] { "Net", "Client", "Hud", "PracticeMatchPresenter.cs" },
                         new[] { "Net", "Shared", "PracticeFeed.cs" },
                         new[] { "NetBindings", "PracticeMatchBinding.cs" },
                     })
            {
                string source = ReadScript(file);
                foreach (string entry in network)
                    Assert.DoesNotContain(entry, source, StringComparison.Ordinal);
            }
        }

        private static string ReadScript(params string[] relativeParts)
        {
            var parts = new List<string> { RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts" };
            parts.AddRange(relativeParts);

            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"Expected to find a script at {path}.");
            return File.ReadAllText(path);
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
