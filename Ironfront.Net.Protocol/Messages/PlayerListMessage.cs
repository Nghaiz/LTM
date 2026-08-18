using System;
using System.Text;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// One row of <c>S_PLAYER_LIST</c>: which actor id a display name belongs to.
    /// </summary>
    /// <remarks>
    /// Mutable and parse-in-place, like <see cref="ActorSnapshotEntry"/>. The name is a slice
    /// of the caller's receive buffer, not a <c>string</c> — decoding to a string here would
    /// allocate one per player per broadcast, and the only consumer that needs a string is the
    /// UI, which can pay for it once.
    /// </remarks>
    public struct PlayerListEntry
    {
        /// <summary>
        /// A <c>u8</c>, not the <c>u16</c> every other message uses.
        /// </summary>
        /// <remarks>
        /// Safe because actorIds are allocated from <c>0..MAX_ACTORS - 1</c> (protocol-spec.md
        /// section 4.3.1) and <see cref="ProtocolConstants.MAX_ACTORS"/> is 64. It is pinned by
        /// a test rather than left to a comment, because raising MAX_ACTORS past 256 would
        /// truncate ids here silently and the symptom would be a scoreboard naming the wrong
        /// player.
        /// </remarks>
        public byte ActorId;

        /// <summary>UTF-8 display name, at most <see cref="PlayerListMessage.MaxNameBytes"/>.</summary>
        public ReadOnlyMemory<byte> Name;
    }

    /// <summary>
    /// <c>S_PLAYER_LIST</c> (0x4B) body codec, channel 2. protocol-spec.md section 4.11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The opcode was declared at the freeze and had no message type, no writer and no router
    /// case anywhere — so a killfeed line knew an actor id had died and had nothing to render.
    /// Sent on join and on change, not per tick: names do not move.
    /// </para>
    /// <para>
    /// <b>Names only, no scores.</b> Score and match time already travel in
    /// <see cref="MatchStateMessage"/>, and duplicating them here would be a second source of
    /// truth for a number that changes far more often than a name does.
    /// </para>
    /// </remarks>
    public static class PlayerListMessage
    {
        /// <summary>u8 playerCount, before any row.</summary>
        public const int HeaderSize = 1;

        /// <summary>
        /// Longest UTF-8 name the wire carries. Matches the 16-character upper bound MSP
        /// enforces on a username (<see cref="ErrorCode.InvalidUsername"/>).
        /// </summary>
        public const int MaxNameBytes = 16;

        /// <summary>actorId + nameLength, before the name bytes.</summary>
        public const int EntryHeaderSize = 2;

        /// <summary>
        /// Worst case: every actor named at the full length. 1 + 64 x 18 = 1153, which still
        /// fits one un-fragmented channel-2 payload.
        /// </summary>
        public const int MaxBodySize =
            HeaderSize + ProtocolConstants.MAX_ACTORS * (EntryHeaderSize + MaxNameBytes);

        /// <summary>Encoded size of a player list with these entries.</summary>
        public static int SizeFor(ReadOnlySpan<PlayerListEntry> entries)
        {
            int size = HeaderSize;
            for (int i = 0; i < entries.Length; i++)
                size += EntryHeaderSize + entries[i].Name.Length;
            return size;
        }

        /// <summary>
        /// Writes the message body. Returns bytes written, or -1.
        /// </summary>
        /// <remarks>
        /// A name longer than <see cref="MaxNameBytes"/> is refused rather than truncated:
        /// cutting UTF-8 at a fixed byte count splits multi-byte code points and produces a
        /// name that renders as replacement characters. The caller clips at a character
        /// boundary, where it still knows what the characters are.
        /// </remarks>
        public static int Write(Span<byte> dst, ReadOnlySpan<PlayerListEntry> entries)
        {
            if (entries.Length > byte.MaxValue) return -1;

            var w = new SpanWriter(dst);
            w.WriteU8((byte)entries.Length);

            for (int i = 0; i < entries.Length; i++)
            {
                ReadOnlySpan<byte> name = entries[i].Name.Span;
                if (name.Length > MaxNameBytes) return -1;

                w.WriteU8(entries[i].ActorId);
                w.WriteU8((byte)name.Length);
                w.WriteBytes(name);
            }

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a player list body. <paramref name="entries"/> must have room for the encoded
        /// count — size it to <see cref="ProtocolConstants.MAX_ACTORS"/> and reuse it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The parsed names point into <paramref name="src"/>, so they are only valid while
        /// that buffer is. A caller that keeps them past the current message copies first.
        /// </para>
        /// <para>
        /// <b>On failure, <paramref name="entries"/> has already been partially overwritten</b> —
        /// the same contract every parse-in-place codec here has. Treat the buffer as undefined
        /// unless this returned <c>true</c>.
        /// </para>
        /// </remarks>
        public static bool TryParse(
            byte[] src, int offset, int length,
            Span<PlayerListEntry> entries,
            out int entryCount)
        {
            entryCount = 0;
            if (src == null) return false;

            // Written as a subtraction, not as `offset + length > src.Length`. That form is int
            // arithmetic and wraps: offset 2 with length int.MaxValue sums to -2147483647, passes
            // the guard, and then throws out of the Span constructor — a TryParse that throws, in
            // the one parser here doing its own offset maths, in a library whose IO layer exists
            // precisely so that a truncated packet is routine rather than exceptional.
            if (offset < 0 || length < 0) return false;
            if (offset > src.Length || length > src.Length - offset) return false;

            var r = new SpanReader(new ReadOnlySpan<byte>(src, offset, length));
            byte count = r.ReadU8();
            if (!r.Ok) return false;
            if (entries.Length < count) return false;

            for (int i = 0; i < count; i++)
            {
                byte actorId    = r.ReadU8();
                byte nameLength = r.ReadU8();
                if (!r.Ok) return false;
                if (nameLength > MaxNameBytes) return false;

                int nameStart = offset + r.Position;
                r.Skip(nameLength);
                if (!r.Ok) return false;

                entries[i] = new PlayerListEntry
                {
                    ActorId = actorId,
                    Name    = new ReadOnlyMemory<byte>(src, nameStart, nameLength),
                };
            }

            entryCount = count;
            return true;
        }

        /// <summary>
        /// Decodes one entry's name. Allocates — call it when a name actually reaches the UI,
        /// not while parsing.
        /// </summary>
        public static string NameOf(in PlayerListEntry entry)
        {
            ReadOnlyMemory<byte> name = entry.Name;
            if (name.Length == 0) return string.Empty;

            ArraySegment<byte> segment;
            return System.Runtime.InteropServices.MemoryMarshal.TryGetArray(name, out segment)
                       && segment.Array != null
                ? Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count)
                : Encoding.UTF8.GetString(name.ToArray());
        }
    }
}
