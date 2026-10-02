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
    /// A scoped weapon has no <c>OnGUI</c> of its own: its blackout is drawn by a
    /// <c>ScopeBlackout</c> added to the first-person weapon when it aims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Unity calls an <c>OnGUI</c> twice a frame on every enabled instance that has one,
    /// and every bot carries a scoped rifle: 34 instances drawing nothing in a 100-bot match, about
    /// 1.2 ms a frame (development profile, 2026-10-02).
    /// </para>
    /// <para>
    /// <b>A source scan</b>, for <c>OfflinePlayerTeamTests</c>' reason: <c>Assembly-CSharp</c> is a
    /// file no test assembly compiles.
    /// </para>
    /// </remarks>
    public class ScopeBlackoutTests
    {
        [Fact]
        public void AScopedWeaponHasNoOnGuiOfItsOwn()
        {
            Assert.DoesNotContain(Methods("ScopedWeapon.cs"), m => m.Identifier.Text == "OnGUI");
            Assert.Contains(Methods("ScopeBlackout.cs"), m => m.Identifier.Text == "OnGUI");
        }

        [Fact]
        public void TheBlackoutIsAddedOnlyOnTheFirstPersonPathOfAiming()
        {
            MethodDeclarationSyntax setAiming = Methods("ScopedWeapon.cs").Single(m => m.Identifier.Text == "SetAiming");
            string body = setAiming.Body!.ToString();

            int firstPersonGuard = body.IndexOf("if (!HasActiveAnimator())", StringComparison.Ordinal);
            int ensure = body.IndexOf("EnsureBlackout()", StringComparison.Ordinal);
            Assert.True(firstPersonGuard >= 0 && ensure > firstPersonGuard,
                "the blackout must be added after the first-person guard, or every bot's rifle gets one");
        }

        private static MethodDeclarationSyntax[] Methods(string file)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", file);
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            // UNITY_SERVER undefined, as in a client build, so its #if blocks are parsed.
            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .ToArray();
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
