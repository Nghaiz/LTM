using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Sequential little-endian writer over a caller-supplied buffer. Allocation-free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Overflow does not throw. conventions.md section 3.2 forbids exceptions for routine
    /// conditions, and a buffer that turned out too small is routine when batching
    /// messages. Instead the writer latches <see cref="Ok"/> to false and every
    /// subsequent write is a no-op, so a caller can write a whole message optimistically
    /// and check <see cref="Ok"/> once at the end.
    /// </para>
    /// <para>
    /// This is a ref struct: it can only live on the stack, which is what makes it safe
    /// to hold a <see cref="Span{T}"/> and free of GC pressure in the 30 Hz tick loop.
    /// </para>
    /// </remarks>
    public ref struct SpanWriter
    {
        private readonly Span<byte> _buffer;
        private int _position;
        private bool _ok;

        public SpanWriter(Span<byte> buffer)
        {
            _buffer   = buffer;
            _position = 0;
            _ok       = true;
        }

        /// <summary>False once any write has overflowed the buffer.</summary>
        public bool Ok => _ok;

        /// <summary>Bytes written so far. Meaningless when <see cref="Ok"/> is false.</summary>
        public int Position => _position;

        /// <summary>Bytes still available.</summary>
        public int Remaining => _buffer.Length - _position;

        private bool Reserve(int count)
        {
            if (!_ok) return false;
            if (_position + count > _buffer.Length)
            {
                _ok = false;
                return false;
            }
            return true;
        }

        public void WriteU8(byte v)
        {
            if (!Reserve(1)) return;
            _buffer[_position++] = v;
        }

        public void WriteI8(sbyte v) => WriteU8(unchecked((byte)v));

        public void WriteU16(ushort v)
        {
            if (!Reserve(2)) return;
            Endian.WriteU16LE(_buffer, _position, v);
            _position += 2;
        }

        public void WriteI16(short v) => WriteU16(unchecked((ushort)v));

        public void WriteU32(uint v)
        {
            if (!Reserve(4)) return;
            Endian.WriteU32LE(_buffer, _position, v);
            _position += 4;
        }

        public void WriteI32(int v) => WriteU32(unchecked((uint)v));

        public void WriteU64(ulong v)
        {
            if (!Reserve(8)) return;
            Endian.WriteU64LE(_buffer, _position, v);
            _position += 8;
        }

        public void WriteBytes(ReadOnlySpan<byte> src)
        {
            if (!Reserve(src.Length)) return;
            src.CopyTo(_buffer.Slice(_position, src.Length));
            _position += src.Length;
        }

        /// <summary>Writes <paramref name="count"/> zero bytes (reserved fields, padding).</summary>
        public void WriteZeros(int count)
        {
            if (!Reserve(count)) return;
            _buffer.Slice(_position, count).Clear();
            _position += count;
        }

        /// <summary>The bytes written so far. Empty when <see cref="Ok"/> is false.</summary>
        public ReadOnlySpan<byte> Written
            => _ok ? _buffer.Slice(0, _position) : ReadOnlySpan<byte>.Empty;
    }
}
