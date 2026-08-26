using System;
using System.Diagnostics;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    /// <summary>
    /// The join ticket's display name reaches the server through a real handshake, sanitized.
    /// verdict-closure R2 task R2.2, ledger X-36.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ledger X-36 read "the server never parses the join ticket", and that was true of the
    /// NAME and false of the parse.</b> <c>UdpTransportServer.HandleConnectResponse</c> has
    /// verified the HMAC and called <c>JoinTicket.TryReadFields</c> since the playerId binding
    /// landed — and threw the name away in the same call with an <c>out string _</c>. So the row
    /// was filed believing a real name needed a new opcode and a <c>PROTOCOL_VERSION</c> move,
    /// and it needed neither: protocol-spec § 12 has carried <c>u8[16] displayNameUtf8</c> inside
    /// the signed ticket since the freeze.
    /// </para>
    /// <para>
    /// <b>These tests go through the socket rather than calling the parser.</b>
    /// <c>JoinTicketTests</c> already pins that <c>TryReadFields</c> returns the name; what was
    /// missing was anything asserting it SURVIVES to a <c>ConnectionInfo</c>, which is the only
    /// place <c>ServerTickLoop</c> can read it from. A unit test of the parser would have been
    /// green for the whole four phases the name was being discarded one line later.
    /// </para>
    /// </remarks>
    public sealed class JoinTicketDisplayNameTests
    {
        /// <summary>
        /// The secret both ends share. Any non-empty value works — the server here validates
        /// with its own callback, so this only has to produce a well-formed ticket.
        /// </summary>
        private static readonly byte[] Secret = new byte[32];

        [Fact]
        public void ATicketsDisplayNameReachesTheServersConnectionInfo()
        {
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();

            server.OnValidateTicket += _ => true;
            server.Start(0, 4);

            ushort connectionId = 0;
            server.OnClientConnected += (id, _) => connectionId = id;

            client.Connect("127.0.0.1", server.Port, Ticket("Bob", playerId: 5001));
            Pump(server, client, () => client.State == ConnectionState.Connected, 2000);
            Pump(server, client, () => connectionId != 0, 2000);

            ConnectionInfo seen = server.GetInfo(connectionId);

            // THE assertion. Before this change it read string.Empty on every connection, and
            // ServerTickLoop.DisplayNameFor therefore rendered "#5001" into the killfeed.
            Assert.Equal("Bob", seen.DisplayName);
            Assert.Equal(5001u, seen.PlayerId);
        }

        [Fact]
        public void AHostileDisplayNameIsSanitizedBeforeItLeavesTheTransport()
        {
            // The ticket is HMAC-signed, which proves the MASTER issued it — not that its
            // contents are safe. The master takes this string from a registration form, so the
            // signature carries a user-chosen name into a UI label with full integrity. The
            // sanitizer runs at this ingress precisely because the signature does not help here.
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();

            server.OnValidateTicket += _ => true;
            server.Start(0, 4);

            ushort connectionId = 0;
            server.OnClientConnected += (id, _) => connectionId = id;

            client.Connect("127.0.0.1", server.Port, Ticket("<b>Bo\nb", playerId: 7));
            Pump(server, client, () => client.State == ConnectionState.Connected, 2000);
            Pump(server, client, () => connectionId != 0, 2000);

            string name = server.GetInfo(connectionId).DisplayName;

            Assert.DoesNotContain("<", name);
            Assert.DoesNotContain(">", name);
            Assert.DoesNotContain("\n", name);
        }

        [Fact]
        public void ATicketWithNoNameLeavesTheFieldEmptyRatherThanNull()
        {
            // The development-stub case, and the loopback's: a ticket whose name field is all
            // zeroes. Empty is what ServerTickLoop.DisplayNameFor falls through on, so this is
            // the input that keeps "#5001" and "Player 3" alive as the honest older answers.
            // Never null — ConnectionInfo.DisplayName promises that, so no consumer guards.
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();

            server.OnValidateTicket += _ => true;
            server.Start(0, 4);

            ushort connectionId = 0;
            server.OnClientConnected += (id, _) => connectionId = id;

            client.Connect(
                "127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            Pump(server, client, () => client.State == ConnectionState.Connected, 2000);
            Pump(server, client, () => connectionId != 0, 2000);

            Assert.Equal(string.Empty, server.GetInfo(connectionId).DisplayName);
        }

        [Fact]
        public void AConnectionInfoBuiltWithoutANameIsEmptyRatherThanNull()
        {
            // The two older constructors are still called — the loopback transport uses the
            // four-argument one and has no ticket to read a name out of. Pinned so that a
            // future edit cannot make DisplayName null on that path and turn every consumer
            // into a null check.
            var info = new ConnectionInfo(1, "loopback", 0f, ConnectionState.Connected);

            Assert.Equal(string.Empty, info.DisplayName);
            Assert.Equal(
                string.Empty,
                new ConnectionInfo(1, "x", 0f, ConnectionState.Connected, 5, default).DisplayName);
            Assert.Equal(
                string.Empty,
                new ConnectionInfo(1, "x", 0f, ConnectionState.Connected, 5, default, null!)
                    .DisplayName);
        }

        private static byte[] Ticket(string displayName, uint playerId)
        {
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            Assert.True(JoinTicket.Issue(
                ticket,
                playerId: playerId,
                serverId: 0,
                roomId: 0,
                expiresAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000,
                displayName: displayName,
                sharedSecret: Secret) > 0);

            return ticket;
        }

        /// <summary>
        /// Drives both ends until <paramref name="condition"/> holds.
        /// </summary>
        /// <remarks>
        /// <b>Every caller waits on <c>connectionId != 0</c> and NOT on
        /// <c>server.ConnectionCount &gt; 0</c>.</b> The count rises when the connection is
        /// created; the id only arrives when <c>OnClientConnected</c> fires, and those are not
        /// the same instant. An `||` between them let the wait finish early, and
        /// <c>GetInfo(0)</c> then returns <c>default(ConnectionInfo)</c> — whose
        /// <c>DisplayName</c> is null rather than empty, so the assertion failed for a reason
        /// that had nothing to do with the name. Caught by the full-suite run rather than the
        /// filtered one, which is the ordering these tests actually ship under.
        /// </remarks>
        private static void Pump(
            UdpTransportServer server, UdpTransportClient client, Func<bool> condition, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
            {
                server.Poll();
                client.Poll();
            }

            Assert.True(condition(), "the transport did not reach the expected state in time");
        }
    }
}
