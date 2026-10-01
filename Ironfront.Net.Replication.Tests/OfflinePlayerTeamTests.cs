using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The offline player's team is set once its actor is awake: in <c>FpsActorController.Start</c>,
    /// never in <c>Awake</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found 2026-10-01 by the bot soak, which plays a map offline.</b> P12 (#242) put
    /// <c>actor.SetTeam(0)</c> in <c>FpsActorController.Awake</c>. The controller sits on the player
    /// prefab's root and its <c>Actor</c> on the child "Actor Parent", whose <c>Awake</c> runs later,
    /// so <c>SetTeam</c> recoloured renderers that did not exist yet and threw. That aborted the
    /// controller's own <c>Awake</c> before it fetched its <c>FirstPersonController</c>: every frame
    /// after threw from <c>Velocity()</c> (3,003 times in two minutes) and the loadout never opened.
    /// Offline is only the Editor sandbox and the soak today, which is how it went unnoticed.
    /// </para>
    /// <para>
    /// <b>A source scan, like <c>SeatedDeathTests</c>, and for its reason:</b>
    /// <c>Assembly-CSharp</c> is a file no test assembly compiles.
    /// </para>
    /// </remarks>
    public class OfflinePlayerTeamTests
    {
        [Fact]
        public void TheOfflineTeamIsSetOnceTheActorIsAwake()
        {
            SyntaxNode controller = Parse("Assembly-CSharp/FpsActorController.cs");

            Assert.DoesNotContain("SetTeam", Calls(Method(controller, "Awake")));
            Assert.Contains("SetTeam", Calls(Method(controller, "Start")));
        }

        private static MethodDeclarationSyntax Method(SyntaxNode root, string name) =>
            root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == name && m.ParameterList.Parameters.Count == 0);

        private static List<string> Calls(MethodDeclarationSyntax method) =>
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(call => call.Expression is MemberAccessExpressionSyntax member
                    ? member.Name.Identifier.Text
                    : call.Expression.ToString())
                .ToList();

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
