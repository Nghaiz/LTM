using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// The wire version, and the layouts of both per-player table opcodes — <c>S_PLAYER_LIST</c>
    /// (0x4B) and <c>S_PLAYER_SCORES</c> (0x51) — pinned together, so an edit that moves one
    /// cannot land quietly. debt-closure phase 2 task 2a criterion 2; extended by P18 criterion 9.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It started as 0x4B's alone.</b> Per P-D8 that opcode was already reserved, so wiring a
    /// caller to it was not a protocol change: V3 shipped the struct, the codec, the router case
    /// and § 4.11, and phase 2 added only the server-side caller and the client-side subscriber.
    /// Nothing about the encoding moved, so the version stayed at 3 that day.
    /// </para>
    /// <para>
    /// <b>P18 extended this file rather than writing a rival.</b> 0x51 shares 0x4B's actor-id
    /// width, its <c>MAX_ACTORS</c> ceiling and the one version constant asserted below — three
    /// facts, and a second file pinning them would be a second place to update and a first place
    /// to forget.
    /// </para>
    /// <para>
    /// <b>The version assertion alone would prove nothing.</b> "The number is still N" is true on
    /// every build where somebody changed a layout and forgot — which is precisely the failure
    /// worth catching. So the layout constants are pinned BY NAME beside it: change
    /// <see cref="PlayerListMessage.HeaderSize"/>, <see cref="PlayerListMessage.EntryHeaderSize"/>,
    /// <see cref="PlayerListMessage.MaxNameBytes"/> or <see cref="PlayerScoresMessage.EntrySize"/>
    /// and this file goes red pointing at the version, which is the decision the change owes.
    /// </para>
    /// <para>
    /// <b>Do not re-pin this to whatever a failing run reported.</b> A red here means one of two
    /// things and they need opposite responses: a layout moved deliberately, in which case bump
    /// <c>PROTOCOL_VERSION</c>, write the § 15 and § 4.11/§ 4.13 rows, and update these constants
    /// together — or it moved accidentally, in which case revert it. Editing the constants to
    /// match the code and leaving the version alone converts a protocol break into a silent one.
    /// </para>
    /// </remarks>
    public sealed class PlayerListVersionPinTests
    {
        [Fact]
        public void ProtocolVersionIsWhereTheChangelogSaysItIs()
        {
            Assert.Equal(12, ProtocolConstants.PROTOCOL_VERSION);
        }

        [Fact]
        public void PlayerListLayoutIsUnchanged()
        {
            // u8 playerCount.
            Assert.Equal(1, PlayerListMessage.HeaderSize);

            // u8 actorId + u8 nameLength, per row.
            Assert.Equal(2, PlayerListMessage.EntryHeaderSize);

            // The 16-character bound MSP already enforces on a username.
            Assert.Equal(16, PlayerListMessage.MaxNameBytes);

            // A row per connected human, 64 at most (2026-09-30): bounded by connections, not by
            // MAX_ACTORS, because the list names people and never bots.
            Assert.Equal(64, PlayerListMessage.MaxEntries);

            // Derived rather than restated: a hand-written 1153 here would go on passing after
            // the bound moved, and the symptom would be a truncated broadcast on a full server.
            Assert.Equal(
                PlayerListMessage.HeaderSize
                    + PlayerListMessage.MaxEntries
                        * (PlayerListMessage.EntryHeaderSize + PlayerListMessage.MaxNameBytes),
                PlayerListMessage.MaxBodySize);

            Assert.Equal(0x4B, (byte)ServerMessageType.PlayerList);
        }

        /// <summary>
        /// P18's <c>S_PLAYER_SCORES</c> layout, pinned beside its sibling for the same reason.
        /// </summary>
        /// <remarks>
        /// <b>Here rather than in a rival test file</b>, per P18 criterion 9. The two messages
        /// share an actor-id width, a <c>MAX_ACTORS</c> ceiling and the version this file
        /// asserts; two files pinning them would be two places to update and one place to
        /// forget. The same do-not-re-pin rule above applies to every constant below.
        /// </remarks>
        [Fact]
        public void PlayerScoresLayoutIsUnchanged()
        {
            // u8 playerCount.
            Assert.Equal(1, PlayerScoresMessage.HeaderSize);

            // u8 actorId + u16 kills + u16 deaths + u8 team.
            Assert.Equal(6, PlayerScoresMessage.EntrySize);

            // The stats tail (2026-09-30): a version byte after the rows, then per row u8 status,
            // u8 headshots, u8 streak, u8 best, u16 points, u16 ping.
            Assert.Equal(1, PlayerScoresMessage.StatsTailVersion);
            Assert.Equal(8, PlayerScoresMessage.StatsEntrySize);

            // The page tail (2026-09-30): after the stats tail, u8 version, u8 page, u8 pages, on
            // a table longer than one page of 64 rows. A table that fits one page has none.
            Assert.Equal(64, PlayerScoresMessage.RowsPerPage);
            Assert.Equal(1, PlayerScoresMessage.PageTailVersion);
            Assert.Equal(3, PlayerScoresMessage.PageTailSize);

            // Derived rather than restated, for MaxBodySize's reason one test up: one full page
            // with both tails, 901, whatever MAX_ACTORS is.
            Assert.Equal(
                PlayerScoresMessage.HeaderSize
                    + PlayerScoresMessage.RowsPerPage * PlayerScoresMessage.EntrySize
                    + 1 + PlayerScoresMessage.RowsPerPage * PlayerScoresMessage.StatsEntrySize
                    + PlayerScoresMessage.PageTailSize,
                PlayerScoresMessage.MaxBodySize);

            Assert.Equal(0x51, (byte)ServerMessageType.PlayerScores);
        }

        [Fact]
        public void AnActorIdStillFitsTheRowsSingleByte()
        {
            // PlayerListEntry.ActorId and PlayerScoreEntry.ActorId are both u8 where every other
            // message uses a u16, which is safe only while MAX_ACTORS stays under 256. Raising it
            // past that truncates ids in BOTH SILENTLY, and the symptom is a scoreboard naming
            // and crediting the wrong player — so the ceiling is asserted rather than left to the
            // remarks on the fields. One assertion covering both, per P18 criterion 9: the bound
            // is one fact and a second copy of it is a second thing to forget.
            Assert.True(
                ProtocolConstants.MAX_ACTORS <= 256,
                $"MAX_ACTORS is {ProtocolConstants.MAX_ACTORS}. PlayerListEntry.ActorId and "
                + "PlayerScoreEntry.ActorId are u8 and would now truncate. Widen both fields, "
                + "bump PROTOCOL_VERSION, and rewrite protocol-spec.md §§ 4.11 and 4.13 — do not "
                + "relax this bound.");
        }
    }
}
