using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    internal static class PacketBuilder
    {
        public static int Write(
            byte[] destination,
            PacketType packetType,
            PacketFlags flags,
            ushort sequence,
            ushort ack,
            uint ackBitfield,
            ushort connectionId,
            ReadOnlySpan<byte> payload)
        {
            if (payload.Length > ProtocolConstants.MAX_PAYLOAD
                || destination.Length < GspHeader.Size + payload.Length)
                return -1;

            var header = new GspHeader(
                packetType, flags, sequence, ack, ackBitfield, connectionId, (ushort)payload.Length);
            if (!header.TryWrite(destination)) return -1;
            payload.CopyTo(destination.AsSpan(GspHeader.Size, payload.Length));
            return GspHeader.Size + payload.Length;
        }

        /// <summary>
        /// Re-stamps an already-built datagram with a new sequence and a fresh ack window,
        /// leaving every other field alone. This is how a retransmission becomes a genuinely
        /// new packet rather than a copy the receiver can no longer address (X-32).
        /// </summary>
        /// <remarks>
        /// The offsets live here and in <see cref="GspHeader.TryWrite"/> and nowhere else. The
        /// ack is refreshed as well as the sequence because a retransmission is one more of the
        /// "33 pieces of ack information" protocol-spec.md § 2.2 counts on, and shipping a
        /// stale window would waste it.
        /// </remarks>
        public static bool TryRestamp(
            Span<byte> datagram, ushort sequence, ushort ack, uint ackBitfield)
        {
            if (datagram.Length < GspHeader.Size) return false;

            Endian.WriteU16LE(datagram, 4, sequence);
            Endian.WriteU16LE(datagram, 6, ack);
            Endian.WriteU32LE(datagram, 8, ackBitfield);
            return true;
        }
    }
}
