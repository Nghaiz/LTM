using System;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The subscriber <c>S_PLAYER_LIST</c> never had: actor id to display name, so a killfeed
    /// line renders a name. debt-closure phase 2 task 2a, ledger C-3.
    /// </summary>
    public sealed class PlayerNameTableTests
    {
        private static PlayerListEntry Row(byte actorId, string name)
            => new PlayerListEntry
            {
                ActorId = actorId,
                Name = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(name)),
            };

        /// <summary>
        /// The router hands out its full-length reusable buffer plus a live count, so every test
        /// here goes through the same shape the production wiring does.
        /// </summary>
        private static PlayerListEntry[] Buffer(params PlayerListEntry[] rows)
        {
            var buffer = new PlayerListEntry[ProtocolConstants.MAX_ACTORS];
            Array.Copy(rows, buffer, rows.Length);
            return buffer;
        }

        [Fact]
        public void ABroadcastNamesEveryActorItCarries()
        {
            var table = new PlayerNameTable();

            table.Apply(Buffer(Row(5, "Bob"), Row(9, "Anna")), 2);

            Assert.Equal("Bob", table.NameOf(5));
            Assert.Equal("Anna", table.NameOf(9));
            Assert.Equal(2, table.Count);
        }

        [Fact]
        public void AnActorNoBroadcastNamedReadsAsNullRatherThanAnInventedName()
        {
            var table = new PlayerNameTable();

            table.Apply(Buffer(Row(5, "Bob")), 1);

            // Null, not "Player 7". The caller is the one that knows what an unnamed actor should
            // read as; manufacturing it here makes a genuinely missing name indistinguishable
            // from a real one.
            Assert.Null(table.NameOf(7));
            Assert.Equal("actor 7", table.NameOr(7, "actor 7"));
        }

        [Fact]
        public void ASecondBroadcastREPLACESTheTableRatherThanMergingIntoIt()
        {
            var table = new PlayerNameTable();
            table.Apply(Buffer(Row(5, "Bob"), Row(9, "Anna")), 2);

            // Bob left. S_PLAYER_LIST is a whole table, not a delta.
            table.Apply(Buffer(Row(9, "Anna")), 1);

            // Merging would leave a disconnected player named for the rest of the round, which is
            // the failure that reads as a bug in the killfeed rather than in the table.
            Assert.Null(table.NameOf(5));
            Assert.Equal("Anna", table.NameOf(9));
            Assert.Equal(1, table.Count);
        }

        [Fact]
        public void RowsPastTheLiveCountAreNotRead()
        {
            var table = new PlayerNameTable();
            var buffer = Buffer(Row(5, "Bob"), Row(9, "Anna"));

            // The buffer is MAX_ACTORS long whatever arrived. Reading entries.Length instead of
            // the count would name actor 9 from a row the current broadcast did not carry.
            table.Apply(buffer, 1);

            Assert.Equal("Bob", table.NameOf(5));
            Assert.Null(table.NameOf(9));
        }

        [Fact]
        public void ACountOutsideTheBufferIsRefusedRatherThanReadPastTheEnd()
        {
            var table = new PlayerNameTable();
            var buffer = Buffer(Row(5, "Bob"));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => table.Apply(buffer, buffer.Length + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Apply(buffer, -1));
        }

        [Fact]
        public void ResetDropsEveryName()
        {
            var table = new PlayerNameTable();
            table.Apply(Buffer(Row(5, "Bob")), 1);

            table.Reset();

            Assert.Null(table.NameOf(5));
            Assert.Equal(0, table.Count);
        }

        [Fact]
        public void EveryAppliedBroadcastBumpsTheRevision()
        {
            var table = new PlayerNameTable();
            int before = table.Revision;

            table.Apply(Buffer(Row(5, "Bob")), 1);
            table.Apply(Buffer(Row(5, "Bob")), 1);
            table.Reset();

            // A HUD caches formatted lines off this; it has to move on a re-broadcast that
            // happens to carry the same rows, because the caller cannot tell those apart.
            Assert.Equal(before + 3, table.Revision);
        }

        [Fact]
        public void TheRouterWiresStraightIntoApply()
        {
            var router = new ClientMessageRouter();
            var table = new PlayerNameTable();
            router.OnPlayerList += table.Apply;

            var entries = new[] { Row(5, "Bob"), Row(9, "Anna") };
            var frame = new byte[PlayerListMessage.MaxBodySize + 64];
            var scratch = new byte[PlayerListMessage.MaxBodySize];

            int written = ServerEventWriter.WritePlayerList(frame, scratch, entries);
            Assert.True(written > 0);

            Assert.Equal(1, router.Route(new ReadOnlySpan<byte>(frame, 0, written)));

            // The writer's bytes, through the real router, into the real table. This is the
            // end-to-end shape ClientWiringGate's OnPlayerList exemption existed to describe.
            Assert.Equal("Bob", table.NameOf(5));
            Assert.Equal("Anna", table.NameOf(9));
            Assert.Equal(0, router.UnknownMessages);
            Assert.Equal(0, router.MalformedMessages);
        }

        [Fact]
        public void AHostileNameFromTheServerIsSanitizedBeforeItIsStored()
        {
            // THE CLIENT SANITIZES ITS OWN INGRESS, even though the server sanitized the ticket
            // at the transport (ledger X-36). The server's pass protects the SERVER from the
            // ticket; this one protects THIS client from the server, which it cannot verify --
            // a modified or hostile game server can put any bytes it likes in S_PLAYER_LIST, and
            // they land in a killfeed label with Unity rich text on.
            //
            // Every assertion below goes GREEN on a table that stores NameOf's output verbatim,
            // which is what this test exists to reject.
            var table = new PlayerNameTable();

            table.Apply(
                Buffer(
                    Row(1, "<color=#00000000>"),
                    Row(2, "Bob\nAdmin"),
                    Row(3, "‮Bob")),
                3);

            Assert.DoesNotContain("<", table.NameOr(1, string.Empty));
            Assert.DoesNotContain(">", table.NameOr(1, string.Empty));
            Assert.DoesNotContain("\n", table.NameOr(2, string.Empty));
            Assert.Equal("Bob", table.NameOf(3));
        }

        [Fact]
        public void ANameThatSanitizesToNothingReadsAsUnnamedRatherThanBlank()
        {
            // Null, not "". Null is this table's existing word for "no broadcast has named this
            // actor", so NameOr's fallback fires and the killfeed reads "actor 7" -- a row a
            // reader can act on. Storing the empty string would render a blank feed line, which
            // reads as a rendering fault and teaches nobody anything.
            var table = new PlayerNameTable();

            table.Apply(Buffer(Row(7, "‮​ ")), 1);

            Assert.Null(table.NameOf(7));
            Assert.Equal("actor 7", table.NameOr(7, "actor 7"));

            // The row still counted: a player who chose an unrenderable name is present, and a
            // count that disagreed with the broadcast would be a second bug on top of the first.
            Assert.Equal(1, table.Count);
        }
    }
}
