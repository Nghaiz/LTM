using System;
using System.Security.Cryptography;
using System.Text;

namespace Ironfront.Net.Protocol
{
    /// <summary>Why a joinTicket failed verification.</summary>
    public enum TicketVerifyResult
    {
        Valid = 0,
        /// <summary>Wrong length, or a null/empty secret.</summary>
        Malformed = 1,
        /// <summary>HMAC mismatch — forged or tampered with.</summary>
        BadSignature = 2,
        /// <summary>Signature was fine but the 60-second window has passed.</summary>
        Expired = 3,
    }

    /// <summary>
    /// The 64-byte HMAC-signed ticket that bridges TCP (master server) and UDP (game
    /// server). protocol-spec.md section 12.
    /// </summary>
    /// <remarks>
    /// <code>
    ///   u32    playerId
    ///   u16    serverId
    ///   u16    roomId
    ///   u64    expiresAtUnixMs        (valid for 60 seconds from issue)
    ///   u8[16] displayNameUtf8        (truncated/padded to 16 bytes)
    ///   u8[32] hmac                   = HMAC-SHA256(the first 32 payload bytes, SHARED_SECRET)
    /// </code>
    /// <para>
    /// The master server issues the ticket in ROOM_JOIN_RES; the client passes it through
    /// verbatim in CONNECT_REQUEST; the game server verifies it locally with the shared
    /// secret. No callback to the master is needed, so there is no added latency and no
    /// dependency on the master still being alive at join time.
    /// </para>
    /// <para>
    /// The sessionToken is deliberately NOT used for this. It is a long-lived login
    /// secret, and sending it unencrypted over UDP to a game server — potentially operated
    /// by a third party — would leak it. This ticket expires in 60 seconds and is only
    /// valid for one specific server.
    /// </para>
    /// <para>
    /// <b>Issue and Verify live together on purpose.</b> They are the two halves of one
    /// algorithm; splitting them across Dev D's and Dev C's projects is how the two sides
    /// end up disagreeing about which bytes are signed, with every single ticket rejected
    /// and nothing to point at. See dev-d-master-server/phases/phase-02-matchmaking.md.
    /// </para>
    /// </remarks>
    public static class JoinTicket
    {
        public const int Size = ProtocolConstants.JOIN_TICKET_SIZE;   // 64

        /// <summary>The bytes covered by the HMAC: everything before the signature.</summary>
        public const int SignedPayloadSize = 32;

        public const int HmacSize = 32;

        public const int DisplayNameSize = 16;

        /// <summary>Ticket lifetime, per protocol-spec.md section 12.</summary>
        public const long ValidityMs = 60_000;

        // Field offsets.
        private const int OffsetPlayerId    = 0;
        private const int OffsetServerId    = 4;
        private const int OffsetRoomId      = 6;
        private const int OffsetExpiresAt   = 8;
        private const int OffsetDisplayName = 16;
        private const int OffsetHmac        = 32;

        /// <summary>
        /// Issues a signed ticket into <paramref name="dst"/> (which must be at least
        /// <see cref="Size"/> bytes). Returns bytes written, or -1.
        /// </summary>
        /// <param name="expiresAtUnixMs">
        /// Absolute expiry. Callers normally pass <c>nowUnixMs + </c>
        /// <see cref="ValidityMs"/>.
        /// </param>
        public static int Issue(
            Span<byte> dst,
            uint playerId,
            ushort serverId,
            ushort roomId,
            long expiresAtUnixMs,
            string? displayName,
            ReadOnlySpan<byte> sharedSecret)
        {
            if (dst.Length < Size) return -1;
            if (sharedSecret.Length == 0) return -1;

            dst.Slice(0, Size).Clear();

            Endian.WriteU32LE(dst, OffsetPlayerId, playerId);
            Endian.WriteU16LE(dst, OffsetServerId, serverId);
            Endian.WriteU16LE(dst, OffsetRoomId, roomId);
            Endian.WriteU64LE(dst, OffsetExpiresAt, unchecked((ulong)expiresAtUnixMs));
            WriteDisplayName(dst.Slice(OffsetDisplayName, DisplayNameSize), displayName);

            ComputeHmac(dst.Slice(0, SignedPayloadSize), sharedSecret,
                        dst.Slice(OffsetHmac, HmacSize));
            return Size;
        }

        /// <summary>
        /// Verifies a ticket's signature and expiry.
        /// </summary>
        /// <param name="nowUnixMs">Current wall-clock time in Unix milliseconds.</param>
        /// <remarks>
        /// A failure of any kind maps to
        /// <see cref="ConnectDenyReason.InvalidTicket"/> (code 3) — the game server must
        /// not tell the caller which check failed, since that turns the handshake into an
        /// oracle for forging tickets.
        /// </remarks>
        public static TicketVerifyResult Verify(
            ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> sharedSecret, long nowUnixMs)
        {
            if (ticket.Length < Size) return TicketVerifyResult.Malformed;
            if (sharedSecret.Length == 0) return TicketVerifyResult.Malformed;

            Span<byte> expected = stackalloc byte[HmacSize];
            ComputeHmac(ticket.Slice(0, SignedPayloadSize), sharedSecret, expected);

            if (!FixedTimeEquals(expected, ticket.Slice(OffsetHmac, HmacSize)))
                return TicketVerifyResult.BadSignature;

            long expiresAt = unchecked((long)Endian.ReadU64LE(ticket, OffsetExpiresAt));
            if (nowUnixMs > expiresAt) return TicketVerifyResult.Expired;

            return TicketVerifyResult.Valid;
        }

        /// <summary>
        /// Reads the ticket fields. Call <see cref="Verify"/> FIRST — these values are
        /// attacker-controlled until the signature has been checked.
        /// </summary>
        public static bool TryReadFields(
            ReadOnlySpan<byte> ticket,
            out uint playerId,
            out ushort serverId,
            out ushort roomId,
            out long expiresAtUnixMs,
            out string displayName)
        {
            playerId        = 0;
            serverId        = 0;
            roomId          = 0;
            expiresAtUnixMs = 0;
            displayName     = string.Empty;

            if (ticket.Length < Size) return false;

            playerId        = Endian.ReadU32LE(ticket, OffsetPlayerId);
            serverId        = Endian.ReadU16LE(ticket, OffsetServerId);
            roomId          = Endian.ReadU16LE(ticket, OffsetRoomId);
            expiresAtUnixMs = unchecked((long)Endian.ReadU64LE(ticket, OffsetExpiresAt));
            displayName     = ReadDisplayName(ticket.Slice(OffsetDisplayName, DisplayNameSize));
            return true;
        }

        /// <summary>
        /// Maps a verification result onto the CONNECT_DENIED reason code the game server
        /// sends back. Every failure collapses to code 3 by design.
        /// </summary>
        public static ConnectDenyReason ToDenyReason(TicketVerifyResult result)
            => result == TicketVerifyResult.Valid
                ? ConnectDenyReason.None
                : ConnectDenyReason.InvalidTicket;

        // ------------------------------------------------------------------ internals

        private static void ComputeHmac(
            ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key, Span<byte> destination)
        {
            using var hmac = new HMACSHA256(key.ToArray());

            // netstandard2.1 exposes TryComputeHash, so no intermediate array is needed.
            if (!hmac.TryComputeHash(payload, destination, out int written) || written != HmacSize)
            {
                // Cannot happen with SHA-256 and a 32-byte destination; zero the output
                // rather than leaving it uninitialized if it somehow does.
                destination.Clear();
            }
        }

        /// <summary>
        /// Constant-time comparison. A byte-by-byte early-exit compare leaks how many
        /// leading bytes matched, which is enough to forge a signature one byte at a time.
        /// </summary>
        private static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void WriteDisplayName(Span<byte> dst, string? displayName)
        {
            dst.Clear();
            if (string.IsNullOrEmpty(displayName)) return;

            byte[] utf8 = Encoding.UTF8.GetBytes(displayName!);
            int count = utf8.Length > dst.Length ? dst.Length : utf8.Length;
            utf8.AsSpan(0, count).CopyTo(dst);
        }

        private static string ReadDisplayName(ReadOnlySpan<byte> src)
        {
            int end = 0;
            while (end < src.Length && src[end] != 0) end++;
            return end == 0 ? string.Empty : Encoding.UTF8.GetString(src.Slice(0, end).ToArray());
        }
    }
}
