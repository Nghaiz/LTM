using System;
using System.Text;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist items 14 and 15:
    /// <list type="bullet">
    /// <item>joinTicket with a bad HMAC yields CONNECT_DENIED code 3</item>
    /// <item>Expired joinTicket yields CONNECT_DENIED code 3</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The ticket is the only thing standing between an arbitrary UDP sender and a player
    /// slot, so the negative cases matter more than the positive one.
    /// </remarks>
    public class JoinTicketTests
    {
        private static readonly byte[] Secret =
            Encoding.UTF8.GetBytes("test-shared-secret-not-for-production");

        private const long Now = 1_800_000_000_000L;   // fixed clock, so tests never flake

        private static byte[] IssueSample(long expiresAt = Now + JoinTicket.ValidityMs)
        {
            var ticket = new byte[JoinTicket.Size];
            int written = JoinTicket.Issue(
                ticket, playerId: 4242, serverId: 7, roomId: 99,
                expiresAtUnixMs: expiresAt, displayName: "Nghaiz", sharedSecret: Secret);

            Assert.Equal(JoinTicket.Size, written);
            return ticket;
        }

        [Fact]
        public void TicketIsExactly64Bytes()
        {
            Assert.Equal(64, JoinTicket.Size);
            Assert.Equal(64, ProtocolConstants.JOIN_TICKET_SIZE);

            // 4 + 2 + 2 + 8 + 16 signed, then a 32-byte signature.
            Assert.Equal(32, JoinTicket.SignedPayloadSize);
            Assert.Equal(32, JoinTicket.HmacSize);
            Assert.Equal(JoinTicket.Size, JoinTicket.SignedPayloadSize + JoinTicket.HmacSize);
        }

        [Fact]
        public void ValidityWindowIsSixtySeconds()
            => Assert.Equal(60_000L, JoinTicket.ValidityMs);

        [Fact]
        public void AFreshlyIssuedTicketVerifies()
        {
            byte[] ticket = IssueSample();
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void FieldsRoundTripThroughTheTicket()
        {
            long expiresAt = Now + JoinTicket.ValidityMs;
            byte[] ticket = IssueSample(expiresAt);

            Assert.True(JoinTicket.TryReadFields(
                ticket, out uint playerId, out ushort serverId, out ushort roomId,
                out long parsedExpiry, out string displayName));

            Assert.Equal(4242u, playerId);
            Assert.Equal(7, serverId);
            Assert.Equal(99, roomId);
            Assert.Equal(expiresAt, parsedExpiry);
            Assert.Equal("Nghaiz", displayName);
        }

        // ------------------------------------------------- checklist item 14: bad HMAC

        [Fact]
        public void ATamperedSignatureIsRejectedAsDenyCode3()
        {
            byte[] ticket = IssueSample();
            ticket[JoinTicket.Size - 1] ^= 0xFF;      // flip the last signature byte

            TicketVerifyResult result = JoinTicket.Verify(ticket, Secret, Now);

            Assert.Equal(TicketVerifyResult.BadSignature, result);
            Assert.Equal(ConnectDenyReason.InvalidTicket, JoinTicket.ToDenyReason(result));
            Assert.Equal(3, (byte)JoinTicket.ToDenyReason(result));
        }

        [Fact]
        public void APromotedPlayerIdIsRejected()
        {
            // The realistic attack: take a legitimate ticket and edit the payload to claim
            // a different player, or a longer expiry. Both are covered by the HMAC.
            byte[] ticket = IssueSample();
            ticket[0] ^= 0x01;

            Assert.Equal(TicketVerifyResult.BadSignature, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void AnExtendedExpiryIsRejected()
        {
            byte[] ticket = IssueSample();
            Endian.WriteU64LE(ticket, 8, unchecked((ulong)(Now + 999_999_999L)));

            Assert.Equal(TicketVerifyResult.BadSignature, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void ATicketSignedWithADifferentSecretIsRejected()
        {
            // This is the failure mode when the master server and game server are
            // configured with different IRONFRONT_SHARED_SECRET values: every single
            // ticket bounces, with nothing in the logs to say why.
            byte[] wrongSecret = Encoding.UTF8.GetBytes("a-completely-different-secret");
            byte[] ticket = IssueSample();

            Assert.Equal(TicketVerifyResult.BadSignature,
                         JoinTicket.Verify(ticket, wrongSecret, Now));
        }

        [Fact]
        public void AnAllZeroTicketIsRejected()
        {
            // The laziest forgery attempt.
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(TicketVerifyResult.BadSignature, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void AShortTicketIsRejectedAsMalformed()
        {
            var truncated = new byte[JoinTicket.Size - 1];
            Assert.Equal(TicketVerifyResult.Malformed,
                         JoinTicket.Verify(truncated, Secret, Now));
            Assert.Equal(ConnectDenyReason.InvalidTicket,
                         JoinTicket.ToDenyReason(TicketVerifyResult.Malformed));
        }

        // -------------------------------------------------- checklist item 15: expired

        [Fact]
        public void AnExpiredTicketIsRejectedAsDenyCode3()
        {
            long expiresAt = Now - 1;                 // expired one millisecond ago
            byte[] ticket = IssueSample(expiresAt);

            TicketVerifyResult result = JoinTicket.Verify(ticket, Secret, Now);

            Assert.Equal(TicketVerifyResult.Expired, result);
            Assert.Equal(ConnectDenyReason.InvalidTicket, JoinTicket.ToDenyReason(result));
            Assert.Equal(3, (byte)JoinTicket.ToDenyReason(result));
        }

        [Fact]
        public void ATicketIsStillValidAtItsExactExpiryInstant()
        {
            long expiresAt = Now;
            byte[] ticket = IssueSample(expiresAt);

            // Boundary: expiry is inclusive. One millisecond later it is not.
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now));
            Assert.Equal(TicketVerifyResult.Expired, JoinTicket.Verify(ticket, Secret, Now + 1));
        }

        [Fact]
        public void AnExpiredTicketWithATamperedSignature_ReportsTheSignatureFirst()
        {
            // Signature is checked before expiry so that a forged ticket never reveals
            // whether its payload would otherwise have been acceptable.
            byte[] ticket = IssueSample(Now - 1);
            ticket[JoinTicket.Size - 1] ^= 0xFF;

            Assert.Equal(TicketVerifyResult.BadSignature, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void EveryFailureModeCollapsesToDenyCode3()
        {
            // The game server must not distinguish these on the wire — telling an attacker
            // which check failed turns the handshake into a forging oracle.
            Assert.Equal(ConnectDenyReason.InvalidTicket,
                         JoinTicket.ToDenyReason(TicketVerifyResult.Malformed));
            Assert.Equal(ConnectDenyReason.InvalidTicket,
                         JoinTicket.ToDenyReason(TicketVerifyResult.BadSignature));
            Assert.Equal(ConnectDenyReason.InvalidTicket,
                         JoinTicket.ToDenyReason(TicketVerifyResult.Expired));
            Assert.Equal(ConnectDenyReason.None,
                         JoinTicket.ToDenyReason(TicketVerifyResult.Valid));
        }

        [Fact]
        public void IssueAndVerifyAgreeOnWhichBytesAreSigned()
        {
            // Two tickets differing only in a signed field must produce different
            // signatures. If Issue signed fewer bytes than Verify checks (or vice versa),
            // this is where it shows up.
            byte[] a = new byte[JoinTicket.Size];
            byte[] b = new byte[JoinTicket.Size];

            JoinTicket.Issue(a, 1, 1, 1, Now + 1000, "same", Secret);
            JoinTicket.Issue(b, 2, 1, 1, Now + 1000, "same", Secret);

            Assert.NotEqual(
                Hex.ToHex(a.AsSpan(JoinTicket.SignedPayloadSize)),
                Hex.ToHex(b.AsSpan(JoinTicket.SignedPayloadSize)));

            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(a, Secret, Now));
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(b, Secret, Now));
        }

        [Fact]
        public void IssuingIsDeterministic()
        {
            // Same inputs, same bytes — required for the master server and any test
            // harness to agree, and it proves no hidden randomness crept into the payload.
            byte[] a = new byte[JoinTicket.Size];
            byte[] b = new byte[JoinTicket.Size];

            JoinTicket.Issue(a, 4242, 7, 99, Now + 60_000, "Nghaiz", Secret);
            JoinTicket.Issue(b, 4242, 7, 99, Now + 60_000, "Nghaiz", Secret);

            Assert.Equal(Hex.ToHex(a), Hex.ToHex(b));
        }

        [Fact]
        public void ALongDisplayNameIsTruncatedRatherThanOverflowing()
        {
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(JoinTicket.Size, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000,
                "a-display-name-far-longer-than-sixteen-bytes", Secret));

            Assert.True(JoinTicket.TryReadFields(ticket, out _, out _, out _, out _,
                                                 out string displayName));
            Assert.True(Encoding.UTF8.GetByteCount(displayName) <= JoinTicket.DisplayNameSize);
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void AnEmptySecretIsRefusedRatherThanSilentlyAccepted()
        {
            // An unset IRONFRONT_SHARED_SECRET must fail loudly at issue time, not produce
            // tickets that anyone can forge.
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(-1, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000, "x", ReadOnlySpan<byte>.Empty));

            Assert.Equal(TicketVerifyResult.Malformed,
                         JoinTicket.Verify(IssueSample(), ReadOnlySpan<byte>.Empty, Now));
        }

        [Fact]
        public void AnUndersizedDestinationIsRefused()
        {
            var tooSmall = new byte[JoinTicket.Size - 1];
            Assert.Equal(-1, JoinTicket.Issue(tooSmall, 1, 1, 1, Now + 1000, "x", Secret));
        }
    }
}
