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
        /// <para>
        /// <b>Distinct per client, or the second one is turned away.</b> The server's validator
        /// enforces one session per player once a shared secret is configured, so instances
        /// sharing an id have every join after the first rejected — and the rejection is
        /// reported as a bare <c>InvalidTicket</c>, which reads as a capacity limit and is not
        /// one. It is only consulted on the path where the client mints its own ticket; a
        /// master-issued ticket carries its own id and this is ignored.
        /// </para>
        /// <para>
        /// <b>The default is derived from the process id, because a constant collides by
        /// construction.</b> It used to be 1. <c>JoinTicketSource.Mint</c> numbers the load
        /// harness's synthetic clients from <c>clientIndex + 1</c>, so the very first one also
        /// claimed 1 — and the first two-client run against a real server lost a client to
        /// <c>AlreadyConnected</c> for exactly that reason. Lane B runs three rendered clients,
        /// all of which would have claimed 1 together.
        /// </para>
        /// <para>
        /// Offset past <see cref="ReservedIdCeiling"/> so a derived id can never land inside the
        /// harness's range however small the process id is, and forced non-zero because 0 is the
        /// one value the one-session-per-player claim cannot represent. The trade is
        /// reproducibility: the id differs between runs. That is acceptable because the id is
        /// not an input to anything simulated, and because the client logs the id it minted with
        /// — set <c>IRONFRONT_CLIENT_PLAYER_ID</c> explicitly whenever a run needs to be
        /// replayed against the same identities.
        /// </para>
        /// </remarks>
        public uint PlayerId { get; set; } = DeriveDefaultPlayerId();

        /// <summary>
        /// Ids at or below this are left to schedulers that number from a small index — today,
        /// the load harness's <c>clientIndex + 1</c>.
        /// </summary>
        /// <remarks>
        /// 1024 rather than 64 (the harness's current client ceiling): the point is a margin
        /// nobody has to re-check when that ceiling moves, and the id space is 32 bits.
        /// </remarks>
        public const uint ReservedIdCeiling = 1024;

        private static uint DeriveDefaultPlayerId()
        {
            // Process id, not a random draw: two clients started a second apart get different
            // ids, and the same process reports the same id for its whole life -- so a
            // reconnect within one session does not silently become a different player.
            //
            // Process.GetCurrentProcess().Id rather than Environment.ProcessId, which needs
            // .NET 5 and this assembly is netstandard2.1 because Unity consumes it as a
            // prebuilt DLL out of Assets/Plugins.
            uint pid;
            using (var current = System.Diagnostics.Process.GetCurrentProcess())
            {
                pid = unchecked((uint)current.Id);
            }

            return ReservedIdCeiling + 1 + (pid % (uint.MaxValue - ReservedIdCeiling - 1));
        }

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
