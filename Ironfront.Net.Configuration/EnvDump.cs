using System;
using System.Collections.Generic;
using System.Text;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// Renders the configuration a process actually resolved, for the log line it prints at
    /// startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in this repository used to print its effective configuration, and the cost of
    /// that is paid every time a value is wrong: a stale <c>.env</c> in a working directory, a
    /// unit file that sets a variable the process does not read, a systemd override nobody
    /// remembers — all of them look identical from outside, and all of them are one line of
    /// output away from obvious.
    /// </para>
    /// <para>
    /// <b>Secrets are redacted by the registry, not by the caller.</b> Whether a value is a
    /// credential is a property of the variable, so it is declared once on
    /// <see cref="EnvVar.Secret"/> and enforced here — a caller cannot forget, and a variable
    /// added later cannot leak by omission.
    /// </para>
    /// </remarks>
    public static class EnvDump
    {
        /// <summary>Placeholder written in place of a secret that is set.</summary>
        public const string Redacted = "<set, redacted>";

        /// <summary>Placeholder written for any variable that is unset or blank.</summary>
        public const string Unset = "<unset>";

        /// <summary>Renders every declared variable read from the process environment.</summary>
        public static string Render()
            => Render(EnvRegistry.All, Environment.GetEnvironmentVariable);

        /// <summary>Renders the given variables through an arbitrary lookup.</summary>
        public static string Render(IReadOnlyList<EnvVar> variables, Func<string, string?> read)
        {
            if (variables is null) throw new ArgumentNullException(nameof(variables));
            if (read is null) throw new ArgumentNullException(nameof(read));

            int width = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                if (variables[i].Name.Length > width) width = variables[i].Name.Length;
            }

            var text = new StringBuilder();

            for (int i = 0; i < variables.Count; i++)
            {
                EnvVar variable = variables[i];
                text.Append(variable.Name.PadRight(width)).Append(" = ").Append(Describe(variable, read));
                if (i < variables.Count - 1) text.Append('\n');
            }

            return text.ToString();
        }

        /// <summary>The printable form of one variable's current value.</summary>
        public static string Describe(EnvVar variable, Func<string, string?> read)
        {
            if (variable is null) throw new ArgumentNullException(nameof(variable));
            if (read is null) throw new ArgumentNullException(nameof(read));

            string value = EnvParse.Trimmed(variable.Read(read));

            if (value.Length == 0) return Unset;
            return variable.Secret ? Redacted : value;
        }
    }
}
