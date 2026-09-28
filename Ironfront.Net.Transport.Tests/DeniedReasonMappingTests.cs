using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    /// <summary>
    /// P13 criterion 7: what a client does with a CONNECT_DENIED code it does not recognise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The branch nothing could reach.</b> No server in this repository sends a code the
    /// client does not know, so <c>MapDeniedReason</c>'s default arm had no caller and no
    /// test — and spent five protocol versions sending every unrecognised code to
    /// <see cref="DisconnectReason.InvalidTicket"/>. That is not a missing reason, it is a
    /// WRONG one: a player told their ticket is invalid re-logs in, and the real answer was
    /// that their side was full.
    /// </para>
    /// <para>
    /// <b>Why "an old client reading code 7" is the same thing as code 8.</b> A build that
    /// predates <see cref="ConnectDenyReason.TeamFull"/> has no arm for 7, so 7 lands in the
    /// default arm — the arm every code above the highest one it knows lands in. Testing an
    /// undefined code exercises that arm on today's build, which is the only build that can
    /// be tested; asserting it on a rebuilt v5 binary would test a binary nobody ships.
    /// </para>
    /// </remarks>
    public sealed class DeniedReasonMappingTests
    {
        [Theory]
        [InlineData(ConnectDenyReason.ServerFull, DisconnectReason.ServerFull)]
        [InlineData(ConnectDenyReason.ProtocolVersionMismatch, DisconnectReason.ProtocolMismatch)]
        [InlineData(ConnectDenyReason.InvalidTicket, DisconnectReason.InvalidTicket)]
        [InlineData(ConnectDenyReason.Banned, DisconnectReason.Banned)]
        [InlineData(ConnectDenyReason.AlreadyConnected, DisconnectReason.AlreadyConnected)]
        [InlineData(ConnectDenyReason.TeamFull, DisconnectReason.TeamFull)]
        public void EveryKnownCodeKeepsItsOwnMeaning(
            ConnectDenyReason code, DisconnectReason expected)
        {
            Assert.Equal(expected, Connection.MapDeniedReason(code));
        }

        [Fact]
        public void ServerFullAndTeamFullDoNotCollapseIntoOneReason()
        {
            // The distinction the whole of step 3.3 exists to preserve. "The server is full"
            // has no remedy; "your side is full" has one the player can act on.
            Assert.NotEqual(
                Connection.MapDeniedReason(ConnectDenyReason.ServerFull),
                Connection.MapDeniedReason(ConnectDenyReason.TeamFull));
        }

        [Theory]
        [InlineData((byte)8)]
        [InlineData((byte)9)]
        [InlineData((byte)255)]
        public void AnUnknownCodeBecomesAGenericRefusal_NotAWrongReason(byte raw)
        {
            // Criterion 7. Generic, and specifically NOT InvalidTicket — which is what this
            // returned before P13, and which sends the player off to fix a login that is fine.
            DisconnectReason mapped = Connection.MapDeniedReason((ConnectDenyReason)raw);

            Assert.Equal(DisconnectReason.Refused, mapped);
            Assert.NotEqual(DisconnectReason.InvalidTicket, mapped);
            Assert.NotEqual(DisconnectReason.ServerFull, mapped);
            Assert.NotEqual(DisconnectReason.TeamFull, mapped);
        }

        [Fact]
        public void TheCodeAnOldBuildWouldNotKnowIsNotSilent()
        {
            // Not silence either: a refusal the client cannot name must still BE a refusal, or
            // the connection simply stops with nothing to show the player.
            Assert.NotEqual(
                DisconnectReason.LocalRequest, Connection.MapDeniedReason((ConnectDenyReason)7));
            Assert.NotEqual(
                DisconnectReason.LocalRequest, Connection.MapDeniedReason((ConnectDenyReason)99));
        }
    }
}
