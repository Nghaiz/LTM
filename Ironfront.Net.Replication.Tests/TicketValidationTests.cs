using System;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-03 task 4 and trap 4: the game server's half of the joinTicket handshake.
    /// </summary>
    public sealed class TicketValidationTests
    {
        private const long Now = 1_800_000_000_000L;
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-shared-secret");

        private static byte[] Issue(
            uint playerId, ushort serverId = 7, long? expiresAt = null, byte[]? secret = null)
        {
            var ticket = new byte[JoinTicket.Size];
            JoinTicket.Issue(
                ticket, playerId, serverId, roomId: 1,
                expiresAt ?? Now + JoinTicket.ValidityMs, "player", secret ?? Secret);
            return ticket;
        }

        // ------------------------------------------------------------------ the happy path

        [Fact]
        public void AWellSignedTicketIsAdmittedAndNamesItsPlayer()
        {
            var validator = new TicketValidator(Secret, serverId: 7);

            Assert.True(validator.TryAdmit(Issue(42), Now, out uint playerId, out TicketRejection reason));
            Assert.Equal(42u, playerId);
            Assert.Equal(TicketRejection.None, reason);
            Assert.Equal(1, validator.Accepted);
        }

        // ------------------------------------------------------------------ rejections

        [Fact]
        public void ATamperedTicketIsRejected()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            byte[] ticket = Issue(42);
            ticket[0] ^= 0xFF;                              // change the playerId

            Assert.False(validator.TryAdmit(ticket, Now, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.BadSignature, reason);
        }

        [Fact]
        public void ATicketSignedWithTheWrongSecretIsRejected()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            byte[] forged = Issue(42, secret: Encoding.UTF8.GetBytes("not-the-secret"));

            Assert.False(validator.TryAdmit(forged, Now, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.BadSignature, reason);
        }

        [Fact]
        public void AnExpiredTicketIsRejectedAtTheInstantItLapses()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            byte[] ticket = Issue(42, expiresAt: Now + 1000);

            Assert.True(validator.TryAdmit(ticket, Now + 999, out _, out _));
            validator.Release(42);

            Assert.False(validator.TryAdmit(ticket, Now + 1000, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.Expired, reason);
        }

        [Fact]
        public void ATicketForAnotherServerIsRejected()
        {
            var validator = new TicketValidator(Secret, serverId: 7);

            // Correctly signed by the master, genuinely unexpired — and issued for a different
            // game server. Without this check one ticket admits its holder anywhere in the fleet.
            Assert.False(validator.TryAdmit(Issue(42, serverId: 9), Now, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.WrongServer, reason);
        }

        [Fact]
        public void AShortTicketIsRejectedRatherThanReadPastItsEnd()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            byte[] truncated = new byte[JoinTicket.Size - 1];

            Assert.False(validator.TryAdmit(truncated, Now, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.Malformed, reason);
        }

        [Fact]
        public void AServerWithNoSecretRejectsEverything()
        {
            // Fail-closed. A server that silently accepted unsigned tickets is one nobody
            // notices is open.
            var validator = new TicketValidator(ReadOnlySpan<byte>.Empty, serverId: 7);

            Assert.False(validator.TryAdmit(Issue(42), Now, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.Malformed, reason);
        }

        [Fact]
        public void AnUnregisteredServerStillChecksSignatureAndExpiry()
        {
            // serverId 0 means "the master has not told us who we are yet". Refusing every
            // ticket in that state would make standalone mode unjoinable; accepting an
            // unsigned one would be worse. Signature and expiry still apply.
            var validator = new TicketValidator(Secret, serverId: 0);

            Assert.True(validator.TryAdmit(Issue(42, serverId: 123), Now, out _, out _));

            byte[] tampered = Issue(43);
            tampered[5] ^= 0x01;
            Assert.False(validator.TryAdmit(tampered, Now, out _, out _));
        }

        // ------------------------------------------------------------------ replay

        [Fact]
        public void TheSameTicketCannotAdmitTwoClients()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            byte[] ticket = Issue(42);

            Assert.True(validator.TryAdmit(ticket, Now, out _, out _));

            // A leaked ticket is otherwise usable by everyone who has it for the whole 60 s
            // window, and none of them has finished connecting when the next is checked.
            Assert.False(validator.TryAdmit(ticket, Now, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.AlreadyConnected, reason);
        }

        [Fact]
        public void TwoDifferentTicketsForTheSamePlayerAlsoCollide()
        {
            var validator = new TicketValidator(Secret, serverId: 7);

            Assert.True(validator.TryAdmit(Issue(42), Now, out _, out _));
            Assert.False(validator.TryAdmit(Issue(42), Now + 1, out _, out TicketRejection reason));
            Assert.Equal(TicketRejection.AlreadyConnected, reason);
        }

        [Fact]
        public void AbandonedHandshakesLapseWithTheirTicketRatherThanLeakingASlot()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            long expiry = Now + 1000;

            Assert.True(validator.TryAdmit(Issue(42, expiresAt: expiry), Now, out _, out _));
            Assert.Equal(1, validator.ClaimCount);

            // The client never completed the handshake. No timer and no sweep — the claim
            // cannot outlive the ticket that created it.
            Assert.True(validator.TryAdmit(Issue(43, expiresAt: expiry + 5000), expiry, out _, out _));
            Assert.False(validator.IsClaimed(42));
        }

        [Fact]
        public void AConfirmedConnectionOutlivesItsTicket()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            long expiry = Now + 1000;

            validator.TryAdmit(Issue(42, expiresAt: expiry), Now, out uint playerId, out _);
            validator.ConfirmConnected(playerId);

            // An hour later the session is still live, so a replay must still be refused.
            Assert.True(validator.IsClaimed(42));
            Assert.False(validator.TryAdmit(
                Issue(42, expiresAt: Now + 3_600_000), Now + 3_000_000, out _,
                out TicketRejection reason));
            Assert.Equal(TicketRejection.AlreadyConnected, reason);
        }

        [Fact]
        public void ReleasingLetsThePlayerRejoin()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            validator.TryAdmit(Issue(42), Now, out _, out _);
            validator.ConfirmConnected(42);

            Assert.True(validator.Release(42));
            Assert.True(validator.TryAdmit(Issue(42), Now + 1, out _, out _));
        }

        // ------------------------------------------------------------------ admission pairing

        [Fact]
        public void AdmissionsAreHandedBackInTheOrderTheyWereMade()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            validator.TryAdmit(Issue(1), Now, out _, out _);
            validator.TryAdmit(Issue(2), Now, out _, out _);

            Assert.Equal(2, validator.PendingAdmissionCount);
            Assert.True(validator.TryTakePendingAdmission(out uint first));
            Assert.True(validator.TryTakePendingAdmission(out uint second));
            Assert.False(validator.TryTakePendingAdmission(out _));

            Assert.Equal(1u, first);
            Assert.Equal(2u, second);
        }

        [Fact]
        public void ARejectedTicketQueuesNoAdmission()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            validator.TryAdmit(Issue(42, serverId: 9), Now, out _, out _);

            Assert.Equal(0, validator.PendingAdmissionCount);
        }

        // ------------------------------------------------------------------ diagnostics

        [Fact]
        public void RejectionsAreCountedByReasonForTheServersOwnLogs()
        {
            var validator = new TicketValidator(Secret, serverId: 7);

            validator.TryAdmit(new byte[4], Now, out _, out _);
            validator.TryAdmit(Issue(42, serverId: 9), Now, out _, out _);

            byte[] tampered = Issue(43);
            tampered[1] ^= 0x7F;
            validator.TryAdmit(tampered, Now, out _, out _);

            Assert.Equal(3, validator.Rejected);
            Assert.Equal(1, validator.RejectionsByReason[(int)TicketRejection.Malformed]);
            Assert.Equal(1, validator.RejectionsByReason[(int)TicketRejection.WrongServer]);
            Assert.Equal(1, validator.RejectionsByReason[(int)TicketRejection.BadSignature]);
        }

        [Fact]
        public void EveryFailureCollapsesToOneDenyReasonOnTheWire()
        {
            // The counters above are for the server's log. What the client is told must not
            // distinguish the cases, or the handshake becomes an oracle for forging a ticket.
            Assert.Equal(
                ConnectDenyReason.InvalidTicket,
                JoinTicket.ToDenyReason(TicketVerifyResult.BadSignature));
            Assert.Equal(
                ConnectDenyReason.InvalidTicket,
                JoinTicket.ToDenyReason(TicketVerifyResult.Expired));
            Assert.Equal(
                ConnectDenyReason.InvalidTicket,
                JoinTicket.ToDenyReason(TicketVerifyResult.Malformed));
        }

        [Fact]
        public void ClearDropsClaimsAndPendingAdmissionsTogether()
        {
            var validator = new TicketValidator(Secret, serverId: 7);
            validator.TryAdmit(Issue(1), Now, out _, out _);
            validator.TryAdmit(Issue(2), Now, out _, out _);

            validator.Clear();

            Assert.Equal(0, validator.ClaimCount);
            Assert.Equal(0, validator.PendingAdmissionCount);
        }

        [Fact]
        public void SixteenPlayersJoiningAtOnceAreAllAdmittedExactlyOnce()
        {
            var validator = new TicketValidator(Secret, serverId: 7);

            for (uint player = 1; player <= ProtocolConstants.MAX_PLAYERS; player++)
                Assert.True(validator.TryAdmit(Issue(player), Now, out _, out _));

            Assert.Equal(ProtocolConstants.MAX_PLAYERS, validator.Accepted);
            Assert.Equal(ProtocolConstants.MAX_PLAYERS, validator.ClaimCount);
            Assert.Equal(0, validator.Rejected);
        }
    }
}
