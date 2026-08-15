#nullable enable

using System;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Everything the UDP side needs, handed over by the TCP side. phase-03 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Written by the lead's assist track
    /// (plans/assist-dev-a/step-06-master-connection.md).
    /// </para>
    /// <para>
    /// This is the junction between the two protocols, and it is deliberately a value with no
    /// behaviour: the master answers a room join with an address and a signed ticket, and the
    /// only thing left to decide is when to dial. Keeping the three fields together rather than
    /// as three parameters is what stops a caller connecting to one server with another's
    /// ticket, which the game server would reject with a signature failure that names neither.
    /// </para>
    /// <para>
    /// <b>The ticket is short-lived.</b> <c>JoinTicket</c> is signed by the master and expires
    /// 60 seconds after issue, so a <see cref="PendingJoin"/> held across a lobby screen is
    /// worthless by the time it is used. <c>MasterSession</c> dials immediately and times out
    /// well inside that window.
    /// </para>
    /// </remarks>
    public readonly struct PendingJoin
    {
        /// <summary>The game server's public address, as the master reported it.</summary>
        public readonly string Ip;

        /// <summary>Its UDP port.</summary>
        public readonly int Port;

        /// <summary>The master's signed ticket. Never fabricated by the client.</summary>
        public readonly byte[] Ticket;

        public PendingJoin(string ip, int port, byte[] ticket)
        {
            Ip = ip ?? string.Empty;
            Port = port;
            Ticket = ticket ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Whether this is worth dialling.
        /// </summary>
        /// <remarks>
        /// An empty ticket is allowed and is not checked here: a game server running standalone
        /// accepts unsigned tickets when its own <c>_acceptUnsignedTickets</c> switch says so,
        /// which is the LAN path step 07's direct-connect field uses. Whether an empty ticket is
        /// acceptable is the server's decision, and asserting it here would break that path for
        /// no gain.
        /// </remarks>
        public bool IsValid => !string.IsNullOrEmpty(Ip) && Port > 0 && Port <= ushort.MaxValue;

        /// <summary>Nothing pending.</summary>
        public static PendingJoin None => default;

        public override string ToString() => $"{Ip}:{Port} ({Ticket.Length}-byte ticket)";
    }
}
