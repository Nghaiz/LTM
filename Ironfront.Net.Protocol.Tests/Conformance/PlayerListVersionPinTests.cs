using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// Giving <c>S_PLAYER_LIST</c> a sender did not change a byte on the wire, and this is what
    /// makes a future edit that DOES change one impossible to land quietly.
    /// debt-closure phase 2 task 2a, acceptance criterion 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per P-D8 the opcode was already reserved, so wiring a caller to it is not a protocol
    /// change.</b> V3 shipped the struct, the codec, the router case and § 4.11; phase 2 adds the
    /// server-side caller and the client-side subscriber the table never had. Nothing about the
    /// encoding moves, so <see cref="ProtocolConstants.PROTOCOL_VERSION"/> stays at 3.
    /// </para>
    /// <para>
    /// <b>The version assertion alone would prove nothing.</b> "The number is still 3" is true on
    /// every build where somebody changed the layout and forgot — which is precisely the failure
    /// worth catching. So the layout constants are pinned BY NAME beside it: change
    /// <see cref="PlayerListMessage.HeaderSize"/>, <see cref="PlayerListMessage.EntryHeaderSize"/>
    /// or <see cref="PlayerListMessage.MaxNameBytes"/> and this test goes red pointing at the
    /// version, which is the decision the change actually owes.
    /// </para>
    /// <para>
    /// <b>Do not re-pin this to whatever a failing run reported.</b> A red here means one of two
    /// things and they need opposite responses: the layout moved deliberately, in which case
    /// bump <c>PROTOCOL_VERSION</c>, write the § 5 and § 4.11 rows, and update these constants
    /// together — or it moved accidentally, in which case revert it. Editing the constants to
    /// match the code and leaving the version alone converts a protocol break into a silent one.
    /// </para>
    /// </remarks>
    public sealed class PlayerListVersionPinTests
    {
        [Fact]
        public void GivingPlayerListASenderDidNotMoveTheProtocolVersion()
        {
            Assert.Equal(6, ProtocolConstants.PROTOCOL_VERSION);   // 3 -> 4 in X-53: Quantize's position WINDOW moved (-1024..3072), so the same i16 decodes to a different metre. Same bytes, different meaning -- exactly what the version is for. 4 -> 5 in P11: S_MATCH_STATE grew victoryPoints (Size 8 -> 10) AND tickets0/1 became ascending score0/1 at the same offsets -- again same bytes, different meaning. 5 -> 6 in P13: the joinTicket gained a u8 team at offset 16 and displayName shrank 16 -> 15 to pay for it, so every byte from 16 on MOVED -- a layout change, not a reinterpretation. None of the three bumps touched the layout this test pins, which is why the layout constants beside it did not move.
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

            // Derived rather than restated: a hand-written 1153 here would go on passing after
            // MAX_ACTORS moved, and the symptom would be a truncated broadcast on a full server.
            Assert.Equal(
                PlayerListMessage.HeaderSize
                    + ProtocolConstants.MAX_ACTORS
                        * (PlayerListMessage.EntryHeaderSize + PlayerListMessage.MaxNameBytes),
                PlayerListMessage.MaxBodySize);

            Assert.Equal(0x4B, (byte)ServerMessageType.PlayerList);
        }

        [Fact]
        public void AnActorIdStillFitsTheRowsSingleByte()
        {
            // PlayerListEntry.ActorId is a u8 where every other message uses a u16, which is safe
            // only while MAX_ACTORS stays under 256. Raising it past that truncates ids here
            // SILENTLY, and the symptom is a scoreboard naming the wrong player — so the ceiling
            // is asserted rather than left to the remark on the field.
            Assert.True(
                ProtocolConstants.MAX_ACTORS <= 256,
                $"MAX_ACTORS is {ProtocolConstants.MAX_ACTORS}. PlayerListEntry.ActorId is a u8 "
                + "and would now truncate. Widen the field, bump PROTOCOL_VERSION, and rewrite "
                + "protocol-spec.md § 4.11 — do not relax this bound.");
        }
    }
}
