using System;
using System.Text;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// <c>C_CHAT</c> (0x24) and <c>S_CHAT</c> (0x47) body codecs, both channel 2.
    /// protocol-spec.md section 4.12. Phase P6 task 3.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both directions live in one class because they are one message with an attribution
    /// added.</b> The client sends text; the server decides who said it and re-broadcasts the
    /// same text with an actor id in front. Splitting them would leave
    /// <see cref="MaxTextBytes"/> defined twice, and a client that clips at a different length
    /// than the server accepts is a message silently refused at the far end.
    /// </para>
    /// <para>
    /// <b>The client never states who it is.</b> There is no <c>actorId</c> in the C_CHAT body
    /// and there must not be: the server already knows which session the datagram arrived on,
    /// and a self-declared id is a client asserting it is somebody else. This is the same
    /// reason <c>C_SEAT_REQUEST</c> carries no actor id.
    /// </para>
    /// <para>
    /// <b>The text is not sanitized here.</b> A codec's job is bytes; deciding what a label may
    /// be asked to render is <see cref="PlayerNameSanitizer"/>'s, and both ends run it at their
    /// own ingress exactly as they do for a display name. What this class does enforce is the
    /// length bound, because that is a wire fact rather than a rendering one.
    /// </para>
    /// </remarks>
    public static class ChatTextMessage
    {
        /// <summary>
        /// Longest UTF-8 chat line the wire carries.
        /// </summary>
        /// <remarks>
        /// One line, not a paragraph. 120 bytes is roughly 120 Latin characters or 40 of
        /// Vietnamese, and the worst-case S_CHAT body is <c>2 + 120 = 122 B</c> — far inside one
        /// un-fragmented channel-2 payload, so a chat message can never be the thing that
        /// fragments. Bytes rather than characters because the wire counts bytes; see
        /// <see cref="MaxTextCharacters"/> for the bound a sender clips against.
        /// </remarks>
        public const int MaxTextBytes = 120;

        /// <summary>
        /// The character bound a sender clips at, before encoding.
        /// </summary>
        /// <remarks>
        /// <b>Characters, and the wire limit is bytes</b> — the same relationship
        /// <see cref="PlayerNameSanitizer.MaxCharacters"/> has with
        /// <see cref="PlayerListMessage.MaxNameBytes"/>, and the same reasoning. Forty
        /// characters of Vietnamese or Cyrillic exceeds forty bytes, so this does NOT relieve
        /// <see cref="WriteClient"/> of its byte check; it exists so a sender clips at a
        /// character boundary, where it still knows what the characters are, rather than cutting
        /// UTF-8 mid-code-point. The two agree in the ASCII case and the byte limit binds
        /// otherwise, which is the safe direction.
        /// </remarks>
        public const int MaxTextCharacters = 60;

        /// <summary>u8 textLength, before the text bytes.</summary>
        public const int ClientHeaderSize = 1;

        /// <summary>u8 actorId + u8 textLength, before the text bytes.</summary>
        public const int ServerHeaderSize = 2;

        /// <summary>Worst-case C_CHAT body.</summary>
        public const int MaxClientBodySize = ClientHeaderSize + MaxTextBytes;

        /// <summary>Worst-case S_CHAT body.</summary>
        public const int MaxServerBodySize = ServerHeaderSize + MaxTextBytes;

        /// <summary>
        /// Writes a <c>C_CHAT</c> body. Returns bytes written, or -1.
        /// </summary>
        /// <remarks>
        /// Over-long text is <b>refused, not truncated</b>, for
        /// <see cref="PlayerListMessage.Write"/>'s reason: cutting UTF-8 at a fixed byte count
        /// splits multi-byte code points and renders as replacement characters. The caller clips
        /// at a character boundary — see <see cref="MaxTextCharacters"/>.
        /// </remarks>
        public static int WriteClient(Span<byte> dst, ReadOnlySpan<byte> textUtf8)
        {
            if (textUtf8.Length > MaxTextBytes) return -1;
            if (textUtf8.Length == 0) return -1;

            var w = new SpanWriter(dst);
            w.WriteU8((byte)textUtf8.Length);
            w.WriteBytes(textUtf8);

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Writes an <c>S_CHAT</c> body. Returns bytes written, or -1.
        /// </summary>
        /// <remarks>
        /// <paramref name="actorId"/> is a <c>u8</c>, not the <c>u16</c> most messages use —
        /// <see cref="PlayerListEntry.ActorId"/>'s reason exactly, pinned by the same
        /// conformance argument: ids are allocated from <c>0..MAX_ACTORS - 1</c> and
        /// <see cref="ProtocolConstants.MAX_ACTORS"/> is 64.
        /// </remarks>
        public static int WriteServer(Span<byte> dst, byte actorId, ReadOnlySpan<byte> textUtf8)
        {
            if (textUtf8.Length > MaxTextBytes) return -1;
            if (textUtf8.Length == 0) return -1;

            var w = new SpanWriter(dst);
            w.WriteU8(actorId);
            w.WriteU8((byte)textUtf8.Length);
            w.WriteBytes(textUtf8);

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a <c>C_CHAT</c> body. The text points into <paramref name="src"/>.
        /// </summary>
        /// <remarks>
        /// <b>An empty line is refused rather than parsed to nothing.</b> A client that sends
        /// zero bytes of text is asking the server to broadcast a blank row to every player,
        /// which costs a reliable send and renders as a rendering fault. Refusing it here makes
        /// it a malformed message at the router, where malformed messages are already counted.
        /// </remarks>
        public static bool TryParseClient(ReadOnlySpan<byte> src, out ReadOnlySpan<byte> textUtf8)
        {
            textUtf8 = default;

            if (src.Length < ClientHeaderSize) return false;

            int length = src[0];
            if (length == 0 || length > MaxTextBytes) return false;
            if (src.Length < ClientHeaderSize + length) return false;

            textUtf8 = src.Slice(ClientHeaderSize, length);
            return true;
        }

        /// <summary>
        /// Parses an <c>S_CHAT</c> body. The text points into <paramref name="src"/>.
        /// </summary>
        public static bool TryParseServer(
            ReadOnlySpan<byte> src, out byte actorId, out ReadOnlySpan<byte> textUtf8)
        {
            actorId  = 0;
            textUtf8 = default;

            if (src.Length < ServerHeaderSize) return false;

            actorId    = src[0];
            int length = src[1];
            if (length == 0 || length > MaxTextBytes) return false;
            if (src.Length < ServerHeaderSize + length) return false;

            textUtf8 = src.Slice(ServerHeaderSize, length);
            return true;
        }

        /// <summary>
        /// Encodes a clipped, already-sanitized line to UTF-8. Returns bytes written, or -1 when
        /// it does not fit <see cref="MaxTextBytes"/>.
        /// </summary>
        /// <remarks>
        /// Returns -1 rather than clipping bytes, so the caller learns its character clip was
        /// not enough instead of shipping a split code point. A caller in that position drops
        /// the line; it has no honest way to shorten text it has already decided is minimal.
        /// </remarks>
        public static int Encode(string text, Span<byte> dst)
        {
            if (string.IsNullOrEmpty(text)) return -1;

            int byteCount = Encoding.UTF8.GetByteCount(text);
            if (byteCount > MaxTextBytes || byteCount > dst.Length) return -1;

            return Encoding.UTF8.GetBytes(text.AsSpan(), dst);
        }

        /// <summary>
        /// Decodes a parsed line to a string. Allocates — call it when a line reaches the UI.
        /// </summary>
        public static string TextOf(ReadOnlySpan<byte> textUtf8) =>
            textUtf8.Length == 0 ? string.Empty : Encoding.UTF8.GetString(textUtf8);
    }
}
