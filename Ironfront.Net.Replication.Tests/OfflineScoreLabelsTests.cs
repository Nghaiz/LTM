using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Offline, the scoreboard shows no match phase and no clock: <c>ScoreUi.Awake</c> blanks the
    /// labels only the server's match state writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-02.</b> Practice showed "Playing 0:00" under the scores for the whole
    /// match: the prefab's authored text, because <c>SetAuthoritativeState</c> is their only writer
    /// and nothing sends match state offline.
    /// </para>
    /// <para>
    /// <b>A source scan</b>, for <c>OfflinePlayerTeamTests</c>' reason: <c>Assembly-CSharp</c> is a
    /// file no test assembly compiles.
    /// </para>
    /// </remarks>
    public class OfflineScoreLabelsTests
    {
        [Fact]
        public void OfflineBlanksThePhaseAndTheClock()
        {
            MethodDeclarationSyntax awake = Parse("Assembly-CSharp/ScoreUi.cs")
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Awake");

            IfStatementSyntax offline = awake.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(i => i.Condition.ToString() == "NetContext.IsOffline"
                             && i.Statement.ToString().Contains("UpdateUi()", StringComparison.Ordinal));

            string body = offline.Statement.ToString();
            Assert.Contains("ClearText(phaseText)", body, StringComparison.Ordinal);
            Assert.Contains("ClearText(phaseTimerText)", body, StringComparison.Ordinal);
        }

        private static SyntaxNode Parse(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
