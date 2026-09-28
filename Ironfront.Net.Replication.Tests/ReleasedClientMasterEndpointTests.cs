using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A released build must reach the public master with nothing but a double-click.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What these guard.</b> A player who downloads a release has no <c>.env</c>, no
    /// launcher script and no environment variables, so the endpoint serialized on
    /// <c>ClientFlowBootstrap</c> in <c>Menu.unity</c> is the only one that build will ever
    /// dial. Until the first public release it was <c>127.0.0.1:27000</c> plaintext: every
    /// developer launched through <c>play-lan.ps1</c>, which overrides it, so nothing that
    /// was ever run could have noticed that the exe alone reached no master at all.
    /// </para>
    /// <para>
    /// <b>Why scene data and not the C# default.</b> Unity uses the serialized value; the field
    /// initializer only seeds a freshly added component. Both are asserted, because a reset
    /// component in the Editor would otherwise quietly go back to whatever the initializer says.
    /// </para>
    /// <para>
    /// Read off disk rather than in EditMode because CI does not run the EditMode suite.
    /// </para>
    /// </remarks>
    public sealed class ReleasedClientMasterEndpointTests
    {
        private const string BootstrapPath =
            "Ironfront_Reborn/Assets/Scripts/Net/Client/ClientFlowBootstrap.cs";

        private const string MenuScenePath = "Ironfront_Reborn/Assets/Scenes/Menu.unity";

        [Fact]
        public void MenuSceneDialsThePublicMasterOverTls()
        {
            string host = PublicMasterHost();
            string port = PublicMasterPort();
            string block = BootstrapBlock();

            Assert.Equal(host, Field(block, "_masterHost"));
            Assert.Equal(port, Field(block, "_masterPort"));

            // fly terminates TLS at the edge: a plaintext dial completes TCP, reports connected,
            // and dies on the first request with nothing on screen to say why.
            Assert.Equal("1", Field(block, "_masterTls"));
        }

        [Fact]
        public void FieldInitializersMatchThePublicMaster()
        {
            string source = Read(BootstrapPath);

            Assert.Matches(new Regex(@"private string _masterHost = PublicMasterHost;"), source);
            Assert.Matches(new Regex(@"private int _masterPort = PublicMasterPort;"), source);
            Assert.Matches(new Regex(@"private bool _masterTls = true;"), source);
        }

        /// <summary>
        /// The developer launcher and the released build must name the same master, or a
        /// developer's green run says nothing about what a player reaches.
        /// </summary>
        [Fact]
        public void PlayLanDefaultsToTheSameMaster()
        {
            string script = Read("tools/play-lan.ps1");

            System.Text.RegularExpressions.Match hostDefault = Regex.Match(script, @"\[string\]\s*\$MasterHost\s*=\s*""([^""]+)""");
            System.Text.RegularExpressions.Match portDefault = Regex.Match(script, @"\[int\]\s*\$MasterPort\s*=\s*(\d+)");

            Assert.True(hostDefault.Success, "play-lan.ps1 no longer declares a $MasterHost default.");
            Assert.True(portDefault.Success, "play-lan.ps1 no longer declares a $MasterPort default.");
            Assert.Equal(PublicMasterHost(), hostDefault.Groups[1].Value);
            Assert.Equal(PublicMasterPort(), portDefault.Groups[1].Value);
        }

        private static string PublicMasterHost()
        {
            System.Text.RegularExpressions.Match m = Regex.Match(Read(BootstrapPath),
                @"public const string PublicMasterHost = ""([^""]+)"";");
            Assert.True(m.Success, "ClientFlowBootstrap.PublicMasterHost not found.");
            Assert.DoesNotContain("127.0.0.1", m.Groups[1].Value);
            Assert.DoesNotContain("localhost", m.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
            return m.Groups[1].Value;
        }

        private static string PublicMasterPort()
        {
            System.Text.RegularExpressions.Match m = Regex.Match(Read(BootstrapPath), @"public const int PublicMasterPort = (\d+);");
            Assert.True(m.Success, "ClientFlowBootstrap.PublicMasterPort not found.");
            return m.Groups[1].Value;
        }

        /// <summary>The one ClientFlowBootstrap component in Menu.unity, as YAML text.</summary>
        private static string BootstrapBlock()
        {
            string scene = Read(MenuScenePath);
            string[] documents = scene.Split("\n--- ");
            string? found = null;

            foreach (string document in documents)
            {
                if (!document.Contains("Ironfront.Net.Unity.Client.ClientFlowBootstrap")) continue;
                Assert.True(found == null, "Menu.unity carries more than one ClientFlowBootstrap.");
                found = document;
            }

            Assert.True(found != null, "Menu.unity carries no ClientFlowBootstrap.");
            return found!;
        }

        private static string? Field(string block, string name)
        {
            System.Text.RegularExpressions.Match m = Regex.Match(block, @"^\s*" + Regex.Escape(name) + @": ?(\S*)\s*$",
                                  RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string Read(string relative)
            => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
