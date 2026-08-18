using System;

namespace Ironfront.Net.Replication.Serialization
{
    /// <summary>
    /// Writes individual bits into a caller-supplied buffer, least-significant bit first.
    /// </summary>
    /// <remarks>
    /// <para>
    ///md section 7). The replication track writes the conformance tests that judge
    /// it and does not edit this file.
    /// </para>
    /// <para>
    /// <b>Bit order is LSB-first, and it is a wire contract.</b> The first
    /// <see cref="WriteBits"/> call fills bit 0 of byte 0 upward. Writing <c>0b101</c> in 3
    /// bits then <c>0b11</c> in 2 produces the single byte <c>0b00011101</c>. Getting this
    /// backwards is not a crash, it is a silently corrupt field, which is why the conformance
    /// suite pins it against hand-written hex rather than a round-trip.
    /// </para>
    /// <para>
    /// Multi-byte helpers are defined as one <see cref="WriteBits"/> call of the matching
    /// width, so on a byte boundary they emit exactly the little-endian layout
    /// <c>Ironfront.Net.Protocol.Endian</c> produces. The two writers agree by construction
    /// rather than by coincidence.
    /// </para>
    /// <para>
    /// Overflow does not throw (conventions.md section 3.2): <see cref="Ok"/> latches false
    /// and every later write is a no-op, so a caller writes a whole message optimistically
    /// and checks once.
    /// </para>
    /// </remarks>
    public ref struct BitWriter
    {
        private readonly Span<byte> _buffer;

        /// <summary>Bits committed so far, counting from the start of the buffer.</summary>
        private int _bitPosition;
        private bool _ok;

        public BitWriter(Span<byte> buffer)
        {
            _buffer      = buffer;
            _bitPosition = 0;
            _ok          = true;
        }

        /// <summary>False once any write has run past the end of the buffer.</summary>
        public bool Ok => _ok;

        /// <summary>Bits written so far.</summary>
        public int BitsWritten => _bitPosition;

        /// <summary>
        /// Bytes needed to hold what has been written, rounding a partial byte up. This is
        /// the length to hand to the transport.
        /// </summary>
        public int BytesWritten => (_bitPosition + 7) / 8;

        /// <summary>Bits still available.</summary>
        public int BitsRemaining => _ok ? _buffer.Length * 8 - _bitPosition : 0;

        /// <summary>
        /// Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>.
        /// Bits above <paramref name="bitCount"/> are ignored rather than corrupting the next
        /// field.
        /// </summary>
        /// <param name="bitCount">1..32.</param>
        public void WriteBits(uint value, int bitCount)
        {
            if (!_ok) return;

            if (bitCount <= 0 || bitCount > 32) { _ok = false; return; }
            if (_bitPosition + bitCount > _buffer.Length * 8) { _ok = false; return; }

            // Mask first: a caller passing 0xFF into 3 bits must write 0b111 and leave the
            // following field alone, not smear five stray bits across it.
            uint masked = bitCount == 32 ? value : value & ((1u << bitCount) - 1u);

            int written = 0;
            while (written < bitCount)
            {
                int byteIndex  = (_bitPosition + written) / 8;
                int bitOffset  = (_bitPosition + written) % 8;
                int roomInByte = 8 - bitOffset;
                int chunk      = Math.Min(roomInByte, bitCount - written);

                uint chunkBits = (masked >> written) & (chunk == 32 ? uint.MaxValue : ((1u << chunk) - 1u));

                _buffer[byteIndex] = (byte)(_buffer[byteIndex] | (byte)(chunkBits << bitOffset));
                written += chunk;
            }

            _bitPosition += bitCount;
        }

        public void WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);

        public void WriteByte(byte value) => WriteBits(value, 8);

        public void WriteUInt16(ushort value) => WriteBits(value, 16);

        public void WriteUInt32(uint value) => WriteBits(value, 32);

        public void WriteInt16(short value) => WriteBits(unchecked((ushort)value), 16);

        public void WriteSByte(sbyte value) => WriteBits(unchecked((byte)value), 8);

        /// <summary>
        /// Pads with zero bits up to the next byte boundary. A no-op when already aligned.
        /// </summary>
        public void AlignToByte()
        {
            if (!_ok) return;

            int stray = _bitPosition % 8;
            if (stray == 0) return;

            int padding = 8 - stray;
            if (_bitPosition + padding > _buffer.Length * 8) { _ok = false; return; }

            _bitPosition += padding;
        }

        /// <summary>True when the next write starts on a byte boundary.</summary>
        public bool IsByteAligned => _bitPosition % 8 == 0;

        /// <summary>
        /// The finished bytes, partial trailing byte included. Empty when
        /// <see cref="Ok"/> is false.
        /// </summary>
        public ReadOnlySpan<byte> Written
            => _ok ? _buffer.Slice(0, BytesWritten) : ReadOnlySpan<byte>.Empty;
    }
}
