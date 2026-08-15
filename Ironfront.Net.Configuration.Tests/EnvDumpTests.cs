using System;
using System.Collections.Generic;
using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// The startup dump. Its one hard requirement is that a credential never reaches a log
    /// file, so that is what most of this checks.
    /// </summary>
    public class EnvDumpTests
    {
        private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string key, string value) in pairs) map[key] = value;

            return name => map.TryGetValue(name, out string? value) ? value : null;
        }

        [Fact]
        public void SecretsAreRedactedButStillReportedAsSet()
        {
            string dump = EnvDump.Render(
                EnvRegistry.All,
                Env((EnvRegistry.SharedSecret.Name, "a-real-looking-base64-key-goes-here==")));

            Assert.DoesNotContain("a-real-looking-base64-key", dump, StringComparison.Ordinal);
            Assert.Contains(EnvDump.Redacted, dump, StringComparison.Ordinal);
        }

        [Fact]
        public void EverySecretVariableIsRedacted()
        {
            // Guards the omission case: a credential added to the registry later is redacted
            // because the flag lives on the descriptor, not on the caller.
            foreach (EnvVar variable in EnvRegistry.All)
            {
                if (!variable.Secret) continue;

                string rendered = EnvDump.Describe(variable, Env((variable.Name, "super-secret")));
                Assert.Equal(EnvDump.Redacted, rendered);
            }
        }

        [Fact]
        public void UnsetVariablesSaySoRatherThanRenderingBlank()
        {
            Assert.Equal(EnvDump.Unset, EnvDump.Describe(EnvRegistry.MasterHost, Env()));
            Assert.Equal(EnvDump.Unset, EnvDump.Describe(EnvRegistry.MasterHost, Env((EnvRegistry.MasterHost.Name, "   "))));
        }

        [Fact]
        public void OrdinaryValuesAreShown()
        {
            Assert.Equal("28000", EnvDump.Describe(EnvRegistry.MasterPort, Env((EnvRegistry.MasterPort.Name, "28000"))));
        }
    }
}
