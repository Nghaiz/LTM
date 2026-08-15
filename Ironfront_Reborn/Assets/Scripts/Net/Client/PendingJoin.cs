#nullable enable

using System;
using Ironfront.Net.Protocol;

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

        private readonly byte[] _ticket;

        /// <summary>The master's signed ticket. Never fabricated by the client.</summary>
        /// <remarks>
        /// Read through the field rather than exposed directly so that <see cref="None"/> — and
        /// any other <c>default(PendingJoin)</c>, which bypasses the constructor entirely — reads
        /// as empty rather than null. <c>LeaveMatch</c> assigns <see cref="None"/>, and
        /// <see cref="ToString"/> is on a debug screen, which is a poor place to learn that a
        /// struct's default is not what its constructor guarantees.
        /// </remarks>
        public byte[] Ticket => _ticket ?? Array.Empty<byte>();

        public PendingJoin(string ip, int port, byte[] ticket)
        {
            Ip = ip ?? string.Empty;
            Port = port;
            _ticket = ticket ?? Array.Empty<byte>();
        }

        /// <summary>
        /// A placeholder ticket for a server that accepts unsigned ones. phase-03 UI item 14.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An empty ticket does not reach the wire — it throws.</b>
        /// <c>Connection.BeginConnect</c> rejects anything that is not exactly
        /// <c>ProtocolConstants.JOIN_TICKET_SIZE</c> bytes, before a packet is sent, so a client
        /// dialling with <c>ReadOnlySpan&lt;byte&gt;.Empty</c> raises <c>ArgumentException</c> at
        /// its own <c>Connect</c> call and never gets an answer from anybody. The server's
        /// <c>_acceptUnsignedTickets</c> switch is real, but it is reached only by a ticket that
        /// is the right length: with it on, the validator is <c>_ =&gt; true</c> and the contents
        /// are never examined.
        /// </para>
        /// <para>
        /// So the LAN path sends 64 zero bytes. It is not a forgery attempt — a server with
        /// validation on rejects it on the HMAC like any other unsigned ticket, which is the
        /// correct outcome. It is the difference between being turned away by the server and
        /// never leaving the building.
        /// </para>
        /// </remarks>
        public static byte[] CreateUnsignedTicket() => new byte[ProtocolConstants.JOIN_TICKET_SIZE];

        /// <summary>
        /// Whether this is worth dialling.
        /// </summary>
        /// <remarks>
        /// The ticket is not checked here: whether its contents are acceptable is the server's
        /// decision, and a standalone server with <c>_acceptUnsignedTickets</c> on never looks.
        /// Its <i>length</i> is a different matter and is not optional — see
        /// <see cref="CreateUnsignedTicket"/>.
        /// </remarks>
        public bool IsValid => !string.IsNullOrEmpty(Ip) && Port > 0 && Port <= ushort.MaxValue;

        /// <summary>Nothing pending.</summary>
        public static PendingJoin None => default;

        public override string ToString() => $"{Ip}:{Port} ({Ticket.Length}-byte ticket)";
    }
}
