using System;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// One environment variable, described once so that <c>.env.example</c>, the startup
    /// dump and the code that reads it cannot disagree.
    /// </summary>
    /// <remarks>
    /// The reason this type exists is a bug it would have prevented:
    /// <c>IRONFRONT_GAMESERVER_UDP_PORT</c> sat in a hand-maintained <c>.env.example</c> for
    /// three phases while no line of code read it. A documented variable that nothing reads
    /// is worse than an undocumented one, because an operator who sets it believes they have
    /// configured something.
    /// </remarks>
    public sealed class EnvVar
    {
        /// <summary>Creates a descriptor. See the property docs for each argument.</summary>
        public EnvVar(
            string name,
            string section,
            string readBy,
            string comment,
            string defaultValue = "",
            bool secret = false,
            string summary = "")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));

            Name          = name;
            Section       = section;
            ReadBy        = readBy;
            Comment       = comment ?? string.Empty;
            DefaultValue  = defaultValue ?? string.Empty;
            Secret        = secret;
            ExplicitSummary = summary ?? string.Empty;
        }

        /// <summary>The variable name, e.g. <c>IRONFRONT_MASTER_PORT</c>.</summary>
        public string Name { get; }

        /// <summary>The <c>.env.example</c> section this is written under.</summary>
        public string Section { get; }

        /// <summary>
        /// Which process or script reads it, in prose — "master server", "game server",
        /// "tools/backup.sh". Rendered into <c>.env.example</c> so that an operator changing
        /// a value knows what they have to restart.
        /// </summary>
        public string ReadBy { get; }

        /// <summary>The explanation, without leading <c>#</c>. May span lines.</summary>
        public string Comment { get; }

        /// <summary>The one-line form supplied by the declaration, or empty.</summary>
        public string ExplicitSummary { get; }

        /// <summary>
        /// A single line fit for a <c>--help</c> screen, falling back to the first line of
        /// <see cref="Comment"/> when the declaration did not supply one.
        /// </summary>
        /// <remarks>
        /// The fallback exists so a new variable is never invisible; it is not good enough for
        /// a printed usage block, where a sentence that stops mid-clause reads as a bug. Any
        /// variable a process actually prints is required to declare its own — see
        /// <c>EnvUsageTests</c>.
        /// </remarks>
        public string Summary
        {
            get
            {
                if (ExplicitSummary.Length > 0) return ExplicitSummary;

                int newline = Comment.IndexOf('\n');
                return newline < 0 ? Comment : Comment.Substring(0, newline);
            }
        }

        /// <summary>What <c>.env.example</c> ships as the value. Empty means "unset".</summary>
        public string DefaultValue { get; }

        /// <summary>
        /// True for credentials. Redacted by <see cref="EnvDump"/>, and never printed by
        /// anything else either.
        /// </summary>
        public bool Secret { get; }

        /// <summary>Reads this variable from the process environment.</summary>
        public string? Read() => Environment.GetEnvironmentVariable(Name);

        /// <summary>Reads this variable through an arbitrary lookup, for tests.</summary>
        public string? Read(Func<string, string?> read)
        {
            if (read is null) throw new ArgumentNullException(nameof(read));
            return read(Name);
        }

        /// <inheritdoc/>
        public override string ToString() => Name;
    }
}
