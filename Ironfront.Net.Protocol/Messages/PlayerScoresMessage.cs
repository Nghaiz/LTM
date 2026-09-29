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
        /// Worst case: every actor scored, with the stats tail. 1 + 64 x 6 + 1 + 64 x 8 = 898,
        /// inside one un-fragmented channel-2 payload (1181).
        /// </summary>
        public const int MaxBodySize =
            HeaderSize + ProtocolConstants.MAX_ACTORS * EntrySize + 1 + ProtocolConstants.MAX_ACTORS * StatsEntrySize;

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
        {
            if (entries.Length > byte.MaxValue) return -1;

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
        {
            entryCount = 0;

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

            return true;
        }
    }
}
