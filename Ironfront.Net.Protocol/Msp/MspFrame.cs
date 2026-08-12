using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Master Server Protocol frame layout (TCP). protocol-spec.md section 10.
    /// </summary>
    /// <remarks>
    /// <code>
    /// u32  length        Byte count AFTER this field (msgType + body). Big-endian
    /// u16  msgType
    /// u8[] body          UTF-8 JSON
    /// </code>
    /// <para>
    /// <b>Byte order.</b> The <c>length</c> prefix is big-endian, called out explicitly in
    /// section 10 as the network standard. <c>msgType</c> is NOT called out, so it follows
    /// the section 0 default of little-endian. That mixed-endian frame is what this class
    /// implements. It is worth a second look during integration — if Dev A's client and
    /// Dev D's master server disagree on msgType's byte order, every message routes to the
    /// wrong handler while the length prefix still parses perfectly, which is a
    /// particularly confusing failure. Resolve it via the section 2 protocol-change
    /// process if the team wants both fields big-endian.
    /// </para>
    /// <para>
    /// Bodies are JSON rather than binary because MSP frequency is low (a few packets per
    /// minute), so the overhead is irrelevant, and being readable in Wireshark and logs is
    /// worth far more.
    /// </para>
    /// </remarks>
    public static class MspFrame
    {
        /// <summary>The u32 length prefix.</summary>
        public const int LengthPrefixSize = 4;

        /// <summary>The u16 msgType that the length prefix counts.</summary>
        public const int MsgTypeSize = 2;

        /// <summary>Smallest legal frame: prefix + msgType, with an empty body.</summary>
        public const int MinFrameSize = LengthPrefixSize + MsgTypeSize;

        /// <summary>Total bytes on the wire for a body of this size.</summary>
        public static int FrameSizeFor(int bodyLength) => MinFrameSize + bodyLength;

        /// <summary>
        /// Writes a complete frame. Returns bytes written, or -1 when the buffer is too
        /// small or the body exceeds
        /// <see cref="ProtocolConstants.MSP_MAX_FRAME_LENGTH"/>.
        /// </summary>
        public static int Write(Span<byte> dst, MspMessageType msgType, ReadOnlySpan<byte> body)
        {
            long declaredLength = MsgTypeSize + (long)body.Length;
            if (declaredLength > ProtocolConstants.MSP_MAX_FRAME_LENGTH) return -1;

            int total = FrameSizeFor(body.Length);
            if (dst.Length < total) return -1;

            Endian.WriteU32BE(dst, 0, (uint)declaredLength);
            Endian.WriteU16LE(dst, LengthPrefixSize, (ushort)msgType);
            body.CopyTo(dst.Slice(MinFrameSize, body.Length));
            return total;
        }
    }

    /// <summary>Outcome of a <see cref="MspFrameReader.TryReadFrame"/> call.</summary>
    public enum MspReadResult
    {
        /// <summary>Not enough bytes buffered yet — call again after the next receive.</summary>
        NeedMoreData = 0,
        /// <summary>A complete frame was produced.</summary>
        Frame = 1,
        /// <summary>
        /// The declared length exceeds
        /// <see cref="ProtocolConstants.MSP_MAX_FRAME_LENGTH"/>. The caller MUST close the
        /// connection; the reader is latched and will not produce another frame.
        /// </summary>
        FrameTooLarge = 2,
    }

    /// <summary>
    /// Accumulating-buffer frame parser for a TCP byte stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TCP is a byte stream with no message boundaries. A single <c>Receive()</c> can
    /// return half a message, or three messages stuck together — this is the number-one
    /// mistake made by people new to TCP, and the reason an accumulating buffer is
    /// mandatory rather than optional. Feed everything you receive to
    /// <see cref="Append"/>, then drain with <see cref="TryReadFrame"/> in a loop until it
    /// returns <see cref="MspReadResult.NeedMoreData"/>.
    /// </para>
    /// <para>
    /// The 64 KB cap is a memory-exhaustion defense: without it a peer sends
    /// <c>length = 0xFFFFFFFF</c> and the server dutifully allocates 4 GB waiting for a
    /// body that will never arrive.
    /// </para>
    /// <para>
    /// One instance per connection. Not thread-safe — in Dev D's design all logic runs on
    /// a single thread, so no locking is needed.
    /// </para>
    /// <para>
    /// conventions.md section 3.4 lists this class as one of the two things the team
    /// writes by hand despite the BCL already solving it
    /// (<c>System.IO.Pipelines</c>) — write it, benchmark against the library, and put the
    /// comparison in the report.
    /// </para>
    /// </remarks>
    public sealed class MspFrameReader
    {
        private byte[] _buffer;
        private int _length;      // bytes currently buffered
        private int _consumed;    // bytes already handed out, pending compaction
        private bool _faulted;

        private readonly int _maxFrameLength;

        public MspFrameReader(
            int initialCapacity = 4096,
            int maxFrameLength = ProtocolConstants.MSP_MAX_FRAME_LENGTH)
        {
            _buffer         = new byte[initialCapacity < MspFrame.MinFrameSize
                                        ? MspFrame.MinFrameSize
                                        : initialCapacity];
            _maxFrameLength = maxFrameLength;
        }

        /// <summary>True once an over-long frame was seen. The connection must be closed.</summary>
        public bool IsFaulted => _faulted;

        /// <summary>Bytes buffered but not yet parsed into a frame.</summary>
        public int BufferedBytes => _length - _consumed;

        /// <summary>Appends freshly received bytes.</summary>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (_faulted || data.Length == 0) return;

            Compact();
            EnsureCapacity(_length + data.Length);
            data.CopyTo(_buffer.AsSpan(_length, data.Length));
            _length += data.Length;
        }

        /// <summary>
        /// Attempts to pull one complete frame out of the buffer.
        /// </summary>
        /// <param name="msgType">The frame's message type, when the result is
        /// <see cref="MspReadResult.Frame"/>.</param>
        /// <param name="body">
        /// The frame body. Points into the reader's internal buffer and is only valid
        /// until the next <see cref="Append"/> or <see cref="TryReadFrame"/> call — copy it
        /// if you need to keep it.
        /// </param>
        public MspReadResult TryReadFrame(out MspMessageType msgType, out ReadOnlySpan<byte> body)
        {
            msgType = default;
            body    = ReadOnlySpan<byte>.Empty;

            if (_faulted) return MspReadResult.FrameTooLarge;

            int available = _length - _consumed;
            if (available < MspFrame.LengthPrefixSize) return MspReadResult.NeedMoreData;

            uint declaredLength = Endian.ReadU32BE(_buffer.AsSpan(_consumed), 0);

            // Check the cap BEFORE waiting for the bytes — otherwise we would buffer
            // toward a length we already know we will reject.
            if (declaredLength > _maxFrameLength || declaredLength < MspFrame.MsgTypeSize)
            {
                _faulted = true;
                return MspReadResult.FrameTooLarge;
            }

            int totalFrameSize = MspFrame.LengthPrefixSize + (int)declaredLength;
            if (available < totalFrameSize) return MspReadResult.NeedMoreData;

            int msgTypeOffset = _consumed + MspFrame.LengthPrefixSize;
            msgType = (MspMessageType)Endian.ReadU16LE(_buffer.AsSpan(msgTypeOffset), 0);

            int bodyOffset = msgTypeOffset + MspFrame.MsgTypeSize;
            int bodyLength = (int)declaredLength - MspFrame.MsgTypeSize;
            body = _buffer.AsSpan(bodyOffset, bodyLength);

            _consumed += totalFrameSize;
            return MspReadResult.Frame;
        }

        public void Reset()
        {
            _length   = 0;
            _consumed = 0;
            _faulted  = false;
        }

        private void Compact()
        {
            if (_consumed == 0) return;

            int remaining = _length - _consumed;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, _consumed, _buffer, 0, remaining);

            _length   = remaining;
            _consumed = 0;
        }

        private void EnsureCapacity(int required)
        {
            if (_buffer.Length >= required) return;

            int capacity = _buffer.Length;
            while (capacity < required) capacity *= 2;

            var grown = new byte[capacity];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }
    }
}
