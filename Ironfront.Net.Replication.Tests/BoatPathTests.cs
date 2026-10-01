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
    /// A boat sent to a flag inland heads for the water nearest it instead of failing to find a
    /// path at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure.</b> A bot driving Island's RHIB was sent to Farm, 72 m up and well inland.
    /// The search for the goal's node on the boat graph gave up beyond
    /// <c>AstarPath.maxNearestNodeDistance</c> (100 m) of water, so the path failed with "Couldn't
    /// find a close node to the end point" -- logged as an error by <c>OnPathComplete</c> -- and the
    /// boat never moved. The bot soak caught it in two of six Island runs (a live v3.0.0 Island
    /// server logged that error six times in one session). Searched directly on Island's graph from two boat
    /// pads to the failing goal, the search fails with the limit and succeeds without it, ending at
    /// the water 118 m from the goal.
    /// </para>
    /// <para>
    /// <b>A source scan, like <c>SeatedDeathTests</c>, and for its reason:</b>
    /// <c>AiActorController</c> is an <c>Assembly-CSharp</c> type no test assembly compiles.
    /// </para>
    /// </remarks>
    public class BoatPathTests
    {
        [Fact]
        public void ABoatsPathSearchIsNotLimitedToAHundredMetresOfWater()
        {
            MethodDeclarationSyntax gotoMethod = Parse("Assembly-CSharp/AiActorController.cs")
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Goto");
            string body = gotoMethod.Body!.ToString();

            int boat = body.IndexOf("aquatic && actor.IsDriver()", StringComparison.Ordinal);
            Assert.True(boat >= 0, "Goto no longer treats a boat's driver separately");
            Assert.Contains("constrainDistance = false", body.Substring(boat));
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
