using System;
using System.Collections.Generic;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// A fixed-size byte-buffer pool. Written by hand rather than using
    /// <c>ArrayPool&lt;byte&gt;</c> on purpose — conventions.md section 3.4 lists this as one
    /// of the two "write it yourself, then benchmark against the BCL" exercises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every buffer is exactly <see cref="BufferSize"/> bytes, which is what makes
    /// <see cref="Return"/> a push rather than a size-bucket lookup. The pool grows past
    /// its initial capacity rather than blocking: running dry mid-tick and stalling the
    /// server is worse than one allocation.
    /// </para>
    /// <para>
    /// Not thread-safe. The transport is single-threaded by design (B-AD-1).
    /// </para>
    /// </remarks>
    public sealed class BufferPool
    {
        private readonly Stack<byte[]> _free;

        public BufferPool(int capacity, int bufferSize)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));

            BufferSize = bufferSize;
            _free = new Stack<byte[]>(capacity);
            for (int i = 0; i < capacity; i++) _free.Push(new byte[bufferSize]);

            TotalBuffers = capacity;
        }

        public int BufferSize { get; }

        /// <summary>Buffers ever created. Grows only when the pool ran dry.</summary>
        public int TotalBuffers { get; private set; }

        /// <summary>Buffers currently idle in the pool.</summary>
        public int AvailableCount => _free.Count;

        /// <summary>Buffers currently rented out.</summary>
        public int RentedCount => TotalBuffers - _free.Count;

        /// <summary>
        /// Takes a buffer. Contents are undefined — treat it as uninitialized and write
        /// before reading.
        /// </summary>
        public byte[] Rent()
        {
            if (_free.Count > 0) return _free.Pop();

            // Ran dry. Grow rather than stall; GrewCount makes the event visible so the
            // initial capacity can be tuned instead of silently under-sized forever.
            GrewCount++;
            TotalBuffers++;
            return new byte[BufferSize];
        }

        /// <summary>How many times <see cref="Rent"/> had to allocate. Should settle at 0.</summary>
        public int GrewCount { get; private set; }

        /// <summary>
        /// Gives a buffer back. Returning a foreign or wrong-sized array is a caller bug and
        /// throws — silently accepting it would poison the pool for every later renter.
        /// </summary>
        public void Return(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length != BufferSize)
                throw new ArgumentException(
                    $"Buffer is {buffer.Length} bytes, pool holds {BufferSize}.", nameof(buffer));

#if DEBUG
            // A returned buffer must never be observed by an application callback. Filling it
            // makes accidental retention fail loudly and deterministically in debug builds.
            Array.Fill(buffer, (byte)0xDD);
#endif

            _free.Push(buffer);
        }
    }
}
