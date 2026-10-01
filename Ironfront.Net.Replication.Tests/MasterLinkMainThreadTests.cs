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
    /// A game server finishes registering with the master on Unity's main thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure.</b> <c>MasterLinkBootstrap.ConnectAsync</c> awaited the registration with
    /// <c>ConfigureAwait(false)</c> and relied on the response completing it from <c>Poll()</c>, on
    /// the main thread. <c>GameServerLink.RegisterAsync</c> first awaits its own send, which
    /// completes on a pool thread, so when the next <c>Poll()</c> read the master's answer before
    /// that pool thread reached its await on it, the rest of <c>ConnectAsync</c> ran on the pool
    /// thread. The Island server of the 2026-10-01 deploy died of it on its first start: SIGSEGV
    /// in <c>Object.FindObjectsOfType</c>, called from <c>AdoptServerIdOnValidator</c> on a
    /// thread-pool thread. A re-registration after a lost link runs the same race mid-match.
    /// </para>
    /// <para>
    /// <b>A source scan</b>, like <c>BoatPathTests</c>: <c>MasterLinkBootstrap</c> lives in a Unity
    /// assembly no test project compiles.
    /// </para>
    /// </remarks>
    public class MasterLinkMainThreadTests
    {
        [Fact]
        public void RegistrationResumesInUnitysContextBeforeTouchingTheScene()
        {
            MethodDeclarationSyntax connect = Parse("Net/Server/MasterLinkBootstrap.cs")
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "ConnectAsync");

            Assert.DoesNotContain(
                connect.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => call.Expression is MemberAccessExpressionSyntax member
                        && member.Name.Identifier.Text == "ConfigureAwait");

            string body = connect.Body!.ToString();
            int register = body.IndexOf("await reporter.ConnectAndRegisterAsync(", StringComparison.Ordinal);
            int adopt = body.IndexOf("AdoptServerIdOnValidator();", StringComparison.Ordinal);
            Assert.True(register >= 0, "ConnectAsync no longer awaits the registration; revisit this test");
            Assert.True(adopt > register, "the server id must be adopted after the registration resumes");
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
