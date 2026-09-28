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
    /// A player killed in a seat dies on screen: out of the seat without drawing a weapon, and
    /// with the death camera on the corpse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Playtest 2026-09-28, bug 1: "shot down or crashed while flying a helicopter, I see no
    /// death -- I jump out of the cockpit and reload my rifle, the animation from a respawn".</b>
    /// Every networked way out of a seat on death went through <c>Actor.LeaveSeat</c>, which draws
    /// the first weapon for a body that comes out empty-handed; the offline <c>Actor.Die</c> never
    /// reaches that draw because it drops the weapons first. And the death camera was never
    /// placed: <c>FpsActorController.Die</c> sweeps it in behind the body, a networked death
    /// marked the actor dead instead, and the camera stayed where the seat's exit left it -- in
    /// the air, for a helicopter, while the corpse fell out of frame. The player's own log showed
    /// the rig frozen at 52.75 m while the server's corpse fell through 48 and 38.
    /// </para>
    /// <para>
    /// <b>A source scan, like <c>NetworkedDeveloperKeysTests</c>, and for its reason:</b> these are
    /// <c>Assembly-CSharp</c> and binding files no test assembly compiles.
    /// </para>
    /// </remarks>
    public class SeatedDeathTests
    {
        [Fact]
        public void LeavingASeatDeadDrawsNoWeapon()
        {
            SyntaxNode actor = Parse("Assembly-CSharp/Actor.cs");

            MethodDeclarationSyntax leave = Methods(actor, "LeaveSeat")
                .Single(m => m.ParameterList.Parameters.Count == 1);
            Assert.Equal("drawWeapon", leave.ParameterList.Parameters[0].Identifier.ValueText);

            List<InvocationExpressionSyntax> draws = Invocations(leave, "SwitchToFirstAvailableWeapon");
            Assert.NotEmpty(draws);
            foreach (InvocationExpressionSyntax draw in draws)
            {
                Assert.True(IsInsideIfMentioning(draw, "drawWeapon"),
                    "Actor.LeaveSeat draws a weapon whatever drawWeapon says, so a pilot killed in "
                    + "his seat plays the respawn draw instead of dying (bug 1, 2026-09-28).");
            }

            // The parameterless form every living caller uses still draws.
            MethodDeclarationSyntax living = Methods(actor, "LeaveSeat")
                .Single(m => m.ParameterList.Parameters.Count == 0);
            Assert.Contains(Invocations(living, "LeaveSeat"), i => Normalized(i) == "LeaveSeat(drawWeapon:true)");
        }

        [Fact]
        public void EveryDeathInASeatLeavesItAsACorpse()
        {
            SyntaxNode binding = Parse("NetBindings/LocalPlayerRigBinding.cs");
            AssertEveryLeaveIsACorpse(Methods(binding, "FellBody").Single(), "LocalPlayerRigBinding.FellBody");
            AssertEveryLeaveIsACorpse(Methods(binding, "LeaveSeatAsCorpse").Single(),
                "LocalPlayerRigBinding.LeaveSeatAsCorpse");

            // S_DEATH reaches the vehicle stage too, and either handler may run first.
            SyntaxNode stage = Parse("Net/Client/ClientVehicleStage.cs");
            MethodDeclarationSyntax onDeath = Methods(stage, "OnDeath").Single();
            Assert.Contains(Invocations(onDeath, "LeaveLocalSeat"), i => Normalized(i).EndsWith("asCorpse:true)"));
        }

        [Fact]
        public void AWreckOnAClientLeavesItsEnclosedCrewToTheServersDeath()
        {
            // The snapshot that flags a wreck Dead runs Vehicle.Die on a client, where Damage
            // cannot kill. The enclosed crew leaves the seat drawing nothing, and is not damaged
            // here: S_DEATH fells it.
            MethodDeclarationSyntax die = Methods(Parse("Assembly-CSharp/Vehicle.cs"), "Die").Single();

            IfStatementSyntax clientBranch = die.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(s => Normalized(s.Condition).Contains("NetContext.IsClient", StringComparison.Ordinal));
            Assert.Contains("seat.enclosed", Normalized(clientBranch.Condition), StringComparison.Ordinal);

            List<InvocationExpressionSyntax> leaves = Invocations(clientBranch.Statement, "LeaveSeat");
            Assert.Single(leaves);
            Assert.Equal("occupant.LeaveSeat(drawWeapon:false)", Normalized(leaves[0]));
            Assert.Empty(Invocations(clientBranch.Statement, "Damage"));
        }

        [Fact]
        public void ANetworkedDeathKeepsTheCameraOnTheCorpse()
        {
            MethodDeclarationSyntax fell = Methods(Parse("NetBindings/LocalPlayerRigBinding.cs"), "FellBody").Single();
            Assert.Single(Invocations(fell, "FollowCorpse"));

            SyntaxNode controller = Parse("Assembly-CSharp/FpsActorController.cs");

            // Followed every frame while it lasts...
            MethodDeclarationSyntax late = Methods(controller, "LateUpdate").Single();
            Assert.Contains(Invocations(late, "UpdateThirdPersonCamera"),
                i => Normalized(i) == "UpdateThirdPersonCamera(followingCorpse)");

            // ...and over at every return to first person, which every respawn goes through.
            MethodDeclarationSyntax firstPerson = Methods(controller, "FirstPersonCamera").Single();
            Assert.Contains(firstPerson.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "followingCorpse=false");
        }

        // ------------------------------------------------------------------------ helpers

        private static void AssertEveryLeaveIsACorpse(MethodDeclarationSyntax method, string where)
        {
            List<InvocationExpressionSyntax> leaves = Invocations(method, "LeaveSeat");
            Assert.NotEmpty(leaves);
            foreach (InvocationExpressionSyntax leave in leaves)
            {
                Assert.True(Normalized(leave).EndsWith("LeaveSeat(drawWeapon:false)", StringComparison.Ordinal),
                    $"{where} takes a dead body out of its seat with '{leave}', which draws its "
                    + "first weapon: the respawn animation played on a corpse (bug 1, 2026-09-28).");
            }
        }

        private static bool IsInsideIfMentioning(SyntaxNode node, string name)
            => node.Ancestors().OfType<IfStatementSyntax>()
                .Any(s => s.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(n => n.Identifier.ValueText == name));

        private static IEnumerable<MethodDeclarationSyntax> Methods(SyntaxNode root, string name)
            => root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == name);

        /// <summary>Calls whose invoked name (the last identifier before the arguments) is <paramref name="name"/>.</summary>
        private static List<InvocationExpressionSyntax> Invocations(SyntaxNode scope, string name)
            => scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText == name,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == name,
                    _ => false,
                })
                .ToList();

        private static string Normalized(SyntaxNode node)
            => string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));

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
