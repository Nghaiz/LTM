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
    /// The local swimmer is placed at the surface only where there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>v3.1.1 live, Forest Lake, 2026-10-01.</b> A player ran out of breath in the lake and
    /// respawned on the hill. In that frame <c>UpdateNetworkSwim</c> still read the pre-teleport
    /// "in water", and <c>FpsActorController.LateUpdate</c> put the body at the surface over the
    /// spawn point -- which is negative infinity: "transform.position assign attempt for 'Actor
    /// Parent' is not valid. Input position is { 772.027405, -Infinity, 1466.975464 }".
    /// </para>
    /// <para>
    /// A source scan, for <c>SeatedDeathTests</c>' reason: <c>Assembly-CSharp</c> is compiled by no test
    /// assembly.
    /// </para>
    /// </remarks>
    public sealed class NetworkSwimPlacementTests
    {
        [Fact]
        public void TheSwimmerIsPlacedOnlyUnderARealSurface()
        {
            MethodDeclarationSyntax late = Method(Parse("Assembly-CSharp/FpsActorController.cs"), "LateUpdate");

            AssignmentExpressionSyntax placement = late.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Single(a => Normalized(a.Left) == "body.position");

            IfStatementSyntax guard = placement.Ancestors().OfType<IfStatementSyntax>().First();
            Assert.Equal("!float.IsNegativeInfinity(surface)", Normalized(guard.Condition));
        }

        [Fact]
        public void TheSwimIsDecidedWhereTheBodyIsThisFrame()
        {
            MethodDeclarationSyntax swim = Method(Parse("Assembly-CSharp/FpsActorController.cs"), "UpdateNetworkSwim");

            Assert.Contains(swim.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => Normalized(i).StartsWith("SwimPresentation.Swims(!actor.dead,CapsuleInWaterNow(),", StringComparison.Ordinal));
            Assert.DoesNotContain("actor.inWater", Normalized(swim.Body!));
        }

        private static MethodDeclarationSyntax Method(SyntaxNode root, string name)
            => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);

        private static string Normalized(SyntaxNode node)
            => string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));

        private static SyntaxNode Parse(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
