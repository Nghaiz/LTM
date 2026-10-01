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
    /// A spawn-button update that arrives before the minimap is built is skipped quietly.
    /// </summary>
    /// <remarks>
    /// A flag change or a snapshot can reach <c>MinimapUi.UpdateSpawnPointButtons</c> while the map
    /// is still loading, before <c>Start</c> has built the button map; <c>Start</c> then applies
    /// the owners as they stand. Every client log nonetheless carried "UpdateSpawnPointButtons ran
    /// before SetupMinimap built its button map" at every map load (tmp/after-client-1.log, line
    /// 208). The warning now speaks only after <c>Start</c>, where a missing map is a real fault.
    /// A source scan, like <c>BoatPathTests</c>: <c>MinimapUi</c> is <c>Assembly-CSharp</c>.
    /// </remarks>
    public class MinimapEarlyUpdateTests
    {
        [Fact]
        public void OnlyAMissingButtonMapAfterStartIsWarnedAbout()
        {
            SyntaxNode root = Parse("Assembly-CSharp/MinimapUi.cs");
            MethodDeclarationSyntax start = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Start");
            MethodDeclarationSyntax update = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "UpdateSpawnPointButtons" && m.ParameterList.Parameters.Count == 1);

            string startBody = start.Body!.ToString();
            int setup = startBody.IndexOf("SetupMinimap();", StringComparison.Ordinal);
            int started = startBody.IndexOf("started = true;", StringComparison.Ordinal);
            Assert.True(setup >= 0 && started > setup, "Start must mark itself started after SetupMinimap");

            string updateBody = update.Body!.ToString();
            int gate = updateBody.IndexOf("if (instance.started)", StringComparison.Ordinal);
            int warn = updateBody.IndexOf("WarnOnce(", StringComparison.Ordinal);
            Assert.True(warn >= 0, "UpdateSpawnPointButtons no longer warns; revisit this test");
            Assert.True(gate >= 0 && gate < warn, "the missing-map warning must wait for Start");
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
