using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <c>NetClientBootstrap.Awake</c> installs <c>ClientPhysicsSync</c>, and only past its offline
    /// and dedicated-server returns: the server and practice run the original simulation, which was
    /// written against <c>Physics.autoSyncTransforms</c>.
    /// </summary>
    /// <remarks>
    /// A source scan because waking the bootstrap in a test dials its configured server
    /// (<c>_connectOnStart</c> is on by default). The component's own behaviour is pinned in
    /// <c>ClientPhysicsSyncTests</c> (EditMode).
    /// </remarks>
    public class ClientPhysicsSyncWiringTests
    {
        [Fact]
        public void AnOnlineClientInstallsThePerFrameSyncPastTheOfflineAndServerReturns()
        {
            string awake = AwakeBody();

            int offline = awake.IndexOf("if (NetContext.IsDeclaredOffline)", StringComparison.Ordinal);
            int server = awake.IndexOf("if (NetContext.IsDedicatedServer)", StringComparison.Ordinal);
            int install = awake.IndexOf("EnsurePhysicsSync();", StringComparison.Ordinal);

            Assert.True(offline >= 0 && server >= 0, "setup: the bootstrap's role guards moved");
            Assert.True(install > offline && install > server,
                "auto-sync would be switched off on the server or in practice, or never at all");
        }

        private static string AwakeBody()
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Client", "NetClientBootstrap.cs");
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Awake")
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
