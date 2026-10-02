using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A mounted shot the server's authority approved is launched even while the engine's draw
    /// timer is still running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Sitting down starts <c>Weapon.Unholster</c>'s <c>unholsterTime</c> timer (1.2 s
    /// on the helicopter's rocket pod). <c>MountedWeapon.FireApprovedByServer</c> refused while it
    /// ran, after <c>MountedWeaponAuthority</c> had spent the rocket and the server had announced
    /// it to every client: Island, v3.1.1 server log of 2026-10-01, "NOTHING WAS LAUNCHED".
    /// </para>
    /// <para>
    /// <b>A source scan</b>, for <c>OfflinePlayerTeamTests</c>' reason: <c>Assembly-CSharp</c> is a
    /// file no test assembly compiles.
    /// </para>
    /// </remarks>
    public class MountedWeaponDrawTests
    {
        [Fact]
        public void AnApprovedShotFinishesTheDrawInsteadOfRefusing()
        {
            string body = FireApprovedByServerBody();

            Assert.DoesNotContain("return false", body, StringComparison.Ordinal);
            Assert.Contains("UnholsterDone();", body, StringComparison.Ordinal);
            Assert.Contains("CancelInvoke(\"UnholsterDone\");", body, StringComparison.Ordinal);
        }

        private static string FireApprovedByServerBody()
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "MountedWeapon.cs");
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "FireApprovedByServer")
                .Body!.ToString();
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
