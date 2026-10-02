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
    /// The wheel visuals of a car and a tank ask <c>Vehicle.WheelsSimulated</c> first, which rests
    /// a network-driven vehicle's wheel colliders away from the camera
    /// (<c>RemoteWheelSimulation</c>).
    /// </summary>
    /// <remarks>
    /// <b>A source scan</b>, for <c>OfflinePlayerTeamTests</c>' reason: <c>Assembly-CSharp</c> is a
    /// file no test assembly compiles. The rule itself is tested in edit mode
    /// (<c>RemoteWheelSimulationTests</c>); this pins that it is wired.
    /// </remarks>
    public class RemoteWheelWiringTests
    {
        [Theory]
        [InlineData("Assembly-CSharp/Car.cs", "LateUpdate")]
        [InlineData("Assembly-CSharp/Tank.cs", "Update")]
        public void TheWheelVisualsAskWhetherTheWheelsAreSimulatedFirst(string file, string method)
        {
            MethodDeclarationSyntax body = Parse(file)
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == method && m.ParameterList.Parameters.Count == 0);

            StatementSyntax first = body.Body!.Statements.First();
            Assert.Contains("WheelsSimulated()", first.ToString(), StringComparison.Ordinal);
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
