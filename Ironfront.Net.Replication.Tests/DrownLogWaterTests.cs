using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The drowning log's <c>water=</c> is the surface over the body's feet, lake as well as sea.
    /// </summary>
    /// <remarks>
    /// It printed <c>WaterLevel.height</c>, the sea's alone, so every drowning on Forest Lake --
    /// whose water is all lakes -- read <c>water=-Infinity</c> beside a crown depth of 0.24 m
    /// (v3.1.1 server log, 2026-10-01). A source scan, for <c>OfflinePlayerTeamTests</c>' reason:
    /// the binding compiles into <c>Assembly-CSharp</c>.
    /// </remarks>
    public class DrownLogWaterTests
    {
        [Fact]
        public void TheWaterFieldIsTheSurfaceOverTheFeet()
        {
            string body = DescribeSubmersionBody();

            Assert.Contains("WaterLevel.BoundedSurfaceAt(feet.x, feet.z)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("water={WaterLevel.height", body, StringComparison.Ordinal);
        }

        private static string DescribeSubmersionBody()
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "NetBindings", "IronfrontNetBindings.cs");
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "DescribeSubmersion")
                .Body!.ToString();
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
