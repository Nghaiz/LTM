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
