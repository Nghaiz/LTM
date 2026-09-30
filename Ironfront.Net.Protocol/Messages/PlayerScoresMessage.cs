using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// One row of <c>S_PLAYER_SCORES</c>: what an actor has killed and how often it has died.
    /// </summary>
    /// <remarks>
    /// A plain value, unlike <see cref="PlayerListEntry"/>, because there is nothing here that
    /// points into the receive buffer — four small fields copy as cheaply as a slice does. So a
    /// caller that keeps a row past the callback needs no copy step, which is the one way this
    /// message is easier to hold than the name table beside it.
    /// </remarks>
    public struct PlayerScoreEntry
    {
        /// <summary>
        /// A <c>u8</c>, the same width and for the same reason as
        /// <see cref="PlayerListEntry.ActorId"/>.
        /// </summary>
        /// <remarks>
        /// Actor ids are allocated from <c>0..MAX_ACTORS - 1</c> (protocol-spec.md § 4.3.1) and
        /// <see cref="ProtocolConstants.MAX_ACTORS"/> is 64. Pinned by
        /// <c>PlayerListVersionPinTests</c>, which was extended to cover this opcode rather than
        /// copied — raising MAX_ACTORS past 256 truncates the id here silently and the symptom
        /// is a scoreboard crediting the wrong player.
        /// </remarks>
        public byte ActorId;

        /// <summary>Kills credited to this actor this match.</summary>
        public ushort Kills;

        /// <summary>Deaths recorded against this actor this match.</summary>
        public ushort Deaths;

        /// <summary>
        /// Which side this actor is on, or <see cref="TeamId.None"/> when the server has none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>On this message because the snapshot cannot answer for the whole roster.</b> The
        /// actor entry already carries a team (§ 4.3) and that is where a client reads one — but
        /// <c>InterestManager</c> emits actors in relevance buckets under a per-snapshot ceiling
        /// with a shed cursor, so a client holds teams only for the actors it currently sees. On
        /// a 41-bot map that is a small minority, and a scoreboard columned from the snapshot
        /// would put most of the roster on no side at all. This field is that same authoritative
        /// team for the actors the scoreboard has to place, which is all of them.
        /// </para>
        /// <para>
        /// <b>Not the second-source-of-truth § 4.11 refuses.</b> That objection is about the TEAM
        /// SCORE, which <c>S_MATCH_STATE</c> owns and which changes many times a match; this is a
        /// per-actor assignment that changes at most once a life, and both copies are written
        /// from the server's one answer in the same tick loop. What the spec forbids is two
        /// places to LOOK for one moving number, not one fact reaching two audiences.
        /// </para>
        /// <para>
        /// <b>This is a deliberate departure from P18 § 1.3's stated row</b>, which listed
        /// <c>actorId</c>, <c>kills</c> and <c>deaths</c> only and said teams would come from the
        /// snapshot. The interest filter is why they cannot. The cost is one byte per row: worst
        /// case 1 + 64 x 6 = 385 B, against the same 1181-byte budget § 1.2's table is drawn on.
        /// </para>
        /// </remarks>
        public byte Team;

        // ---- the stats tail (2026-09-30, the owner's Tab-board report) ----

        /// <summary>Whether the stats tail was sent for this row. A server before 2026-09-30 never sends it.</summary>
        public bool HasStats;

        /// <summary>Alive, and in a vehicle seat, as the server holds the actor now.</summary>
        public PlayerStatusFlags Status;

        /// <summary>Enemy kills this match whose killing blow was to the head. Clamped at 255.</summary>
        public byte Headshots;

        /// <summary>Enemy kills since this actor last died. Clamped at 255.</summary>
        public byte Streak;

        /// <summary>The longest <see cref="Streak"/> this match. Clamped at 255.</summary>
        public byte BestStreak;

        /// <summary>
        /// The points this actor's kills put on its side's score this match: each enemy kill is
        /// worth what the match awarded for it, one per flag the side held.
        /// </summary>
        public ushort Points;

        /// <summary>A connected human's smoothed round trip in milliseconds; 0 for a bot or unknown.</summary>
        public ushort PingMs;
    }

    /// <summary>
    /// <c>S_PLAYER_SCORES</c> (0x51) body codec, channel 2. protocol-spec.md § 4.13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A new opcode rather than a wider <c>S_PLAYER_LIST</c>, and the arithmetic is why.</b>
    /// 0x4B's worst case is <c>1 + 64 x 18 = 1153 B</c> against a
    /// <c>MAX_CHANNEL_PAYLOAD</c> of 1181 — 28 bytes of headroom in total. One extra <c>u8</c>
    /// per row costs 64 and already overflows; a <c>u16</c> pair costs 128. Extending 0x4B in
    /// place would have traded the un-fragmented guarantee for a scoreboard, on the map with the
    /// most players, which is exactly when it matters. Phase P18 § 1.2.
    /// </para>
    /// <para>
    /// <b>It is also a different cadence.</b> § 4.11 sends names on join and on change because
    /// names do not move; these numbers move on every death. Two messages let each keep its own
    /// send rule, which is the other half of why a wider entry was the wrong shape.
    /// </para>
    /// <para>
    /// <b><c>u16</c> counters, not <c>u8</c>.</b> A bot on a 40-bot map over a long session
    /// passes 255 deaths, and a wrapped counter renders as a plausible small number rather than
    /// as an error — the failure mode that reads as working. Two bytes per row buys a counter
    /// that cannot lie, at 128 B in a body whose worst case is 385.
    /// </para>
    /// <para>
    /// <b>This does not duplicate the team score.</b> That travels in
    /// <see cref="MatchStateMessage"/> and stays there; per-player kills and deaths travelled
    /// nowhere before this message existed, so it adds a number with no second source.
    /// </para>
    /// </remarks>
    public static class PlayerScoresMessage
    {
        /// <summary>u8 playerCount, before any row.</summary>
        public const int HeaderSize = 1;

        /// <summary>u8 actorId + u16 kills + u16 deaths + u8 team.</summary>
        public const int EntrySize = 6;

        /// <summary>The stats tail's version byte. A tail with any other version is left unread.</summary>
        public const byte StatsTailVersion = 1;

        /// <summary>
        /// One row of the stats tail, in the rows' order: u8 status + u8 headshots + u8 streak +
        /// u8 best streak + u16 points + u16 ping.
        /// </summary>
        public const int StatsEntrySize = 8;

        /// <summary>
        /// The most rows one message carries. A longer table goes in pages of this many
        /// (2026-09-30, ahead of rooms of more than 64 actors).
        /// </summary>
        /// <remarks>
        /// At 14 B a row with the stats tail, a table of 88 rows no longer fits one un-fragmented
        /// channel-2 payload, and the server logged "did not frame" and sent nothing every time
        /// 100 bots and 14 players were scored (P29 capacity bench). Pages keep the guarantee this
        /// message was built on at any roster size instead of trading it for fragments at the
        /// cadence of every death. 64 is today's whole table, so a room of up to 64 actors still
        /// sends exactly the bytes it always has.
        /// </remarks>
        public const int RowsPerPage = 64;

        /// <summary>The page tail's version byte. A tail with any other version is left unread.</summary>
        public const byte PageTailVersion = 1;

        /// <summary>u8 version + u8 page index + u8 page count, after the stats tail.</summary>
        public const int PageTailSize = 3;

        /// <summary>
        /// Worst case of one message: a full page with the stats tail and the page tail.
        /// 1 + 64 x 6 + 1 + 64 x 8 + 3 = 901, inside one un-fragmented channel-2 payload (1181)
        /// whatever <see cref="ProtocolConstants.MAX_ACTORS"/> is.
        /// </summary>
        public const int MaxBodySize =
            HeaderSize + RowsPerPage * EntrySize + 1 + RowsPerPage * StatsEntrySize + PageTailSize;

        /// <summary>How many messages a table of this many rows takes: one, up to <see cref="RowsPerPage"/>.</summary>
        public static int PageCountFor(int rowCount)
            => rowCount <= RowsPerPage ? 1 : (rowCount + RowsPerPage - 1) / RowsPerPage;

        /// <summary>Encoded size of a score table with this many entries.</summary>
        /// <remarks>
        /// A count rather than the rows, unlike <see cref="PlayerListMessage.SizeFor"/>: every
        /// row here is the same width, so the rows themselves would tell it nothing the length
        /// does not.
        /// </remarks>
        public static int SizeFor(int entryCount) => HeaderSize + entryCount * EntrySize;

        /// <summary>Encoded size of a score table with this many entries and the stats tail.</summary>
        public static int SizeWithStatsFor(int entryCount)
            => SizeFor(entryCount) + 1 + entryCount * StatsEntrySize;

        /// <summary>Writes the message body. Returns bytes written, or -1.</summary>
        public static int Write(Span<byte> dst, ReadOnlySpan<PlayerScoreEntry> entries)
            => Write(dst, entries, includeStats: false);

        /// <summary>
        /// Writes the message body, with the stats tail when <paramref name="includeStats"/>.
        /// Returns bytes written, or -1.
        /// </summary>
        /// <remarks>
        /// <b>The tail comes after every row, not inside each.</b> A client from before it reads
        /// the count and the rows and stops, so the tail is compatible without a
        /// <c>PROTOCOL_VERSION</c> bump -- the same argument as <c>S_DEATH</c>'s detail tail.
        /// </remarks>
        public static int Write(Span<byte> dst, ReadOnlySpan<PlayerScoreEntry> entries, bool includeStats)
            => Write(dst, entries, includeStats, pageIndex: 0, pageCount: 1);

        /// <summary>
        /// Writes one page of a table: <paramref name="entries"/> are that page's rows. Returns
        /// bytes written, or -1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A table of one page carries no page tail</b>, so it is byte for byte what a server
        /// before pages sent. A longer table sends <paramref name="pageCount"/> messages in order,
        /// each with the stats tail and then <c>u8 version, u8 pageIndex, u8 pageCount</c>; a
        /// client puts the rows together and shows the table once the last page is in.
        /// </para>
        /// <para>
        /// <b>Pages ride after the stats tail, so a paged table always carries it</b>: the byte
        /// after the rows is the stats tail's version, and a page tail in its place would read as
        /// one.
        /// </para>
        /// </remarks>
        public static int Write(
            Span<byte> dst, ReadOnlySpan<PlayerScoreEntry> entries, bool includeStats, int pageIndex, int pageCount)
        {
            if (entries.Length > byte.MaxValue) return -1;
            if (pageCount < 1 || pageCount > byte.MaxValue || pageIndex < 0 || pageIndex >= pageCount) return -1;
            bool paged = pageCount > 1;
            if (paged && (!includeStats || entries.Length > RowsPerPage)) return -1;

            var w = new SpanWriter(dst);
            w.WriteU8((byte)entries.Length);

            for (int i = 0; i < entries.Length; i++)
            {
                w.WriteU8(entries[i].ActorId);
                w.WriteU16(entries[i].Kills);
                w.WriteU16(entries[i].Deaths);
                w.WriteU8(entries[i].Team);
            }

            if (includeStats)
            {
                w.WriteU8(StatsTailVersion);

                for (int i = 0; i < entries.Length; i++)
                {
                    w.WriteU8((byte)entries[i].Status);
                    w.WriteU8(entries[i].Headshots);
                    w.WriteU8(entries[i].Streak);
                    w.WriteU8(entries[i].BestStreak);
                    w.WriteU16(entries[i].Points);
                    w.WriteU16(entries[i].PingMs);
                }
            }

            if (paged)
            {
                w.WriteU8(PageTailVersion);
                w.WriteU8((byte)pageIndex);
                w.WriteU8((byte)pageCount);
            }

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a score table body. <paramref name="entries"/> must have room for the encoded
        /// count — size it to <see cref="ProtocolConstants.MAX_ACTORS"/> and reuse it.
        /// </summary>
        /// <remarks>
        /// <b>On failure, <paramref name="entries"/> has already been partially overwritten</b> —
        /// the same contract every parse-in-place codec here has. Treat the buffer as undefined
        /// unless this returned <c>true</c>.
        /// </remarks>
        public static bool TryParse(
            ReadOnlySpan<byte> src, Span<PlayerScoreEntry> entries, out int entryCount)
            => TryParse(src, entries, out entryCount, out _, out _);

        /// <summary>
        /// Parses one message of a score table and says which page of how many it is: 0 of 1
        /// for a table sent whole, which is every table of up to <see cref="RowsPerPage"/> rows.
        /// </summary>
        public static bool TryParse(
            ReadOnlySpan<byte> src, Span<PlayerScoreEntry> entries, out int entryCount,
            out int pageIndex, out int pageCount)
        {
            entryCount = 0;
            pageIndex = 0;
            pageCount = 1;

            var r = new SpanReader(src);
            byte count = r.ReadU8();
            if (!r.Ok) return false;
            if (entries.Length < count) return false;

            for (int i = 0; i < count; i++)
            {
                byte actorId  = r.ReadU8();
                ushort kills  = r.ReadU16();
                ushort deaths = r.ReadU16();
                byte team     = r.ReadU8();
                if (!r.Ok) return false;

                entries[i] = new PlayerScoreEntry
                {
                    ActorId = actorId,
                    Kills   = kills,
                    Deaths  = deaths,
                    Team    = team,
                };
            }

            entryCount = count;

            // The stats tail is optional: a server from before it sends the rows alone. A tail of
            // a version this build does not know is left unread rather than refused, so a later
            // server can change it without cutting this client off. Half a tail is malformed --
            // it would put one player's numbers on another's row.
            if (r.Remaining == 0) return true;

            byte version = r.ReadU8();
            if (!r.Ok) return false;
            if (version != StatsTailVersion) return true;

            for (int i = 0; i < count; i++)
            {
                byte status   = r.ReadU8();
                byte heads    = r.ReadU8();
                byte streak   = r.ReadU8();
                byte best     = r.ReadU8();
                ushort points = r.ReadU16();
                ushort ping   = r.ReadU16();
                if (!r.Ok) return false;

                entries[i].HasStats   = true;
                entries[i].Status     = (PlayerStatusFlags)status;
                entries[i].Headshots  = heads;
                entries[i].Streak     = streak;
                entries[i].BestStreak = best;
                entries[i].Points     = points;
                entries[i].PingMs     = ping;
            }

            // The page tail: absent on a table sent whole. A tail of a version this build does
            // not know is left unread, as the stats tail's is; a page that claims to be past the
            // end of its own table is malformed.
            if (r.Remaining == 0) return true;

            byte pageVersion = r.ReadU8();
            if (!r.Ok) return false;
            if (pageVersion != PageTailVersion) return true;

            byte index = r.ReadU8();
            byte total = r.ReadU8();
            if (!r.Ok || total == 0 || index >= total) return false;

            pageIndex = index;
            pageCount = total;
            return true;
        }
    }
}
