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
    /// A network-driven helicopter makes no physics query in its fixed step; a simulated one still
    /// probes the ground for the AI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> The probe fed only <c>ShouldBeAvoided</c>, which only the AI asks, and a
    /// network-driven helicopter exists only on a client. Each raycast after a physics step also
    /// synchronised every moved transform first: 0.7 ms a frame for two helicopters in a 100-bot
    /// Forest Lake match (development profile, 2026-10-02).
    /// </para>
    /// <para>
    /// <b>A source scan</b>, for <c>OfflinePlayerTeamTests</c>' reason: <c>Assembly-CSharp</c> is a
    /// file no test assembly compiles.
    /// </para>
    /// </remarks>
    public class RemoteHelicopterProbeTests
    {
        [Fact]
        public void ANetworkDrivenHelicopterMakesNoPhysicsQuery()
        {
            IfStatementSyntax networkDriven = NetworkDrivenBranch();

            Assert.DoesNotContain("Physics.", networkDriven.Statement.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void ASimulatedHelicopterStillProbesTheGroundForTheAi()
        {
            MethodDeclarationSyntax fixedUpdate = FixedUpdate();
            string afterBranch = fixedUpdate.Body!.ToString()
                .Substring(NetworkDrivenBranch().Span.End - fixedUpdate.Body.SpanStart);

            Assert.Contains("isAirborne = !Physics.Raycast", afterBranch, StringComparison.Ordinal);
        }

        private static IfStatementSyntax NetworkDrivenBranch() =>
            FixedUpdate().Body!.Statements.OfType<IfStatementSyntax>()
                .Single(i => i.Condition.ToString() == "NetworkDriven");

        private static MethodDeclarationSyntax FixedUpdate()
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "Helicopter.cs");
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "FixedUpdate");
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
