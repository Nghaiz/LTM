using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// The gate that keeps <c>.env.example</c> honest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The template used to be hand-maintained, and it drifted in both directions at once:
    /// <c>IRONFRONT_GAMESERVER_UDP_PORT</c> was documented and read by nothing, while
    /// <c>IRONFRONT_BACKUP_DIR</c>, <c>IRONFRONT_ROOT</c>, <c>IRONFRONT_METRICS_HOST</c> and
    /// three more were read by the deployment scripts and documented nowhere. Generating the
    /// file from <see cref="EnvRegistry"/> makes the first failure impossible; this test makes
    /// the second one impossible too, by failing the build when the committed file is not what
    /// the registry renders.
    /// </para>
    /// <para>
    /// <b>Set <c>IRONFRONT_WRITE_ENV_EXAMPLE=1</c> to regenerate instead of assert.</b> That is
    /// the whole tooling story — no extra script, no extra CI step, and the thing that rewrites
    /// the file is by construction the thing that checks it.
    /// </para>
    /// </remarks>
    public class EnvExampleTests
    {
        /// <summary>The variable that flips this test from checking to writing.</summary>
        public const string WriteVariable = "IRONFRONT_WRITE_ENV_EXAMPLE";

        [Fact]
        public void CommittedTemplateMatchesTheRegistry()
        {
            string path = LocateEnvExample();
            string rendered = EnvRegistry.RenderEnvExample();

            if (EnvParse.Flag(Environment.GetEnvironmentVariable(WriteVariable)))
            {
                File.WriteAllText(path, rendered);
                return;
            }

            // Newlines normalised on both sides. .gitattributes checks the file out with the
            // platform's line endings, and a test that failed on Windows for that reason would
            // teach everyone to ignore it.
            string committed = File.ReadAllText(path).Replace("\r\n", "\n");

            Assert.Equal(rendered, committed);
        }

        /// <summary>
        /// Every <see cref="EnvVar"/> declared on <see cref="EnvRegistry"/> is in
        /// <see cref="EnvRegistry.All"/>. X-93.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Declaring a variable is two steps and nothing enforced the second.</b>
        /// <c>All</c> is a hand-written list, and every other gate in this file walks it — so a
        /// variable added to the class but not to that list is read by its consumer at runtime
        /// and is simultaneously invisible to <c>.env.example</c>, to <c>EnvDump</c>, and to
        /// <see cref="CommittedTemplateMatchesTheRegistry"/>. The drift gate cannot report a
        /// variable it has never heard of, so it stays green while the template goes stale.
        /// </para>
        /// <para>
        /// That is not hypothetical: the three <c>IRONFRONT_CLIENT_MASTER_TLS*</c> variables
        /// were added, built, unit-tested and passed the whole suite while absent from the
        /// template — found by grepping the template rather than by any check.
        /// </para>
        /// <para>
        /// Reflection rather than a second list, because a second list is the same defect with
        /// one more place to forget.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryDeclaredVariableIsInTheAllList()
        {
            var declared = typeof(EnvRegistry)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.FieldType == typeof(EnvVar))
                .Select(f => (EnvVar)f.GetValue(null)!)
                .ToList();

            Assert.NotEmpty(declared);

            var listed = new HashSet<string>(EnvRegistry.All.Select(v => v.Name), StringComparer.Ordinal);
            var missing = declared.Where(v => !listed.Contains(v.Name)).Select(v => v.Name).ToList();

            Assert.True(
                missing.Count == 0,
                "Declared on EnvRegistry but absent from EnvRegistry.All: "
                + string.Join(", ", missing)
                + ". Such a variable is read at runtime and is invisible to .env.example, to "
                + "EnvDump and to the drift gate above -- add it to All rather than deleting it "
                + "here.");
        }

        [Fact]
        public void EveryDeclaredVariableIsNamedInTheTemplate()
        {
            string committed = File.ReadAllText(LocateEnvExample());

            foreach (EnvVar variable in EnvRegistry.All)
            {
                Assert.Contains(variable.Name + "=", committed, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void VariableNamesAreUniqueAndPrefixed()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            foreach (EnvVar variable in EnvRegistry.All)
            {
                Assert.StartsWith("IRONFRONT_", variable.Name, StringComparison.Ordinal);
                Assert.True(seen.Add(variable.Name), $"{variable.Name} is declared twice.");
                Assert.False(string.IsNullOrWhiteSpace(variable.ReadBy), $"{variable.Name} does not say who reads it.");
            }
        }

        [Fact]
        public void CopyingTheTemplateVerbatimChangesNoBehaviour()
        {
            // The template writes out the defaults, which is only harmless while each written
            // value matches what the code already does. IRONFRONT_GAMESERVER_TRANSPORT is the
            // one where it did not: the resolver defaults to udp, the scene ships with the
            // loopback wire on, and the environment beats the scene -- so a copied .env would
            // have switched every Editor to real sockets. It is blank for that reason and this
            // is the guard that keeps it blank.
            Assert.Equal(string.Empty, EnvRegistry.GameServerTransport.DefaultValue);

            var scene = new GameServerConfig { UseLoopbackTransport = true };
            var resolved = new GameServerConfig { UseLoopbackTransport = true }
                .ApplyEnvironment(name => EnvRegistry.Find(name)?.DefaultValue);

            Assert.Equal(scene.UseLoopbackTransport, resolved.UseLoopbackTransport);
            Assert.Equal(scene.UdpPort, resolved.UdpPort);
            Assert.Equal(scene.MaxConnections, resolved.MaxConnections);
            Assert.Equal(scene.MaxPlayers, resolved.MaxPlayers);
            Assert.Equal(scene.MasterPort, resolved.MasterPort);
            Assert.Equal(scene.AcceptUnsignedTickets, resolved.AcceptUnsignedTickets);
        }

        /// <summary>
        /// Walks up from the test binary to the repository root. The test host's working
        /// directory is <c>bin/Debug/net8.0</c>, which is four levels down and not somewhere
        /// a relative path should be hard-coded from.
        /// </summary>
        private static string LocateEnvExample()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, ".env.example");
                if (File.Exists(candidate)) return candidate;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $".env.example was not found above {AppContext.BaseDirectory}.");
        }
    }
}
