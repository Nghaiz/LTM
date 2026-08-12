using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Sequential little-endian reader over a received buffer. Allocation-free.
    /// </summary>
    /// <remarks>
    /// Reading past the end does not throw — corrupt and truncated packets are routine on
    /// a UDP socket, not exceptional (conventions.md section 3.2). A short read latches
    /// <see cref="Ok"/> to false and returns default(T); the caller checks
    /// <see cref="Ok"/> once after parsing the whole message.
    /// </remarks>
    public ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _position;
        private bool _ok;

        public SpanReader(ReadOnlySpan<byte> buffer)
        {
            _buffer   = buffer;
            _position = 0;
            _ok       = true;
        }

        /// <summary>False once any read has run past the end of the buffer.</summary>
        public bool Ok => _ok;

        public int Position => _position;

        public int Remaining => _ok ? _buffer.Length - _position : 0;

        private bool Require(int count)
        {
            if (!_ok) return false;
            if (_position + count > _buffer.Length)
            {
                _ok = false;
                return false;
            }
            return true;
        }

        public byte ReadU8()
        {
            if (!Require(1)) return 0;
            return _buffer[_position++];
        }

        public sbyte ReadI8() => unchecked((sbyte)ReadU8());

        public ushort ReadU16()
        {
            if (!Require(2)) return 0;
            ushort v = Endian.ReadU16LE(_buffer, _position);
            _position += 2;
            return v;
        }

        public short ReadI16() => unchecked((short)ReadU16());

        public uint ReadU32()
        {
            if (!Require(4)) return 0;
            uint v = Endian.ReadU32LE(_buffer, _position);
            _position += 4;
            return v;
        }

        public int ReadI32() => unchecked((int)ReadU32());

        public ulong ReadU64()
        {
            if (!Require(8)) return 0;
            ulong v = Endian.ReadU64LE(_buffer, _position);
            _position += 8;
            return v;
        }

        /// <summary>
        /// Returns a slice of <paramref name="count"/> bytes without copying. The slice
        /// points into the original buffer, so it is only valid while that buffer is.
        /// </summary>
        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (!Require(count)) return ReadOnlySpan<byte>.Empty;
            ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
            _position += count;
            return slice;
        }

        public void Skip(int count)
        {
            if (!Require(count)) return;
            _position += count;
        }
    }
}
