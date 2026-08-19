using System;
using System.Text;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>
    /// Mints the join ticket each synthetic client presents, signed when a shared secret is
    /// reachable and unsigned when one is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> The harness's first run against a real server refused
    /// every client with <c>InvalidTicket</c>. Nothing was wrong with the transport — the
    /// handshake completed a full round trip and the server answered correctly. The repository
    /// carries a <c>.env</c> with <c>IRONFRONT_SHARED_SECRET</c>, so
    /// <c>NetServerBootstrap.RegisterTicketValidator</c> had HMAC validation on, and 64 zero
    /// bytes are exactly what that check exists to reject.
    /// </para>
    /// <para>
    /// <b>The fix is to sign, not to switch the check off.</b> Telling the operator to unset
    /// the secret would make every measurement conditional on a server configured the way no
    /// deployed server is — and <c>ServerMasterReporter</c> installs a stricter validator once
    /// the server has an id, so the unsigned path gets further from reality over time, not
    /// closer.
    /// </para>
    /// <para>
    /// <b>Same issuer as the master server.</b> <see cref="JoinTicket.Issue"/> is the shipped
    /// minting routine, so a ticket this harness presents is byte-identical in construction to
    /// one a player receives from matchmaking. Nothing here reimplements the HMAC — that would
    /// be the criterion-4 mistake one layer over from the decoder.
    /// </para>
    /// </remarks>
    public sealed class JoinTicketSource
    {
        private readonly byte[]? _secret;

        private JoinTicketSource(byte[]? secret, string origin)
        {
            _secret = secret;
            Origin = origin;
        }

        /// <summary>Where the secret came from, for the banner and the report.</summary>
        public string Origin { get; }

        /// <summary>Whether tickets carry a real signature.</summary>
        public bool Signed => _secret != null;

        /// <summary>
        /// Resolves the secret: the explicit flag first, then the environment (with a
        /// <c>.env</c> walked up from the working directory, as every other Ironfront process
        /// does), then nothing.
        /// </summary>
        /// <remarks>
        /// The <c>.env</c> load matters: the game server finds its secret that way, so a
        /// harness that only read the process environment would be unsigned on precisely the
        /// machine where the server is signed — and the two would disagree for a reason
        /// invisible from either side.
        /// </remarks>
        public static JoinTicketSource Resolve(string? explicitSecret)
        {
            if (!string.IsNullOrEmpty(explicitSecret))
                return new JoinTicketSource(Encoding.UTF8.GetBytes(explicitSecret), "--secret");

            // Existing process variables win over the file, so an operator can override without
            // editing it. That is DotEnv's own precedence, not a choice made here.
            DotEnv.LoadFromAncestors(null, out string path);

            string? fromEnvironment =
                Environment.GetEnvironmentVariable(EnvRegistry.SharedSecret.Name);

            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                string origin = string.IsNullOrEmpty(path)
                    ? EnvRegistry.SharedSecret.Name
                    : $"{EnvRegistry.SharedSecret.Name} (.env at {path})";
                return new JoinTicketSource(Encoding.UTF8.GetBytes(fromEnvironment), origin);
            }

            return new JoinTicketSource(null, "none — unsigned tickets");
        }

        /// <summary>
        /// Builds the ticket for one client.
        /// </summary>
        /// <param name="clientIndex">Zero-based index within the run.</param>
        /// <remarks>
        /// <b>A distinct <c>playerId</c> per client, and never 0.</b> The server's validator
        /// enforces one session per player once a secret is configured, so N clients sharing an
        /// id would have the second and later joins rejected — a failure that looks exactly
        /// like a capacity limit and is not one.
        /// </remarks>
        public byte[] Mint(int clientIndex)
        {
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            if (_secret == null)
            {
                // Byte for byte what PendingJoin.CreateUnsignedTicket hands the Unity client.
                // Accepted only where the server was told to accept it.
                return ticket;
            }

            long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + JoinTicket.ValidityMs;

            int written = JoinTicket.Issue(
                ticket,
                playerId: (uint)(clientIndex + 1),
                serverId: 0,
                roomId: 0,
                expiresAtUnixMs: expiresAt,
                displayName: $"harness-{clientIndex}",
                sharedSecret: _secret);

            if (written != ProtocolConstants.JOIN_TICKET_SIZE)
            {
                throw new InvalidOperationException(
                    $"JoinTicket.Issue wrote {written} bytes, expected "
                    + $"{ProtocolConstants.JOIN_TICKET_SIZE}. A ticket that is the wrong length "
                    + "is rejected by Connection.BeginConnect before it is ever sent.");
            }

            return ticket;
        }
    }
}
