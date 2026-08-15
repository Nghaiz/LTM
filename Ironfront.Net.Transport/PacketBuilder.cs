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
    }
}
