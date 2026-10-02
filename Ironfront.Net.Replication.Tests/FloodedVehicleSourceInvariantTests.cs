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
    /// A land vehicle or a helicopter the water swallows drowns: it slows, its health drains, and it
    /// dies outright, without fire or a blast, putting its crew out to swim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-01: "the water logic has to be realistic for vehicles too".</b> A jeep,
    /// tank, quad bike or helicopter driven into a lake drove on along the bottom, intact, and stayed
    /// there: the crew was already put out of a seat under water, but the hull kept its id and its pad,
    /// which respawns only on a death, never gave the team another. Measured offline on Forest Lake in
    /// the Editor (2026-10-02): a jeep put 2 m under the lake sank to the bottom, flooded, and died four
    /// seconds later with Burning never set.
    /// </para>
    /// <para>
    /// A source scan, for <c>SeatedDeathTests</c>' reason: <c>Assembly-CSharp</c> and the server's Unity
    /// files are compiled by no test assembly.
    /// </para>
    /// </remarks>
    public sealed class FloodedVehicleSourceInvariantTests
    {
        [Fact]
        public void AFloodedHullIsSlowedAndDrownedEveryPhysicsStep()
        {
            SyntaxNode vehicle = Parse("Assembly-CSharp/Vehicle.cs");

            Assert.Contains(Method(vehicle, "FixedUpdate").DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => Normalized(i) == "UpdateFlooding()");

            string flooding = Normalized(Method(vehicle, "UpdateFlooding").Body!);
            Assert.Contains("if(dead||!IsFlooded)", flooding, StringComparison.Ordinal);
            Assert.Contains("Damage(maxHealth*FloodDamagePerSecond*Time.fixedDeltaTime)", flooding, StringComparison.Ordinal);
            Assert.Contains("!NetContext.IsClient", flooding, StringComparison.Ordinal);

            // A boat floats; it is never flooded.
            string flooded = Normalized(vehicle.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == "IsFlooded"));
            Assert.Contains("thisisBoat", flooded, StringComparison.Ordinal);
        }

        [Fact]
        public void AFloodedHullDiesOutrightOnTheServerAndNeverBurns()
        {
            string sink = Normalized(Method(Parse("Net/Server/ServerVehicleDamageSink.cs"), "ApplyDamage").Body!);
            Assert.Contains("source.CrashSkipsBurn||source.IsFlooded", sink, StringComparison.Ordinal);

            string health = Normalized(Method(Parse("Assembly-CSharp/Vehicle.cs"), "ApplyHealth").Body!);
            int flooded = health.IndexOf("if(IsFlooded)", StringComparison.Ordinal);
            int burn = health.IndexOf("StartBurning()", StringComparison.Ordinal);
            Assert.True(flooded >= 0 && burn > flooded, "a drowned hull must be decided before the burn starts");
        }

        [Fact]
        public void ADrownedHullSettlesWithoutABlastAndPutsItsCrewOut()
        {
            string die = Normalized(Method(Parse("Assembly-CSharp/Vehicle.cs"), "Die").Body!);

            Assert.Contains("booldrowned=IsFlooded;", die, StringComparison.Ordinal);
            Assert.Contains("if(!drowned){Invoke(\"Explode\",0.3f);}", die, StringComparison.Ordinal);
            Assert.Contains("if(!drowned){using(DeathContext.WentDownWith(base.gameObject))", die, StringComparison.Ordinal);
        }

        private static MethodDeclarationSyntax Method(SyntaxNode root, string name)
            => root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == name);

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
