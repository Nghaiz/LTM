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

        /// <summary>
        /// Whether the client predicts the vehicle it is driving. V5-D6.
        /// </summary>
        /// <remarks>
        /// <b>The fallback lives here so it can be flipped without the Editor.</b> Design
        /// section 9 scores prediction non-convergence at 15, and the remedy has to be reachable
        /// from a headless two-process run and from a QA build — not only from an inspector
        /// checkbox somebody has to find. False routes the driven vehicle down the same
        /// interpolated path every other vehicle already takes: correct, and a round trip
        /// behind.
        /// </remarks>
        public bool PredictLocalVehicle { get; set; } = true;

        /// <summary>
        /// The playerId this client's self-minted join ticket claims. Never 0.
        /// </summary>
        /// <remarks>
        /// <b>Distinct per client, or the second one is turned away.</b> The server's validator
        /// enforces one session per player once a shared secret is configured, so a scripted run
        /// that leaves every instance on the default has its second and third joins rejected —
        /// and the rejection reads as a capacity limit, which it is not. It is only consulted on
        /// the path where the client mints its own ticket; a master-issued ticket carries its own
        /// id and this is ignored.
        /// </remarks>
        public uint PlayerId { get; set; } = 1;

        /// <summary>
        /// The name that self-minted ticket carries, truncated to 16 UTF-8 bytes.
        /// </summary>
        /// <remarks>
        /// This is where a killfeed line gets its name, which is why it is configuration rather
        /// than a constant: the two-client combat check grades a killfeed line <i>with a name</i>,
        /// and two clients on the same default produce a killfeed nobody can read.
        /// </remarks>
        public string DisplayName { get; set; } = "player";

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

            PredictLocalVehicle = EnvParse.Flag(
                EnvRegistry.ClientPredictLocalVehicle.Read(read), PredictLocalVehicle);

            // PositiveInt, not NonNegativeInt: 0 is the one value the server's one-session-per-
            // player claim cannot represent, so it is rejected here rather than becoming a join
            // failure three layers away.
            PlayerId = (uint)EnvParse.PositiveInt(
                EnvRegistry.ClientPlayerId.Read(read), (int)PlayerId, EnvRegistry.ClientPlayerId.Name);

            string displayName = EnvParse.Trimmed(EnvRegistry.ClientDisplayName.Read(read));
            if (displayName.Length > 0) DisplayName = displayName;

            return this;
        }
    }
}
