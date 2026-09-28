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

        private static byte[] IssueSample(
            long expiresAt = Now + JoinTicket.ValidityMs, byte team = 0)
        {
            var ticket = new byte[JoinTicket.Size];
            int written = JoinTicket.Issue(
                ticket, playerId: 4242, serverId: 7, roomId: 99,
                expiresAtUnixMs: expiresAt, team: team, displayName: "Nghaiz",
                sharedSecret: Secret);

            Assert.Equal(JoinTicket.Size, written);
            return ticket;
        }

        [Fact]
        public void TicketIsExactly64Bytes()
        {
            Assert.Equal(64, JoinTicket.Size);
            Assert.Equal(64, ProtocolConstants.JOIN_TICKET_SIZE);

            // 4 + 2 + 2 + 8 + 1 + 15 signed, then a 32-byte signature. The team byte came out
            // of the name, so neither total moved — which is exactly why § 3.2 chose this
            // layout over growing the ticket.
            Assert.Equal(15, JoinTicket.DisplayNameSize);
            Assert.Equal(
                JoinTicket.SignedPayloadSize,
                4 + 2 + 2 + 8 + 1 + JoinTicket.DisplayNameSize);

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
                out long parsedExpiry, out byte team, out string displayName));

            Assert.Equal(4242u, playerId);
            Assert.Equal(7, serverId);
            Assert.Equal(99, roomId);
            Assert.Equal(expiresAt, parsedExpiry);
            Assert.Equal(0, team);
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
        public void ATicketIsExpiredAtItsExactExpiryInstant()
        {
            long expiresAt = Now;
            byte[] ticket = IssueSample(expiresAt);

            Assert.Equal(TicketVerifyResult.Expired, JoinTicket.Verify(ticket, Secret, Now));
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

            JoinTicket.Issue(a, 1, 1, 1, Now + 1000, 0, "same", Secret);
            JoinTicket.Issue(b, 2, 1, 1, Now + 1000, 0, "same", Secret);

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

            JoinTicket.Issue(a, 4242, 7, 99, Now + 60_000, 1, "Nghaiz", Secret);
            JoinTicket.Issue(b, 4242, 7, 99, Now + 60_000, 1, "Nghaiz", Secret);

            Assert.Equal(Hex.ToHex(a), Hex.ToHex(b));
        }

        [Fact]
        public void ALongDisplayNameIsTruncatedRatherThanOverflowing()
        {
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(JoinTicket.Size, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000, 0,
                "a-display-name-far-longer-than-sixteen-bytes", Secret));

            Assert.True(JoinTicket.TryReadFields(ticket, out _, out _, out _, out _, out _,
                                                 out string displayName));
            Assert.True(Encoding.UTF8.GetByteCount(displayName) <= JoinTicket.DisplayNameSize);
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now));
        }

        [Fact]
        public void VietnameseDisplayNameIsTruncatedAtAUtf8CharacterBoundary()
        {
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(JoinTicket.Size, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000, 0, "NgườiChơiViệtNam", Secret));

            Assert.True(JoinTicket.TryReadFields(ticket, out _, out _, out _, out _, out _,
                                                 out string displayName));
            // "NgườiChơiVi" is 15 UTF-8 bytes: ư (U+01B0) and ơ (U+01A1) cost two bytes each,
            // ờ (U+1EDD) three. The next character, ệ (U+1EC7), needs three more and would
            // overrun the field, so the cut lands after the "i". This name fills the 15-byte
            // field EXACTLY, which is why it survived the 16 → 15 shrink unchanged — and why
            // it is not on its own evidence that the shrink is safe. See the two tests below.
            Assert.Equal("NgườiChơiVi", displayName);
            Assert.True(Encoding.UTF8.GetByteCount(displayName) <= JoinTicket.DisplayNameSize);
            Assert.DoesNotContain('�', displayName);
        }

        [Fact]
        public void AnEmptySecretIsRefusedRatherThanSilentlyAccepted()
        {
            // An unset IRONFRONT_SHARED_SECRET must fail loudly at issue time, not produce
            // tickets that anyone can forge.
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(-1, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000, 0, "x", ReadOnlySpan<byte>.Empty));

            Assert.Equal(TicketVerifyResult.Malformed,
                         JoinTicket.Verify(IssueSample(), ReadOnlySpan<byte>.Empty, Now));
        }

        [Fact]
        public void AnUndersizedDestinationIsRefused()
        {
            var tooSmall = new byte[JoinTicket.Size - 1];
            Assert.Equal(-1, JoinTicket.Issue(tooSmall, 1, 1, 1, Now + 1000, 0, "x", Secret));
        }

        // ---------------------------------------------------------------- P13: the team byte

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)1)]
        public void TheTeamTheMasterChoseIsTheTeamTheTicketCarries(byte team)
        {
            // Criterion 1, first half. Before P13 the ticket had no team at all and the game
            // server re-derived one from slot parity, so this round trip did not exist to fail.
            byte[] ticket = IssueSample(team: team);

            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now));
            Assert.True(JoinTicket.TryReadFields(
                ticket, out _, out _, out _, out _, out byte readTeam, out _));
            Assert.Equal(team, readTeam);
        }

        [Fact]
        public void ATamperedTeamByteFailsTheSignature()
        {
            // Criterion 1, second half, and the only proof that the team is INSIDE the signed
            // 32 bytes. If the layout ever moved it past SignedPayloadSize this goes RED — and
            // a team outside the HMAC means a player picks their side by editing one byte.
            byte[] onTeamZero = IssueSample(team: 0);
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(onTeamZero, Secret, Now));

            onTeamZero[16] = 1;                       // OffsetTeam — promote yourself to team 1

            Assert.Equal(TicketVerifyResult.BadSignature,
                         JoinTicket.Verify(onTeamZero, Secret, Now));
            Assert.Equal(ConnectDenyReason.InvalidTicket,
                         JoinTicket.ToDenyReason(TicketVerifyResult.BadSignature));
        }

        [Fact]
        public void TwoTicketsDifferingOnlyInTeamHaveDifferentSignatures()
        {
            // The tamper test above proves the byte is covered. This proves it is covered by
            // ISSUE too, not only by Verify — the pair is what catches a signer and a verifier
            // that disagree about which 32 bytes they cover.
            byte[] a = IssueSample(team: 0);
            byte[] b = IssueSample(team: 1);

            Assert.NotEqual(
                Hex.ToHex(a.AsSpan(JoinTicket.SignedPayloadSize)),
                Hex.ToHex(b.AsSpan(JoinTicket.SignedPayloadSize)));
        }

        [Fact]
        public void ATeamAboveOneIsRefusedRatherThanClamped()
        {
            // TeamId.None is legal in S_MATCH_STATE.WinningTeam and in a spawn point's owner;
            // it is not legal here. Clamping to 0 would silently put a player on a side nobody
            // chose and the caller would never learn it had a bug.
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(-1, JoinTicket.Issue(ticket, 1, 1, 1, Now + 1000, 2, "x", Secret));
            Assert.Equal(-1, JoinTicket.Issue(ticket, 1, 1, 1, Now + 1000, 255, "x", Secret));

            // And nothing was written on the way to refusing.
            Assert.All(ticket, b => Assert.Equal(0, b));
        }

        // ------------------------------------------------- P13: the 15-byte name, truncated

        [Fact]
        public void A16CharacterAsciiNameLosesItsLastCharacterAndNothingElse()
        {
            // Criterion 2, input one. 16 ASCII characters is 16 bytes into a 15-byte field:
            // the cut is at a character boundary already, so exactly one character is lost.
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(JoinTicket.Size, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000, 1, "abcdefghijklmnop", Secret));

            Assert.True(JoinTicket.TryReadFields(
                ticket, out _, out _, out _, out _, out byte team, out string displayName));

            Assert.Equal("abcdefghijklmno", displayName);
            Assert.DoesNotContain('�', displayName);

            // The name lost a character and the team did not lose anything. That ordering is
            // the reason team sits BEFORE the name in the layout.
            Assert.Equal(1, team);
        }

        [Theory]
        // 14 ASCII + ư (U+01B0, 2 bytes) = 16 bytes. Byte 15 is that character's SECOND byte.
        [InlineData("abcdefghijklmnư", "abcdefghijklmn")]
        // 13 ASCII + ệ (U+1EC7, 3 bytes) = 16 bytes. Byte 15 is that character's THIRD byte,
        // so the back-off has to walk two continuation bytes, not one.
        [InlineData("abcdefghijklmệ", "abcdefghijklm")]
        public void AMultiByteCharacterStraddlingByte15IsDroppedWhole(
            string name, string expected)
        {
            // Criterion 2, input two — the one that will actually catch something. A cut
            // through the middle of a UTF-8 sequence does not throw and does not truncate: it
            // decodes to U+FFFD, and the player's name then carries a replacement glyph in
            // every killfeed line for the whole match.
            var ticket = new byte[JoinTicket.Size];
            Assert.Equal(JoinTicket.Size, JoinTicket.Issue(
                ticket, 1, 1, 1, Now + 1000, 1, name, Secret));

            Assert.True(JoinTicket.TryReadFields(
                ticket, out _, out _, out _, out _, out byte team, out string displayName));

            Assert.Equal(expected, displayName);
            Assert.DoesNotContain('�', displayName);
            Assert.True(Encoding.UTF8.GetByteCount(displayName) <= JoinTicket.DisplayNameSize);
            Assert.Equal(1, team);
            Assert.Equal(TicketVerifyResult.Valid, JoinTicket.Verify(ticket, Secret, Now));
        }
    }
}
