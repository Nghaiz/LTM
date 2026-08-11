using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Layout constants for the message batch inside a PAYLOAD datagram.
    /// protocol-spec.md section 4.
    /// </summary>
    /// <remarks>
    /// <code>
    /// u8   channelId
    /// u16  messageCount
    /// repeat messageCount times:
    ///     u8   msgType
    ///     u16  msgLength    Body size in bytes
    ///     u8[] body
    /// </code>
    /// Batching several messages into one datagram is what keeps the 16-byte GSP header
    /// from dominating a stream of small events.
    /// </remarks>
    public static class PayloadFrame
    {
        /// <summary>channelId + messageCount = 3 bytes.</summary>
        public const int HeaderSize = 3;

        /// <summary>msgType + msgLength = 3 bytes per message.</summary>
        public const int MessageHeaderSize = 3;
    }

    /// <summary>
    /// Builds a message batch into a caller-supplied buffer. Allocation-free.
    /// </summary>
    public ref struct PayloadFrameWriter
    {
        private readonly Span<byte> _buffer;
        private int _position;
        private ushort _messageCount;
        private bool _ok;

        public PayloadFrameWriter(Span<byte> buffer, ChannelId channel)
        {
            _buffer       = buffer;
            _position     = 0;
            _messageCount = 0;
            _ok           = buffer.Length >= PayloadFrame.HeaderSize;

            if (_ok)
            {
                _buffer[0] = (byte)channel;
                // messageCount is patched in Finish(); leave a hole for now.
                Endian.WriteU16LE(_buffer, 1, 0);
                _position = PayloadFrame.HeaderSize;
            }
        }

        public bool Ok => _ok;
        public ushort MessageCount => _messageCount;
        public int Length => _position;
        public int Remaining => _ok ? _buffer.Length - _position : 0;

        /// <summary>
        /// Appends one message. The body must already be serialized. Returns false — and
        /// latches <see cref="Ok"/> false — when it does not fit, which is the caller's
        /// signal to flush this datagram and start another.
        /// </summary>
        public bool WriteMessage(byte msgType, ReadOnlySpan<byte> body)
        {
            if (!_ok) return false;

            if (body.Length > ushort.MaxValue) { _ok = false; return false; }

            int needed = PayloadFrame.MessageHeaderSize + body.Length;
            if (_position + needed > _buffer.Length) { _ok = false; return false; }

            _buffer[_position] = msgType;
            Endian.WriteU16LE(_buffer, _position + 1, (ushort)body.Length);
            body.CopyTo(_buffer.Slice(_position + PayloadFrame.MessageHeaderSize, body.Length));

            _position += needed;
            _messageCount++;
            return true;
        }

        public bool WriteMessage(ClientMessageType msgType, ReadOnlySpan<byte> body)
            => WriteMessage((byte)msgType, body);

        public bool WriteMessage(ServerMessageType msgType, ReadOnlySpan<byte> body)
            => WriteMessage((byte)msgType, body);

        /// <summary>
        /// Patches the messageCount field and returns the finished payload region.
        /// Must be called before the buffer is handed to the transport.
        /// </summary>
        public bool TryFinish(out int totalLength)
        {
            totalLength = 0;
            if (!_ok) return false;

            Endian.WriteU16LE(_buffer, 1, _messageCount);
            totalLength = _position;
            return true;
        }
    }

    /// <summary>
    /// Iterates the messages inside a received PAYLOAD region. Allocation-free; the
    /// bodies it yields point into the original receive buffer.
    /// </summary>
    public ref struct PayloadFrameReader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private int _position;
        private int _remainingMessages;
        private readonly bool _valid;
        private readonly ChannelId _channel;
        private readonly ushort _messageCount;

        public PayloadFrameReader(ReadOnlySpan<byte> payload)
        {
            _payload = payload;

            if (payload.Length < PayloadFrame.HeaderSize)
            {
                _valid             = false;
                _channel           = ChannelId.Unreliable;
                _messageCount      = 0;
                _remainingMessages = 0;
                _position          = 0;
                return;
            }

            _channel           = (ChannelId)payload[0];
            _messageCount      = Endian.ReadU16LE(payload, 1);
            _remainingMessages = _messageCount;
            _position          = PayloadFrame.HeaderSize;
            _valid             = true;
        }

        public bool IsValid => _valid;
        public ChannelId Channel => _channel;
        public ushort MessageCount => _messageCount;

        /// <summary>
        /// Reads the next message. Returns false at the end of the batch, and also on a
        /// truncated or over-long msgLength — a corrupt datagram simply stops the
        /// iteration rather than throwing (conventions.md section 3.2).
        /// </summary>
        public bool TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body)
        {
            msgType = 0;
            body    = ReadOnlySpan<byte>.Empty;

            if (!_valid || _remainingMessages <= 0) return false;
            if (_position + PayloadFrame.MessageHeaderSize > _payload.Length) return false;

            byte type = _payload[_position];
            ushort length = Endian.ReadU16LE(_payload, _position + 1);
            int bodyStart = _position + PayloadFrame.MessageHeaderSize;

            if (bodyStart + length > _payload.Length) return false;

            msgType   = type;
            body      = _payload.Slice(bodyStart, length);
            _position = bodyStart + length;
            _remainingMessages--;
            return true;
        }
    }
}
