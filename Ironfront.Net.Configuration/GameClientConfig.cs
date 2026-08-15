using System;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// Where a client build dials, for runs nobody is driving through the Editor: an
    /// automated two-process test, a smoke check on a build machine, a QA build pointed at
    /// staging.
    /// </summary>
    /// <remarks>
    /// Same precedence as <see cref="GameServerConfig"/> — the inspector fields are the
    /// defaults and a variable that is set overrides them — for the same reason: a developer
    /// with no <c>.env</c> keeps the scene they configured, and a scripted run needs no scene
    /// at all.
    /// </remarks>
    public sealed class GameClientConfig
    {
        /// <summary>The game server to dial.</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>The game server's UDP port.</summary>
        public int Port { get; set; } = 27015;

        /// <summary>Log the first snapshot and every connection state change.</summary>
        public bool Verbose { get; set; } = true;

        /// <summary>Overlays the process environment onto this instance and returns it.</summary>
        public GameClientConfig ApplyEnvironment()
            => ApplyEnvironment(Environment.GetEnvironmentVariable);

        /// <summary>Overlays an arbitrary lookup, for tests.</summary>
        public GameClientConfig ApplyEnvironment(Func<string, string?> read)
        {
            if (read is null) throw new ArgumentNullException(nameof(read));

            string host = EnvParse.Trimmed(EnvRegistry.ClientHost.Read(read));
            if (host.Length > 0) Host = host;

            Port    = EnvParse.Port(EnvRegistry.ClientPort.Read(read), Port, EnvRegistry.ClientPort.Name);
            Verbose = EnvParse.Flag(EnvRegistry.ClientVerbose.Read(read), Verbose);

            return this;
        }
    }
}
