using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// Role declaration for a shipped process. Ledger <b>X-10</b>.
    /// </summary>
    public sealed class NetRoleDeclarationTests
    {
        [Theory]
        [InlineData("server", DeclaredNetRole.Server)]
        [InlineData("Server", DeclaredNetRole.Server)]
        [InlineData("  SERVER  ", DeclaredNetRole.Server)]
        [InlineData("client", DeclaredNetRole.Client)]
        [InlineData("Client", DeclaredNetRole.Client)]
        public void ARoleNameIsReadCaseAndWhitespaceInsensitively(string value, DeclaredNetRole expected)
            => Assert.Equal(expected, NetRoleDeclaration.Parse(value));

        /// <summary>A typo is Undeclared, never a guess in either direction.</summary>
        /// <remarks>
        /// The two ways to get this wrong are symmetrical and both silent: resolving "sever" to
        /// Server honours a typo, and resolving anything-unrecognised to Client inverts one. Only
        /// Undeclared has a behaviour a reader can predict — it falls through to exactly what
        /// setting nothing does — and the caller reports it.
        /// </remarks>
        [Theory]
        [InlineData("sever")]
        [InlineData("SERVERS")]
        [InlineData("listen")]
        [InlineData("true")]
        [InlineData("1")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void AnythingElseIsUndeclaredRatherThanAGuess(string? value)
            => Assert.Equal(DeclaredNetRole.Undeclared, NetRoleDeclaration.Parse(value));

        [Fact]
        public void AnExplicitRoleBeatsEveryInference()
        {
            // A headless process that SAYS it is a client is a client. Inferring over the top of
            // an explicit declaration is how a staging build silently becomes something else.
            Assert.Equal(
                DeclaredNetRole.Client,
                NetRoleDeclaration.Resolve("client", isBatchMode: true, isDedicatedServerBuild: true));

            Assert.Equal(
                DeclaredNetRole.Server,
                NetRoleDeclaration.Resolve("server", isBatchMode: false, isDedicatedServerBuild: false));
        }

        /// <summary>Headless is inferred Server without needing the Dedicated Server platform.</summary>
        /// <remarks>
        /// The dedicated build this project ships is a headless run of the ordinary player, so a
        /// check that required <c>UNITY_SERVER</c> would infer nothing on the binary that
        /// actually runs.
        /// </remarks>
        [Fact]
        public void AHeadlessProcessIsInferredToBeAServer()
        {
            Assert.Equal(
                DeclaredNetRole.Server,
                NetRoleDeclaration.Resolve(null, isBatchMode: true, isDedicatedServerBuild: false));

            Assert.Equal(
                DeclaredNetRole.Server,
                NetRoleDeclaration.Resolve(null, isBatchMode: false, isDedicatedServerBuild: true));
        }

        /// <summary>
        /// A rendered process with no declaration stays Undeclared — the default is NOT changed.
        /// </summary>
        /// <remarks>
        /// This is the assertion that keeps X-10's fix from being a silent product decision.
        /// Returning Client here would be the obvious "fix" and would break offline single-player
        /// and the Editor sandbox: <c>NetServerBootstrap</c> would stop claiming the role, so
        /// nothing would simulate. The bootstraps keep deciding exactly as they do today; what
        /// changes is that a shipped client now HAS a way to declare, and that the undeclared
        /// case is reported instead of being a silent coin flip.
        /// </remarks>
        [Fact]
        public void ARenderedProcessWithNoDeclarationIsLeftToTheBootstraps()
        {
            Assert.Equal(
                DeclaredNetRole.Undeclared,
                NetRoleDeclaration.Resolve(null, isBatchMode: false, isDedicatedServerBuild: false));

            Assert.True(NetRoleDeclaration.IsUndeclaredRenderedProcess(
                DeclaredNetRole.Undeclared, isBatchMode: false));
        }

        /// <summary>Nothing else is worth warning about, and the warning says so by staying quiet.</summary>
        [Theory]
        [InlineData(DeclaredNetRole.Undeclared, true)]   // headless never reaches Undeclared anyway
        [InlineData(DeclaredNetRole.Server, false)]
        [InlineData(DeclaredNetRole.Client, false)]
        [InlineData(DeclaredNetRole.Server, true)]
        public void ADeclaredOrHeadlessProcessIsNotWarnedAbout(DeclaredNetRole role, bool isBatchMode)
            => Assert.False(NetRoleDeclaration.IsUndeclaredRenderedProcess(role, isBatchMode));

        [Theory]
        [InlineData(new[] { "-ironfront-role", "client" }, "client")]
        [InlineData(new[] { "-ironfront-role=client" }, "client")]
        [InlineData(new[] { "-IRONFRONT-ROLE=Server" }, "Server")]
        [InlineData(new[] { "game.exe", "-x", "-ironfront-role", "server", "-y" }, "server")]
        public void TheCommandLineFormIsReadInBothSpellings(string[] args, string expected)
            => Assert.Equal(expected, NetRoleDeclaration.FromCommandLine(args));

        /// <summary>A flag with no value is null, not the next flag.</summary>
        /// <remarks>
        /// <c>-ironfront-role -batchmode</c> returning "-batchmode" would parse to Undeclared
        /// anyway, but it would do so by accident. A trailing flag has no value at all, and
        /// saying so is what stops the next argument being consumed by a typo.
        /// </remarks>
        [Fact]
        public void AFlagWithNoValueYieldsNothing()
        {
            Assert.Null(NetRoleDeclaration.FromCommandLine(new[] { "-ironfront-role" }));
            Assert.Null(NetRoleDeclaration.FromCommandLine(new[] { "game.exe", "-batchmode" }));
            Assert.Null(NetRoleDeclaration.FromCommandLine(null));

            // Consumed as a value, and then rejected by Parse rather than honoured.
            Assert.Equal(
                DeclaredNetRole.Undeclared,
                NetRoleDeclaration.Parse(
                    NetRoleDeclaration.FromCommandLine(new[] { "-ironfront-role", "-batchmode" })));
        }

        /// <summary>The two variables are distinct, and must stay distinct.</summary>
        /// <remarks>
        /// Lane B's <c>IRONFRONT_LANEB_ROLE</c> also installs the harness, strips a bootstrap and
        /// writes checkpoint artifacts. Collapsing the two names would let a player build drag
        /// verification scaffolding in by setting a role.
        /// </remarks>
        [Fact]
        public void TheShippedVariableIsNotLaneBs()
        {
            Assert.Equal("IRONFRONT_ROLE", NetRoleDeclaration.RoleVariable);
            Assert.NotEqual("IRONFRONT_LANEB_ROLE", NetRoleDeclaration.RoleVariable);
        }
    }
}
