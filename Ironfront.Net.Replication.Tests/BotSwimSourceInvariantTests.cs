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
    /// Bots swim upright in the swim clips, and a swimming player is not twisted by the aim look-at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-01 (v3.1.1): "bots tumble and spin in the water, nobody swims; even a
    /// player swims stiff".</b> The original fells a body in water and swims it as an active ragdoll.
    /// On a server that ragdoll's pelvis and heading were what every client drew, and its in-water bit
    /// came from the spine of a body bobbing at the surface, so the proxy switched between the swim
    /// pose and a tumbling ragdoll several times a second. A networked player's own body swam in the
    /// clips with <c>ActorIk</c> still turning its spine and head toward the rifle's aim point.
    /// </para>
    /// <para>
    /// A source scan, for <c>SeatedDeathTests</c>' reason: <c>Assembly-CSharp</c> is compiled by no test
    /// assembly. The behaviour itself was measured offline on Forest Lake in the Editor (2026-10-02):
    /// a bot put 3 m under the lake swam at 2.4 m/s along its path with its head at the surface, came
    /// up swimming three seconds after being knocked over, and walked out where the water shallowed.
    /// </para>
    /// </remarks>
    public sealed class BotSwimSourceInvariantTests
    {
        [Fact]
        public void ABotInWaterSwimsRatherThanFallingOver()
        {
            SyntaxNode actor = Parse("Assembly-CSharp/Actor.cs");
            MethodDeclarationSyntax update = Method(actor, "Update");

            // Its in-water bit is its swim, not the spine sample.
            Assert.Contains(update.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "inWater=UpdateAnimatedSwim(position)");

            // And the original's water fall-over is not for it.
            IfStatementSyntax fall = update.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(s => s.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(i => Normalized(i) == "FallOver()"));
            Assert.Contains("!SwimsAnimated()", Normalized(fall.Condition), StringComparison.Ordinal);
        }

        [Fact]
        public void ABotKnockedIntoTheWaterComesUpSwimming()
        {
            MethodDeclarationSyntax states = Method(Parse("Assembly-CSharp/Actor.cs"), "UpdateRagdollStates");

            InvocationExpressionSyntax surface = states.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => Normalized(i) == "SurfaceIntoSwim()");
            IfStatementSyntax gate = surface.Ancestors().OfType<IfStatementSyntax>().First();
            Assert.Contains("inWater", Normalized(gate.Condition), StringComparison.Ordinal);
        }

        [Fact]
        public void ASwimmingBotFloatsWhereASwimmingPlayersCapsuleDoes()
        {
            string body = Normalized(Method(Parse("Assembly-CSharp/Actor.cs"), "UpdateSwimMovement").Body!);

            Assert.Contains("MovementCore.SwimFloatDepth", body, StringComparison.Ordinal);
            Assert.Contains("MovementCore.HeightFor(crouching:false)*0.5f", body, StringComparison.Ordinal);
            Assert.Contains("MovementCore.SwimSpeed", body, StringComparison.Ordinal);
            Assert.Contains("LiftSwimmerToSurface(surface)", body, StringComparison.Ordinal);
        }

        [Fact]
        public void ASwimmingPlayerIsNotTurnedByTheAimLookAt()
        {
            MethodDeclarationSyntax swim = Method(Parse("Assembly-CSharp/FpsActorController.cs"), "UpdateNetworkSwim");
            Assert.Contains(swim.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => Normalized(i) == "actor.PresentNetworkSwim(swim,moving)");

            MethodDeclarationSyntax present = Method(Parse("Assembly-CSharp/Actor.cs"), "PresentNetworkSwim");
            Assert.Contains(present.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "ik.weight=swimming?0f:1f");

            // A bot's swim takes the look-at off as well.
            MethodDeclarationSyntax begin = Method(Parse("Assembly-CSharp/Actor.cs"), "BeginAnimatedSwim");
            Assert.Contains(begin.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "ik.weight=0f");
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
