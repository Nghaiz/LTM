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
    /// The "declared no role" warning is for a process that starts straight in a map, and
    /// practice declares the role it plays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>NetRoleBootstrap</c> warned at the start of every rendered process that declared no
    /// role, so every player's log opened with it (tmp/diag-clientB2.log, line 52). A player
    /// starts in the splash and the menu, and both ways from there into a map declare first: an
    /// online join as a client, practice as offline. Only a process that starts in a
    /// map -- the Editor playing one -- is left to the Awake race the warning describes.
    /// </para>
    /// <para>
    /// <b>Source scans</b>, like <c>BoatPathTests</c>: both files live in Unity assemblies no test
    /// project compiles.
    /// </para>
    /// </remarks>
    public class RoleWarningSourceTests
    {
        [Fact]
        public void TheWarningIsOnlyForAProcessThatStartsInAMap()
        {
            string body = Method("Net/Shared/NetRoleBootstrap.cs", "Declare");

            int guard = body.IndexOf("MapCatalog.All", StringComparison.Ordinal);
            int warning = body.IndexOf("Debug.LogWarning(", StringComparison.Ordinal);
            Assert.True(warning >= 0, "Declare no longer warns; revisit this test");
            Assert.True(guard >= 0 && guard < warning,
                "the warning must be gated on the first scene being a map, or every player's log opens with it");
            Assert.Contains("SceneManager.GetActiveScene()", body.Substring(0, warning), StringComparison.Ordinal);
        }

        /// <summary>
        /// Practice is the offline single-player game. It declared the Server role until the owner's
        /// report of 2026-10-02: every single-player path reads Server as a headless authority, so
        /// the player could only walk, the score stayed 1000 - 1000 and the first wave threw on team -1.
        /// </summary>
        [Fact]
        public void PracticeDeclaresItselfOffline()
        {
            string body = Method("Net/Client/Menu/MenuScreenController.cs", "LaunchPracticeMap");

            int clear = body.IndexOf("NetContext.Clear();", StringComparison.Ordinal);
            int declare = body.IndexOf("NetContext.DeclareOfflineProcess();", StringComparison.Ordinal);
            int launch = body.IndexOf("practice.LaunchMap(", StringComparison.Ordinal);
            Assert.True(clear >= 0 && declare > clear && launch > declare,
                "practice must clear the online client's declaration, then declare itself offline, before the map loads");
            Assert.DoesNotContain("SetRole(", body, StringComparison.Ordinal);
        }

        private static string Method(string relativePath, string name)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            MethodDeclarationSyntax method = CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == name);
            return method.Body!.ToString();
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
