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
        public void ADespawnedVehicleGivesTheLocalSeatUpBeforeItIsDestroyed()
        {
            // v3.1.1 live, 2026-10-01: the round reset despawned the vehicle the local player was
            // driving, RemoteVehicleRegistry destroyed it, and the rig -- a child of the seat --
            // went with it: 17,282 NullReferenceExceptions in 24 s until the player quit. The
            // stage must hear the despawn itself; its snapshot check lets go a second too late.
            SyntaxNode stage = Parse("Net/Client/ClientVehicleStage.cs");

            Assert.Contains(Methods(stage, "OnEnable").Single().DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "_client.Router.OnVehicleDespawn+=OnVehicleDespawn");
            Assert.Contains(Methods(stage, "OnDisable").Single().DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                a => Normalized(a) == "_client.Router.OnVehicleDespawn-=OnVehicleDespawn");

            MethodDeclarationSyntax despawn = Methods(stage, "OnVehicleDespawn").Single();
            Assert.Single(Invocations(despawn, "LeaveLocalSeat"));
        }

        [Fact]
        public void AWreckOnAClientLeavesEveryOccupantToTheServer()
        {
            // The snapshot that flags a wreck Dead runs Vehicle.Die on a client, and it only takes
            // the body out of its seat -- drawing nothing from an enclosed seat, whose crew S_DEATH
            // is about to fell. It damages nobody: the 2026-09-30 playtest dropped all three
            // open-seat drivers through the map because the offline Damage(0, 200) knocked the
            // local body over while the server kept it standing.
            MethodDeclarationSyntax die = Methods(Parse("Assembly-CSharp/Vehicle.cs"), "Die").Single();

            IfStatementSyntax clientBranch = die.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(s => Normalized(s.Condition).Contains("NetContext.IsClient", StringComparison.Ordinal));
            Assert.Equal("Ironfront.Net.Unity.NetContext.IsClient", Normalized(clientBranch.Condition));

            // An enclosed seat's crew is felled by S_DEATH -- except from a hull the water drowned,
            // which kills nobody (Vehicle.IsFlooded, 2026-10-02), so that crew swims away armed.
            List<InvocationExpressionSyntax> leaves = Invocations(clientBranch.Statement, "LeaveSeat");
            Assert.Single(leaves);
            Assert.Equal("occupant.LeaveSeat(drawWeapon:!seat.enclosed||drowned)", Normalized(leaves[0]));
            Assert.Empty(Invocations(clientBranch.Statement, "Damage"));

            // The server and the offline game keep the original's two outcomes.
            Assert.NotNull(clientBranch.Else);
            List<string> damages = Invocations(clientBranch.Else!.Statement, "Damage").Select(Normalized).ToList();
            Assert.Contains(damages, d => d.StartsWith("occupant.Damage(200f,200f,", StringComparison.Ordinal));
            Assert.Contains(damages, d => d.StartsWith("occupant.Damage(0f,200f,", StringComparison.Ordinal));
        }

        [Fact]
        public void NothingOnAClientKnocksTheLocalBodyOver()
        {
            // The one balance knock-over in Actor.DamageAttributed is gated on the body not being
            // the network-driven local player: the server owns that body's stance and never
            // knocks a player over. FellBody's death ragdoll calls KnockOver directly and is
            // unaffected.
            MethodDeclarationSyntax damage = Methods(Parse("Assembly-CSharp/Actor.cs"), "DamageAttributed").Single();

            List<InvocationExpressionSyntax> knocks = Invocations(damage, "KnockOver");
            Assert.Single(knocks);

            IfStatementSyntax gate = knocks[0].Ancestors().OfType<IfStatementSyntax>().First();
            Assert.Equal("balance<0f&&!IsNetworkDrivenLocalBody()", Normalized(gate.Condition));

            MethodDeclarationSyntax fell = Methods(Parse("NetBindings/LocalPlayerRigBinding.cs"), "FellBody").Single();
            Assert.Single(Invocations(fell, "KnockOver"));
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

        [Fact]
        public void TheServerCapturesTheActorsAfterTheVehicleDeaths()
        {
            // Bug 1 again, reproduced live on 2026-09-29: the actors were captured BEFORE
            // AdvanceVehicleBurn, so a burn that killed a crew broadcast S_DEATH and then sent a
            // snapshot still carrying that crew alive (and out of the seat: MarkDead had already
            // cleared the seat table). The client respawned the corpse on it.
            MethodDeclarationSyntax build = Methods(Parse("Net/Server/ServerTickLoop.cs"), "BuildAndSendSnapshots").Single();

            InvocationExpressionSyntax burn = Invocations(build, "AdvanceVehicleBurn").Single();
            InvocationExpressionSyntax actors = Invocations(build, "CaptureInto")
                .Single(i => Normalized(i) == "ServerActorRegistry.Instance.CaptureInto(_world)");

            Assert.True(burn.SpanStart < actors.SpanStart,
                "BuildAndSendSnapshots captures the actors before AdvanceVehicleBurn, so a vehicle "
                + "that burns out kills its crew after the capture: S_DEATH goes out, then a snapshot "
                + "that still has them alive, and the client respawns the corpse (bug 1, 2026-09-28).");
        }

        [Fact]
        public void TheClientNotesEveryDeployRequestItSends()
        {
            MethodDeclarationSyntax request = Methods(
                Parse("Net/Client/NetClientLocalCombatDriver.cs"), "RequestRespawn").Single();

            InvocationExpressionSyntax send = Invocations(request, "Send").Single();
            InvocationExpressionSyntax noted = Invocations(request, "NoteDeployRequested").Single();

            Assert.Equal("_state.NoteDeployRequested()", Normalized(noted));
            Assert.True(send.SpanStart < noted.SpanStart,
                "The deploy request must be noted only once it has been sent: ClientCombatState "
                + "believes an alive snapshot after a death only as the answer to one.");
        }

        [Fact]
        public void ACorpseAsksForNoSeat()
        {
            MethodDeclarationSyntax update = Methods(Parse("Net/Client/ClientSeatRequester.cs"), "Update").Single();

            IfStatementSyntax gate = update.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(s => Normalized(s.Condition) == "!LocalBodyIsDeployed()");
            Assert.NotEmpty(gate.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>());

            // Before the retry and before the key is read, so neither a fresh press nor a walk to
            // the next seat that a death interrupted can reach the wire.
            Assert.True(gate.SpanStart < Invocations(update, "SendDueRetry").Single().SpanStart);
            Assert.True(gate.SpanStart < Invocations(update, "GetButtonDown").Single().SpanStart);
        }

        [Fact]
        public void TheServerGivesNoSeatToACorpse()
        {
            MethodDeclarationSyntax measure = Methods(
                Parse("Net/Server/ServerSeatBridge.cs"), "TryMeasureSeatReach").Single();

            Assert.Contains(measure.DescendantNodes().OfType<IfStatementSyntax>(),
                s => Normalized(s.Condition) == "!actor.IsAlive"
                     && s.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());
        }

        [Fact]
        public void ACorpseNeverDrawsAWeaponOrTakesASeat()
        {
            SyntaxNode actor = Parse("Assembly-CSharp/Actor.cs");

            MethodDeclarationSyntax leave = Methods(actor, "LeaveSeat")
                .Single(m => m.ParameterList.Parameters.Count == 1);
            foreach (InvocationExpressionSyntax draw in Invocations(leave, "SwitchToFirstAvailableWeapon"))
            {
                Assert.True(IsInsideIfMentioning(draw, "dead"),
                    "Actor.LeaveSeat can draw for a dead body: a seat Left that lands after the "
                    + "death plays the respawn animation on the corpse.");
            }

            // The refusal comes FIRST: InstantGetUp stands a fallen body up and hands it the
            // first-person camera, which is exactly what must not happen to a corpse.
            MethodDeclarationSyntax enter = Methods(actor, "EnterSeat").Single();
            StatementSyntax first = enter.Body!.Statements.First();
            Assert.Equal("if(dead){returnfalse;}", Normalized(first));
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
