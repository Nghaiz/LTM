using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The 4-byte fragment header, placed immediately after the GSP header when
    /// <see cref="PacketFlags.Fragmented"/> is set. protocol-spec.md section 6.
    /// </summary>
    /// <remarks>
    /// <code>
    /// u16  fragmentGroupId      Fragment group id, incrementing
    /// u8   fragmentIndex        0-based
    /// u8   fragmentCount        Total fragments, &lt;= 64
    /// </code>
    /// </remarks>
    public readonly struct FragmentHeader
    {
        public const int Size = 4;

        /// <summary>
        /// Bytes of logical payload one fragment can carry: MAX_PAYLOAD minus this
        /// header, which sits inside the payload region. 1184 - 4 = 1180.
        /// </summary>
        public const int PayloadCapacity = ProtocolConstants.MAX_PAYLOAD - Size;

        public readonly ushort GroupId;
        public readonly byte Index;
        public readonly byte Count;

        public FragmentHeader(ushort groupId, byte index, byte count)
        {
            GroupId = groupId;
            Index   = index;
            Count   = count;
        }

        public bool TryWrite(Span<byte> dst)
        {
            if (dst.Length < Size) return false;
            Endian.WriteU16LE(dst, 0, GroupId);
            dst[2] = Index;
            dst[3] = Count;
            return true;
        }

        /// <summary>
        /// Parses a fragment header. Rejects counts of zero, counts above
        /// <see cref="ProtocolConstants.MAX_FRAGMENTS"/>, and an index outside the count —
        /// all three are how a malicious peer would try to make the reassembler allocate
        /// or index out of range.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> src, out FragmentHeader header)
        {
            header = default;
            if (src.Length < Size) return false;

            byte count = src[3];
            byte index = src[2];
            if (count == 0 || count > ProtocolConstants.MAX_FRAGMENTS) return false;
            if (index >= count) return false;

            header = new FragmentHeader(Endian.ReadU16LE(src, 0), index, count);
            return true;
        }
    }
}
