using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Byte-order primitives written with explicit shifts.
    /// </summary>
    /// <remarks>
    /// protocol-spec.md section 0 requires that the code must NOT depend on
    /// <c>BitConverter.IsLittleEndian</c>. BitConverter happens to be little-endian on
    /// x86 and ARM, but relying on that makes the wire format a property of the host CPU
    /// rather than of the protocol. Manual shifts produce identical bytes everywhere.
    /// </remarks>
    public static class Endian
    {
        // ------------------------------------------------------------- little-endian
        // Used by all of GSP, and by the MSP msgType field.

        public static void WriteU16LE(Span<byte> dst, int offset, ushort v)
        {
            dst[offset]     = (byte)v;
            dst[offset + 1] = (byte)(v >> 8);
        }

        public static ushort ReadU16LE(ReadOnlySpan<byte> src, int offset)
            => (ushort)(src[offset] | (src[offset + 1] << 8));

        public static void WriteU32LE(Span<byte> dst, int offset, uint v)
        {
            dst[offset]     = (byte)v;
            dst[offset + 1] = (byte)(v >> 8);
            dst[offset + 2] = (byte)(v >> 16);
            dst[offset + 3] = (byte)(v >> 24);
        }

        public static uint ReadU32LE(ReadOnlySpan<byte> src, int offset)
            => (uint)(src[offset]
                    | (src[offset + 1] << 8)
                    | (src[offset + 2] << 16)
                    | (src[offset + 3] << 24));

        public static void WriteU64LE(Span<byte> dst, int offset, ulong v)
        {
            for (int i = 0; i < 8; i++) dst[offset + i] = (byte)(v >> (i * 8));
        }

        public static ulong ReadU64LE(ReadOnlySpan<byte> src, int offset)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)src[offset + i] << (i * 8);
            return v;
        }

        // ---------------------------------------------------------------- big-endian
        // Used ONLY by the MSP frame length prefix (protocol-spec.md section 10, which
        // calls it out as the network standard). Everything else stays little-endian.

        public static void WriteU32BE(Span<byte> dst, int offset, uint v)
        {
            dst[offset]     = (byte)(v >> 24);
            dst[offset + 1] = (byte)(v >> 16);
            dst[offset + 2] = (byte)(v >> 8);
            dst[offset + 3] = (byte)v;
        }

        public static uint ReadU32BE(ReadOnlySpan<byte> src, int offset)
            => (uint)((src[offset] << 24)
                    | (src[offset + 1] << 16)
                    | (src[offset + 2] << 8)
                    |  src[offset + 3]);
    }
}
