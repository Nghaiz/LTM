using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase P14 task 3.1 — a game server learns its room from the signed join tickets that
    /// arrive at it, and refuses a second room's.
    /// </summary>
    /// <remarks>
    /// The number used to be a <c>[SerializeField] private int _roomId</c> on a prefab. That is
    /// not cosmetic: <c>MspMessageDispatcher.HandleMatchStarted</c> drops a report whose room the
    /// sending server does not own, with no error and no log, so a hand-typed number made
    /// <c>GsMatchStarted</c> a silent no-op and left the room <c>Waiting</c> for ever.
    /// </remarks>
    public sealed class ServerRoomIdentityTests
    {
        [Fact]
        public void AServerWithNoTicketsHasNoRoom()
        {
            var identity = new ServerRoomIdentity();

            // 0 is the honest answer, and it is what "standalone" has always meant. Fabricating
            // a room here would put a plausible number on a match report nobody allocated.
            Assert.Equal(0, identity.RoomId);
            Assert.False(identity.HasRoom);
        }

        [Fact]
        public void TheFirstTicketsRoomIsAdopted()
        {
            var identity = new ServerRoomIdentity();

            Assert.True(identity.Observe(41, out string conflict));

            Assert.Equal(string.Empty, conflict);
            Assert.Equal(41, identity.RoomId);
            Assert.True(identity.HasRoom);
        }

        [Fact]
        public void LaterTicketsForTheSameRoomAgree()
        {
            var identity = new ServerRoomIdentity();
            identity.Observe(41, out _);

            Assert.True(identity.Observe(41, out _));
            Assert.True(identity.Observe(41, out _));

            Assert.Equal(41, identity.RoomId);
            Assert.Equal(0, identity.ConflictingTickets);
        }

        [Fact]
        public void ASecondRoomsTicketIsRefusedAndTheRoomIsNotRePointed()
        {
            var identity = new ServerRoomIdentity();
            identity.Observe(41, out _);

            Assert.False(identity.Observe(42, out string conflict));

            // Criterion 6. Re-pointing would report the running match under the wrong room and
            // leave the real one Waiting — and the anomaly it implies, one game server allocated
            // to two rooms, is worth a line that names both numbers.
            Assert.Equal(41, identity.RoomId);
            Assert.Equal(1, identity.ConflictingTickets);
            Assert.Contains("42", conflict);
            Assert.Contains("41", conflict);
        }

        [Fact]
        public void ATicketlessJoinIsNotAConflictAndDoesNotClearTheRoom()
        {
            var identity = new ServerRoomIdentity();
            identity.Observe(41, out _);

            // The loopback wire carries no ticket and a development stub carries a zeroed
            // payload. Neither is a second room; treating 0 as one would refuse every LAN
            // client on a server that is hosting a room, and clearing on it would blank the
            // number the match report is stamped with.
            Assert.True(identity.Observe(0, out string conflict));

            Assert.Equal(string.Empty, conflict);
            Assert.Equal(41, identity.RoomId);
            Assert.Equal(0, identity.ConflictingTickets);
        }

        [Fact]
        public void AStandaloneServerStaysAtZeroAcrossTicketlessJoins()
        {
            var identity = new ServerRoomIdentity();

            Assert.True(identity.Observe(0, out _));
            Assert.True(identity.Observe(0, out _));

            Assert.Equal(0, identity.RoomId);
            Assert.False(identity.HasRoom);
        }

        [Fact]
        public void ReleasingLetsTheNextRoomBeAdopted()
        {
            var identity = new ServerRoomIdentity();
            identity.Observe(41, out _);

            identity.Release();
            Assert.False(identity.HasRoom);

            // The master releases the game server on GsMatchEnded, so the process is
            // allocatable again. Without this, the first room a server ever hosted would be the
            // only one it could host: every later allocation's tickets would hit the refusal
            // above and be turned away by a server that is, in fact, free.
            Assert.True(identity.Observe(42, out _));
            Assert.Equal(42, identity.RoomId);
            Assert.Equal(0, identity.ConflictingTickets);
        }

        [Fact]
        public void RefusalsAreCountedNotJustLogged()
        {
            var identity = new ServerRoomIdentity();
            identity.Observe(41, out _);

            identity.Observe(42, out _);
            identity.Observe(43, out _);

            Assert.Equal(2, identity.ConflictingTickets);
            Assert.Equal(41, identity.RoomId);
        }
    }
}
