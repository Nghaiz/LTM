using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Splits an oversized logical payload into GSP fragment payloads.
    /// protocol-spec.md section 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mainly fires on the first full snapshot after joining a match
    /// (64 actors x 20 B = 1280 B, over the 1184 B limit) and on S_PLAYER_LIST.
    /// </para>
    /// <para>
    /// The class produces the GSP <em>payload</em> region only — a
    /// <see cref="FragmentHeader"/> followed by the data slice. Assigning sequence
    /// numbers and prepending the <see cref="GspHeader"/> belongs to the transport layer
    /// (Ironfront.Net.Transport, Dev B), which also owns the requirement that every
    /// fragment is sent reliably: lose one and the whole group is useless.
    /// </para>
    /// </remarks>
    public static class Fragmenter
    {
        /// <summary>True when the payload does not fit in a single unfragmented datagram.</summary>
        public static bool NeedsFragmentation(int payloadLength)
            => payloadLength > ProtocolConstants.MAX_PAYLOAD;

        /// <summary>
        /// Number of fragments a payload of this length splits into, or -1 when it
        /// exceeds what <see cref="ProtocolConstants.MAX_FRAGMENTS"/> fragments can carry
        /// (64 x 1180 = 75,520 bytes).
        /// </summary>
        public static int FragmentCount(int payloadLength)
        {
            if (payloadLength <= 0) return 0;

            int capacity = FragmentHeader.PayloadCapacity;
            int count = (payloadLength + capacity - 1) / capacity;
            return count > ProtocolConstants.MAX_FRAGMENTS ? -1 : count;
        }

        /// <summary>
        /// The slice of <paramref name="payload"/> carried by fragment
        /// <paramref name="index"/>. Does not copy.
        /// </summary>
        public static bool TrySliceFragment(
            ReadOnlySpan<byte> payload, byte index, out ReadOnlySpan<byte> slice)
        {
            slice = ReadOnlySpan<byte>.Empty;

            int count = FragmentCount(payload.Length);
            if (count <= 0 || index >= count) return false;

            int capacity = FragmentHeader.PayloadCapacity;
            int offset   = index * capacity;
            int length   = Math.Min(capacity, payload.Length - offset);

            slice = payload.Slice(offset, length);
            return true;
        }

        /// <summary>
        /// Writes the complete GSP payload region for one fragment —
        /// <see cref="FragmentHeader"/> followed by that fragment's data — into
        /// <paramref name="dst"/>.
        /// </summary>
        /// <returns>Bytes written, or -1 on failure.</returns>
        public static int WriteFragmentPayload(
            Span<byte> dst, ReadOnlySpan<byte> payload, ushort groupId, byte index)
        {
            int count = FragmentCount(payload.Length);
            if (count <= 0 || index >= count) return -1;

            if (!TrySliceFragment(payload, index, out ReadOnlySpan<byte> slice)) return -1;

            int total = FragmentHeader.Size + slice.Length;
            if (dst.Length < total) return -1;

            var header = new FragmentHeader(groupId, index, (byte)count);
            if (!header.TryWrite(dst)) return -1;

            slice.CopyTo(dst.Slice(FragmentHeader.Size, slice.Length));
            return total;
        }
    }
}
