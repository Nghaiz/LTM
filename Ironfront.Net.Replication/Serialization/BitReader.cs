using System;

namespace Ironfront.Net.Replication.Serialization
{
    /// <summary>
    /// Reads individual bits back out of a buffer, least-significant bit first. The exact
    /// inverse of <see cref="BitWriter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field read here comes off the network, so running past the end of the buffer is
    /// a routine event, not an exceptional one: a truncated or hostile packet latches
    /// <see cref="Ok"/> false and every later read returns 0. Check <see cref="Ok"/> once
    /// before trusting anything parsed — never mid-parse, and never by catching.
    /// </para>
    /// </remarks>
    public ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _bitPosition;
        private bool _ok;

        public BitReader(ReadOnlySpan<byte> buffer)
        {
            _buffer      = buffer;
            _bitPosition = 0;
            _ok          = true;
        }

        /// <summary>False once any read has run past the end of the buffer.</summary>
        public bool Ok => _ok;

        /// <summary>Bits consumed so far.</summary>
        public int BitsRead => _bitPosition;

        /// <summary>Bits left in the buffer.</summary>
        public int BitsRemaining => _ok ? _buffer.Length * 8 - _bitPosition : 0;

        /// <summary>True when the next read starts on a byte boundary.</summary>
        public bool IsByteAligned => _bitPosition % 8 == 0;

        /// <summary>
        /// Reads <paramref name="bitCount"/> bits into the low bits of the result. Returns 0
        /// and latches <see cref="Ok"/> false when the buffer is exhausted.
        /// </summary>
        /// <param name="bitCount">1..32.</param>
        public uint ReadBits(int bitCount)
        {
            if (!_ok) return 0;

            if (bitCount <= 0 || bitCount > 32) { _ok = false; return 0; }
            if (_bitPosition + bitCount > _buffer.Length * 8) { _ok = false; return 0; }

            uint result = 0;
            int read = 0;

            while (read < bitCount)
            {
                int byteIndex = (_bitPosition + read) / 8;
                int bitOffset = (_bitPosition + read) % 8;
                int roomInByte = 8 - bitOffset;
                int chunk = Math.Min(roomInByte, bitCount - read);

                uint mask  = chunk == 32 ? uint.MaxValue : (1u << chunk) - 1u;
                uint bits  = ((uint)_buffer[byteIndex] >> bitOffset) & mask;

                result |= bits << read;
                read += chunk;
            }

            _bitPosition += bitCount;
            return result;
        }

        public bool ReadBool() => ReadBits(1) != 0;

        public byte ReadByte() => (byte)ReadBits(8);

        public sbyte ReadSByte() => unchecked((sbyte)(byte)ReadBits(8));

        public ushort ReadUInt16() => (ushort)ReadBits(16);

        public short ReadInt16() => unchecked((short)(ushort)ReadBits(16));

        public uint ReadUInt32() => ReadBits(32);

        /// <summary>
        /// Skips forward to the next byte boundary. Unlike <see cref="BitWriter.AlignToByte"/>
        /// this never fails on a full buffer — skipping to the end is legal, reading past it
        /// is not.
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
    }
}
