using System;
using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// The <c>--help</c> screen, which is rendered from the registry rather than written out
    /// as a literal.
    /// </summary>
    /// <remarks>
    /// The master server carried the list as a string literal until this change, and a literal
    /// is a copy: it was already missing every variable added after it was typed. These tests
    /// guard the two ways the derived version could still read badly — a variable with no
    /// hand-written one-liner, and a renderer that silently drops one.
    /// </remarks>
    public class EnvUsageTests
    {
        /// <summary>The reader name the master server passes to the renderer.</summary>
        public const string MasterServer = "master server";

        [Fact]
        public void EveryPrintedVariableDeclaresItsOwnOneLiner()
        {
            // The fallback (first line of the long comment) keeps a new variable visible, but it
            // stops mid-clause and reads as a bug on a usage screen. Anything actually printed
            // has to say its own short form.
            foreach (EnvVar variable in EnvRegistry.For(MasterServer))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(variable.ExplicitSummary),
                    $"{variable.Name} is printed by the master server's --help but declares no summary.");

                Assert.DoesNotContain("\n", variable.ExplicitSummary, StringComparison.Ordinal);
                Assert.True(variable.ExplicitSummary.Length <= 60, $"{variable.Name}'s summary is too long for the column.");
            }
        }

        [Fact]
        public void TheMasterServerSectionNamesEveryVariableItReads()
        {
            string usage = EnvRegistry.RenderUsage(MasterServer);

            foreach (EnvVar variable in EnvRegistry.For(MasterServer))
            {
                Assert.Contains(variable.Name, usage, StringComparison.Ordinal);
            }

            // And nothing it does not read. IRONFRONT_CLIENT_HOST on the master's help screen
            // would send an operator looking for an effect that cannot happen.
            Assert.DoesNotContain(EnvRegistry.ClientHost.Name, usage, StringComparison.Ordinal);
            Assert.DoesNotContain(EnvRegistry.BackupDir.Name, usage, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSecretIsNamedButItsValueIsNot()
        {
            string usage = EnvRegistry.RenderUsage(MasterServer);

            Assert.Contains(EnvRegistry.SharedSecret.Name, usage, StringComparison.Ordinal);

            // It has no default, so there is nothing to print; a "(default ...)" here would be
            // the one place a credential could reach a help screen.
            Assert.Equal(string.Empty, EnvRegistry.SharedSecret.DefaultValue);
            Assert.DoesNotContain(EnvRegistry.TlsCertificatePassword.Name + "  password for the bundle; never printed (default", usage, StringComparison.Ordinal);
        }

        /// <summary>
        /// The client's master port defaults to the master's own, and nothing may re-state it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An identity assertion, deliberately not a pin on 27000.</b> A test reading
        /// <c>Assert.Equal("27000", ClientMasterPort.DefaultValue)</c> would pass just as
        /// happily if both sides were changed to the wrong number together, and would have to
        /// be edited on every legitimate port change -- so it would be edited without thought.
        /// This asserts the RELATIONSHIP the docstring claims, so it stays true across any
        /// future port and fails the moment the two drift apart again.
        /// </para>
        /// <para>
        /// It exists because they did drift. The client said 27020 while the master bound
        /// 27000, across five separate re-statements of the number and two shipped scenes that
        /// disagreed with each other, and a client build with no override dialled a port
        /// nothing listened on. The failure was silent at every layer -- the connect attempt
        /// surfaced no message a player could act on -- so no test and no operator ever saw it.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheClientDialsThePortTheMasterBinds()
        {
            Assert.Equal(EnvRegistry.MasterPort.DefaultValue, EnvRegistry.ClientMasterPort.DefaultValue);

            // The C# default every client-side field starts from is the same number again.
            // Without this the registry could agree with itself while the code shipped another
            // value, which is the exact shape of the original defect.
            Assert.Equal(
                GameClientConfig.DefaultMasterPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EnvRegistry.MasterPort.DefaultValue);
        }

        [Fact]
        public void AnUnknownReaderRendersNothingRatherThanEverything()
        {
            Assert.Empty(EnvRegistry.For("nobody"));
            Assert.Equal(string.Empty, EnvRegistry.RenderUsage("nobody"));
        }

        [Fact]
        public void TheGameServerAndTheToolsHaveTheirOwnSections()
        {
            // Not printed anywhere yet, but the grouping is what makes ReadBy load-bearing
            // rather than decorative — a variable no process claims is a variable nothing reads.
            Assert.NotEmpty(EnvRegistry.For("game server"));
            Assert.NotEmpty(EnvRegistry.For("tools/backup.sh"));

            foreach (EnvVar variable in EnvRegistry.All)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(variable.ReadBy),
                    $"{variable.Name} names no reader, so nothing can be shown to claim it.");
            }
        }
    }
}
