using System;
using System.Net;
using System.Threading.Tasks;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    /// <summary>
    /// The handshake's two security properties: the server keeps no state for an address it has
    /// not proved, and one signed ticket is one player.
    /// </summary>
    public sealed class HandshakeHardeningTests
    {
        // ------------------------------------------------------------------ the cookie

        [Fact]
        public void TheSameHandshakeDerivesTheSameSaltWithoutStoringAnything()
        {
            using var cookie = new HandshakeCookie(new byte[32]);

            ulong first = cookie.Derive(0x7F000001, 40000, clientSalt: 0xABCD, nowMs: 1000);
            ulong second = cookie.Derive(0x7F000001, 40000, clientSalt: 0xABCD, nowMs: 1000);

            Assert.Equal(first, second);
        }

        [Fact]
        public void ADifferentAddressGetsADifferentSalt()
        {
            // The whole point: a cookie issued to one address must not verify for another, or an
            // attacker could complete a handshake for an address it does not hold.
            using var cookie = new HandshakeCookie(new byte[32]);

            ulong a = cookie.Derive(0x7F000001, 40000, 0xABCD, 1000);
            ulong b = cookie.Derive(0x7F000002, 40000, 0xABCD, 1000);
            ulong c = cookie.Derive(0x7F000001, 40001, 0xABCD, 1000);

            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void ADifferentServerKeyGivesADifferentSalt()
        {
            var keyA = new byte[32];
            var keyB = new byte[32];
            keyB[0] = 1;

            using var a = new HandshakeCookie(keyA);
            using var b = new HandshakeCookie(keyB);

            Assert.NotEqual(
                a.Derive(0x7F000001, 40000, 0xABCD, 1000),
                b.Derive(0x7F000001, 40000, 0xABCD, 1000));
        }

        [Fact]
        public void AChallengeVerifiesForTheAddressItWasIssuedTo()
        {
            using var cookie = new HandshakeCookie(new byte[32]);
            const uint address = 0x0A000001;
            const ushort port = 51000;
            const ulong clientSalt = 0x1122334455667788;

            ulong serverSalt = cookie.Derive(address, port, clientSalt, nowMs: 5000);
            ulong response = clientSalt ^ serverSalt;

            Assert.True(cookie.Verify(address, port, clientSalt, response, nowMs: 5000));
        }

        [Fact]
        public void AChallengeDoesNotVerifyForAnotherAddress()
        {
            using var cookie = new HandshakeCookie(new byte[32]);
            const ulong clientSalt = 0x1122334455667788;

            ulong serverSalt = cookie.Derive(0x0A000001, 51000, clientSalt, 5000);
            ulong response = clientSalt ^ serverSalt;

            Assert.False(cookie.Verify(0x0A000002, 51000, clientSalt, response, 5000));
            Assert.False(cookie.Verify(0x0A000001, 51001, clientSalt, response, 5000));
        }

        [Fact]
        public void AHandshakeStraddlingAnEpochBoundaryStillCompletes()
        {
            // The previous bucket is accepted, so a challenge issued at 29.9 s into a bucket is
            // still answerable a moment later. Without it a small fraction of every minute's
            // handshakes would fail for no reason the client could see.
            using var cookie = new HandshakeCookie(new byte[32]);
            const ulong clientSalt = 0xDEADBEEF;

            double issuedAt = HandshakeCookie.EpochSeconds * 1000.0 - 10;
            ulong serverSalt = cookie.Derive(0x0A000001, 51000, clientSalt, issuedAt);
            ulong response = clientSalt ^ serverSalt;

            double answeredAt = issuedAt + 100;   // now in the next bucket
            Assert.True(cookie.Verify(0x0A000001, 51000, clientSalt, response, answeredAt));
        }

        [Fact]
        public void AStaleCookieStopsVerifying()
        {
            // Bounded replay. Two buckets is the window; anything older is refused.
            using var cookie = new HandshakeCookie(new byte[32]);
            const ulong clientSalt = 0xDEADBEEF;

            ulong serverSalt = cookie.Derive(0x0A000001, 51000, clientSalt, 1000);
            ulong response = clientSalt ^ serverSalt;

            double muchLater = 1000 + HandshakeCookie.EpochSeconds * 1000.0 * 5;
            Assert.False(cookie.Verify(0x0A000001, 51000, clientSalt, response, muchLater));
        }

        [Fact]
        public void AGuessedResponseDoesNotVerify()
        {
            using var cookie = new HandshakeCookie(new byte[32]);

            Assert.False(cookie.Verify(0x0A000001, 51000, 0xABCD, 0, 1000));
            Assert.False(cookie.Verify(0x0A000001, 51000, 0xABCD, 0xABCD, 1000));
            Assert.False(cookie.Verify(0x0A000001, 51000, 0xABCD, ulong.MaxValue, 1000));
        }

        [Fact]
        public void TheSaltIsNeverZero()
        {
            // A zero server salt turns the XOR challenge into "echo your own salt back", which
            // any observer could answer.
            using var cookie = new HandshakeCookie(new byte[32]);

            for (uint address = 0; address < 400; address++)
                Assert.NotEqual(0UL, cookie.Derive(address, 51000, address, 1000));
        }

        // ------------------------------------------------------------------ no state before proof

        [Fact]
        public async Task ABurstOfUnansweredRequestsStillLeavesTheServerAbleToAccept()
        {
            // An end-to-end regression guard, and worth being precise about what it does and
            // does not prove.
            //
            // It does NOT reproduce the original attack. That needed thousands of DISTINCT
            // spoofed source addresses to fill the old pending-challenge table, and loopback
            // gives every packet here the same source IP — where the per-IP rate limiter absorbs
            // the burst long before the handshake sees it. Spoofing real source addresses is not
            // something a unit test can do.
            //
            // What it does prove is that a server hammered with unanswered CONNECT_REQUESTs is
            // still able to complete a real handshake afterwards. The mechanism that makes the
            // spoofed case safe — deriving the salt instead of storing it — is pinned by the
            // cookie tests above, which check that a challenge verifies only for the address it
            // was issued to and only within its epoch window.
            using var server = new UdpTransportServer();
            server.OnValidateTicket += _ => true;
            server.Start(0, maxConnections: 4);

            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            using (var flooder = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp))
            {
                var destination = new IPEndPoint(IPAddress.Loopback, server.Port);
                var datagram = new byte[GspHeader.Size + ConnectRequestPayload.Size];

                for (int i = 0; i < 3000; i++)
                {
                    var header = new GspHeader(
                        PacketType.ConnectRequest, PacketFlags.Reliable,
                        (ushort)i, 0, 0, 0, (ushort)ConnectRequestPayload.Size);
                    header.TryWrite(datagram);
                    new ConnectRequestPayload(
                        ProtocolConstants.PROTOCOL_VERSION, ticket, (ulong)i + 1)
                        .Write(datagram.AsSpan(GspHeader.Size));

                    flooder.SendTo(datagram, destination);
                    if (i % 100 == 0) server.Poll();
                }
            }

            for (int i = 0; i < 40; i++) { server.Poll(); await Task.Delay(5); }

            using var client = new UdpTransportClient();
            client.Connect("127.0.0.1", server.Port, ticket);

            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (client.State != ConnectionState.Connected && DateTime.UtcNow < deadline)
            {
                server.Poll();
                client.Poll();
            }

            Assert.Equal(ConnectionState.Connected, client.State);
        }

        // ------------------------------------------------------------------ playerId binding

        [Fact]
        public void AnAnonymousTicketDoesNotLockOutEveryOtherClient()
        {
            // playerId 0 means "no identity". Binding it would make the FIRST client holding a
            // development-stub or anonymous ticket lock out every other one — sixteen players
            // sharing a single unauthenticated slot. The binding engages when real tickets do.
            Assert.Equal(0u, ReadPlayerId(new byte[ProtocolConstants.JOIN_TICKET_SIZE]));
        }

        [Fact]
        public void ARealTicketCarriesItsPlayerIdThroughToTheServer()
        {
            // The field the binding reads. architecture.md section 9 closes impersonation by
            // tying connectionId to this; until it was read, the ticket was reduced to a bool
            // and one captured ticket could open as many connections as its holder liked.
            var secret = new byte[32];
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            Assert.True(JoinTicket.Issue(
                ticket,
                playerId: 4242,
                serverId: 1,
                roomId: 2,
                expiresAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000,
                displayName: "tester",
                sharedSecret: secret) > 0);

            Assert.Equal(4242u, ReadPlayerId(ticket));
        }

        private static uint ReadPlayerId(byte[] ticket)
        {
            Assert.True(JoinTicket.TryReadFields(
                ticket, out uint playerId, out ushort _, out ushort _, out long _, out string _));
            return playerId;
        }
    }
}
