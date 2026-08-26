using System;
using System.Collections.Generic;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The M2 acceptance criteria that phase 01 left uncovered: the game-server reaper, the
    /// no-server-available path, matchmaking, queue cleanup on disconnect, and match results
    /// reaching the database.
    ///
    /// The joinTicket criteria (4 to 7) are deliberately not here. Issue and Verify live together
    /// in Ironfront.Net.Protocol so that both sides share one implementation, which is where their
    /// tests live too — Conformance/JoinTicketTests covers the bad HMAC, the expired ticket and the
    /// Vietnamese display name, and the replication track's TicketValidationTests covers the accept path.
    /// Duplicating them here would create a second copy that can drift.
    /// </summary>
    public sealed class Phase02MatchmakingTests
    {
        private const string SharedSecret = "this-is-a-long-enough-shared-secret-for-tests";
        private static readonly ushort[] Dustbowl = { 1 };

        /// <summary>M2 criterion 3.</summary>
        [Fact]
        public void ThirtySecondsWithoutAHeartbeatRemovesTheServerAndReleasesItsRoom()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long start = 1_000_000;
            Assert.True(registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, start, out GameServerRecord? server));
            Assert.NotNull(server);
            Assert.Same(server, registry.Allocate(1, 42, start));

            // The record is healthy for 15 s and reaped at 30 s. Both edges matter: reaping at the
            // health boundary would drop a server over one late heartbeat, and the room has to come
            // back or it is stranded on a server that no longer exists.
            Assert.False(server!.IsHealthy(start + 15_001));
            Assert.Empty(registry.Prune(start + 30_000));
            Assert.Equal(new[] { 42 }, registry.Prune(start + 30_001));
            Assert.False(registry.TryGet(server.ServerId, out _));
        }

        /// <summary>
        /// M2 criterion 3, the other half — a late heartbeat rescues the server rather than the
        /// reaper counting from registration.
        /// </summary>
        [Fact]
        public void AHeartbeatResetsTheReaperClock()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long start = 1_000_000;
            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, start, out GameServerRecord? server);

            Assert.True(registry.Heartbeat(1, server!.ServerId, 4, 12f, 8f, 1, start + 25_000));

            Assert.Empty(registry.Prune(start + 40_000));
            Assert.True(registry.TryGet(server.ServerId, out _));
        }

        /// <summary>
        /// M2 criterion 8. Allocation has three independent reasons to refuse, and the client sees
        /// the same 3000 for all of them — so all three are pinned here rather than just the empty
        /// registry, which is the one that would pass by accident.
        /// </summary>
        [Fact]
        public void NoAllocatableServerYieldsNothingForEveryReasonAndTheErrorIsThreeThousand()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;

            Assert.Null(registry.Allocate(1, 42, now));                                  // nothing registered

            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, now, out GameServerRecord? busy);
            registry.Allocate(1, 42, now);
            Assert.Null(registry.Allocate(1, 43, now));                                  // the only server is taken

            registry.Release(busy!.ServerId, 42);
            Assert.Null(registry.Allocate(9, 43, now));                                  // free, but not this map
            Assert.Null(registry.Allocate(1, 43, now + 20_000));                         // free and on the map, but silent

            Assert.Equal(3000, (ushort)ErrorCode.NoGameServerAvailable);
        }

        /// <summary>M2 criterion 9.</summary>
        [Fact]
        public void MatchmakingPutsTwoQueuedPlayersIntoOneRoom()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long now = 1_000_000;

            Assert.True(matchmaking.Enqueue(SessionFor(1, "Một"), 1, now).Ok);
            List<MatchmakeResult> aloneSoFar = matchmaking.Tick(now);
            Assert.Empty(aloneSoFar);

            Assert.True(matchmaking.Enqueue(SessionFor(2, "Hai"), 1, now).Ok);
            List<MatchmakeResult> matched = matchmaking.Tick(now);

            Assert.Equal(2, matched.Count);
            Assert.Equal(matched[0].RoomId, matched[1].RoomId);
            Assert.True(lobby.TryGetRoomById(matched[0].RoomId, out Room? room));
            Assert.Equal(2, room!.Members.Count);
            Assert.Empty(matchmaking.Tick(now));   // and the queue is now empty rather than re-matching them
        }

        /// <summary>
        /// A second player who asks for a map somebody is already waiting on is put into that room
        /// straight away, without a Tick — otherwise the queue would build a second room for a map
        /// that already has one open.
        /// </summary>
        [Fact]
        public void EnqueueJoinsAnOpenRoomOnTheSameMapImmediately()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long now = 1_000_000;
            ServiceResult created = lobby.CreateRoom(SessionFor(1, "Một"),
                new RoomCreateRequest("Open", 1, 8, 0, false, null));

            MatchmakeResult second = matchmaking.Enqueue(SessionFor(2, "Hai"), 1, now);

            Assert.True(second.Ok);
            Assert.Equal(created.Room!.RoomId, second.RoomId);
            Assert.Equal(2, created.Room.Members.Count);
        }

        /// <summary>
        /// Task 4 step 3: waiting past 60 s relaxes the map constraint. The relaxed player has to be
        /// matchable against somebody who wants a specific map — grouping every relaxed entry under
        /// its own key meant a one-minute waiter could only ever match another one-minute waiter,
        /// which is the opposite of relaxing anything.
        /// </summary>
        [Fact]
        public void APlayerPastSixtySecondsIsMatchedWithSomebodyWantingASpecificMap()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long start = 1_000_000;

            Assert.True(matchmaking.Enqueue(SessionFor(1, "Một"), 7, start).Ok);          // wants map 7
            long later = start + 61_000;
            Assert.True(matchmaking.Enqueue(SessionFor(2, "Hai"), 3, later).Ok);          // wants map 3, just arrived

            List<MatchmakeResult> matched = matchmaking.Tick(later);

            Assert.Equal(2, matched.Count);
            Assert.Equal(matched[0].RoomId, matched[1].RoomId);
            Assert.True(lobby.TryGetRoomById(matched[0].RoomId, out Room? room));
            Assert.Equal(3, room!.MapId);   // the fresh request wins; the relaxed player said "any"
        }

        /// <summary>Before the 60 s mark the constraint still holds and the two do not match.</summary>
        [Fact]
        public void TwoPlayersOnDifferentMapsDoNotMatchWhileBothAreStillPicky()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long now = 1_000_000;
            matchmaking.Enqueue(SessionFor(1, "Một"), 7, now);
            matchmaking.Enqueue(SessionFor(2, "Hai"), 3, now);

            Assert.Empty(matchmaking.Tick(now + 59_000));
        }

        /// <summary>
        /// M2 criterion 10, trap 4. A player who vanishes has to leave the queue, or the next Tick
        /// builds a room around somebody who is not connected any more.
        /// </summary>
        [Fact]
        public void ADisconnectedPlayerLeavesTheQueueRatherThanBeingMatched()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            long now = 1_000_000;
            matchmaking.Enqueue(SessionFor(1, "Một"), 1, now);
            matchmaking.Enqueue(SessionFor(2, "Hai"), 1, now);

            matchmaking.Cancel(2);   // what MspMessageDispatcher.OnDisconnected does

            Assert.Empty(matchmaking.Tick(now));
            Assert.Empty(lobby.Rooms);
        }

        /// <summary>Queueing twice is refused rather than putting one player in the queue twice.</summary>
        [Fact]
        public void QueueingTwiceIsRefused()
        {
            var matchmaking = new MatchmakingService(new LobbyService());
            long now = 1_000_000;
            Session session = SessionFor(1, "Một");
            Assert.True(matchmaking.Enqueue(session, 1, now).Ok);

            MatchmakeResult again = matchmaking.Enqueue(session, 1, now);

            Assert.False(again.Ok);
            Assert.Equal(ErrorCode.AlreadyInAnotherRoom, again.ErrorCode);
        }

        /// <summary>M2 criterion 12 — "inspect the DB", as a test rather than by hand.</summary>
        [Fact]
        public void MatchResultsAreWrittenToTheDatabase()
        {
            using var database = new SqliteDatabase(":memory:");
            int first = CreateAccount(database, "playerone", "Một");
            int second = CreateAccount(database, "playertwo", "Hai");
            long endedAt = 1_700_000_000_000;

            database.InsertMatchResult(42, first, 11, 3, 1450, endedAt);
            database.InsertMatchResult(42, second, 4, 9, 620, endedAt);
            database.InsertMatchResult(43, first, 0, 0, 0, endedAt);

            List<MatchResultRecord> stored = database.FindMatchResults(42);

            Assert.Equal(2, stored.Count);
            Assert.Equal(first, stored[0].PlayerId);
            Assert.Equal(11, stored[0].Kills);
            Assert.Equal(3, stored[0].Deaths);
            Assert.Equal(1450, stored[0].Score);
            Assert.Equal(endedAt, stored[0].EndedAt);
            Assert.Equal(second, stored[1].PlayerId);
            Assert.Single(database.FindMatchResults(43));
        }

        /// <summary>
        /// The scoreboard is posted by the game server, so its playerIds arrive over the wire. The
        /// match_results foreign key means an id that is not a real account is refused by SQLite
        /// rather than stored — Microsoft.Data.Sqlite turns foreign keys on, so this is enforced
        /// and not just documentation. MspMessageDispatcher.HandleMatchEnded never reaches this
        /// because it filters on LobbyService.IsMember first; this test records what the storage
        /// layer does if that filter is ever removed, since the exception is not caught in the
        /// dispatch path.
        /// </summary>
        [Fact]
        public void AResultForAnUnknownPlayerIsRefusedByTheDatabase()
        {
            using var database = new SqliteDatabase(":memory:");

            Assert.ThrowsAny<Exception>(() => database.InsertMatchResult(42, 9999, 1, 1, 1, 1_700_000_000_000));
            Assert.Empty(database.FindMatchResults(42));
        }

        /// <summary>
        /// Trap: results are posted over the wire by the game server, so the master has to check
        /// that the server posting them actually holds the room. Otherwise any registered server
        /// can write a scoreboard for a match it never ran.
        /// </summary>
        [Fact]
        public void OnlyTheServerHoldingARoomIsRecognisedAsItsOwner()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;
            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, now, out GameServerRecord? mine);
            registry.TryRegister(2, SharedSecret, "10.0.0.8", 27016, 16, Dustbowl, now, out GameServerRecord? other);
            registry.Allocate(1, 42, now);   // goes to whichever is least busy; both are idle, so mine

            Assert.True(registry.OwnsRoom(1, mine!.ServerId, 42));
            Assert.False(registry.OwnsRoom(2, other!.ServerId, 42));   // registered, but not this room
            Assert.False(registry.OwnsRoom(2, mine.ServerId, 42));     // right room, wrong connection
            Assert.False(registry.OwnsRoom(1, mine.ServerId, 43));     // right server, wrong room
        }

        /// <summary>
        /// A game server's endpoint ultimately reaches every player, so it must be an address
        /// literal and is normalized before storage. The dispatcher chooses either the TCP peer
        /// address or the authenticated server's explicit deployment endpoint before calling this
        /// registry; the registry makes neither path capable of storing arbitrary host text.
        /// </summary>
        [Fact]
        public void TheRegistryStoresTheValidatedEndpointItWasGiven()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;

            Assert.True(registry.TryRegister(
                1, SharedSecret, "203.0.113.9", 27015, 16, Dustbowl, now,
                out GameServerRecord? server));

            Assert.Equal("203.0.113.9", server!.PublicIp);
            Assert.Equal(27015, server.UdpPort);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-an-ip")]
        [InlineData("game-server.ironfront.example")]
        public void ARegistrationWithAnInvalidEndpointIsRefused(string endpoint)
        {
            var registry = new GameServerRegistry(SharedSecret);

            Assert.False(registry.TryRegister(
                1, SharedSecret, endpoint, 27015, 16, Dustbowl, 1_000_000, out _));
        }

        /// <summary>A registration that could never host a match is refused at the door.</summary>
        [Theory]
        [InlineData(0, (byte)16)]        // no UDP port
        [InlineData(70000, (byte)16)]    // not a port
        [InlineData(27015, (byte)0)]     // room for nobody
        public void AnUnusableRegistrationIsRefused(int udpPort, byte maxPlayers)
        {
            var registry = new GameServerRegistry(SharedSecret);

            Assert.False(registry.TryRegister(1, SharedSecret, "10.0.0.7", udpPort, maxPlayers, Dustbowl, 1_000_000, out _));
        }

        /// <summary>Allocation picks the least busy healthy server, per D-AD's registry design.</summary>
        /// <remarks>
        /// Cpu and tick agree here, so this test passes under either ordering rule and cannot by
        /// itself say which one is in force. That is what the three X-7 tests below are for; this
        /// one stays because "least busy wins" is the behaviour D-AD promised, whatever measures it.
        /// </remarks>
        [Fact]
        public void AllocationPrefersTheLeastBusyServer()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;
            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, now, out GameServerRecord? loaded);
            registry.TryRegister(2, SharedSecret, "10.0.0.8", 27016, 16, Dustbowl, now, out GameServerRecord? quiet);
            registry.Heartbeat(1, loaded!.ServerId, 12, 70f, 9f, 1, now);
            registry.Heartbeat(2, quiet!.ServerId, 0, 5f, 2f, 0, now);

            Assert.Same(quiet, registry.Allocate(1, 42, now));
        }

        /// <summary>
        /// Allocation follows measured tick time, not registration order. Ledger <b>X-7</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The registrations are deliberately in the wrong order.</b> The busy server is
        /// registered FIRST, so it is what <c>Dictionary.Values</c> yields first and what the
        /// old <c>CpuPercent</c> comparison left <c>best</c> pointing at. A rule that reads any
        /// signal at all picks the second one; a rule that reads none returns the first.
        /// </para>
        /// <para>
        /// <b>Both report <c>cpuPercent: -1</c>, which is production reality, not a contrivance.</b>
        /// <c>ServerMasterReporter.Update</c> sends that sentinel on every heartbeat by design,
        /// so the old comparison was false for every pair of live servers and allocation was
        /// decided by dictionary layout. This is the X-7 repro.
        /// </para>
        /// </remarks>
        [Fact]
        public void AllocationFollowsTickTimeAndNotRegistrationOrder()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;

            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, now, out GameServerRecord? busy);
            registry.TryRegister(2, SharedSecret, "10.0.0.8", 27016, 16, Dustbowl, now, out GameServerRecord? quick);

            // The sentinel every real game server sends. It orders nothing, and must not.
            registry.Heartbeat(1, busy!.ServerId, 0, -1f, 31f, 0, now);
            registry.Heartbeat(2, quick!.ServerId, 0, -1f, 4f, 0, now);

            Assert.Same(quick, registry.Allocate(1, 42, now));
        }

        /// <summary>
        /// The cpu sentinel does not outrank the measured signal, whichever way it points.
        /// </summary>
        /// <remarks>
        /// A guard against the fix being quietly reverted to "cpu first, tick as a tie-break":
        /// here cpu and tick disagree, so any rule consulting cpu ahead of tick returns the
        /// other server. `AllocationPrefersTheLeastBusyServer` cannot catch that — its two
        /// signals agree, so it passes under either rule.
        /// </remarks>
        [Fact]
        public void AllocationPrefersMeasuredTickTimeOverAReportedCpuPercent()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;

            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, now, out GameServerRecord? lowCpu);
            registry.TryRegister(2, SharedSecret, "10.0.0.8", 27016, 16, Dustbowl, now, out GameServerRecord? lowTick);

            registry.Heartbeat(1, lowCpu!.ServerId, 0, 5f, 30f, 0, now);    // idle cpu, struggling ticks
            registry.Heartbeat(2, lowTick!.ServerId, 0, 80f, 3f, 0, now);   // busy cpu, healthy ticks

            Assert.Same(lowTick, registry.Allocate(1, 42, now));
        }

        /// <summary>
        /// Equal load resolves by server id, in a registry where id order and dictionary order
        /// DISAGREE. Ledger <b>X-7</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The reap-and-re-register is the whole test, and it is not contrived.</b> A tie on
        /// tick time is the common case at match start — idle servers all report the same
        /// number — so the tie-break is what decides most real allocations, and a tie-break that
        /// silently equals "first one the dictionary yields" is the X-7 defect with a new name.
        /// <c>Dictionary</c> hands out insertion order only while nothing is removed; once
        /// <c>Prune</c> or <c>RemoveConnection</c> frees a slot, the next registration reuses it
        /// and lands at the FRONT of iteration. That is a long-running master's steady state,
        /// not an edge case.
        /// </para>
        /// <para>
        /// <b>Here server 4 occupies freed slot 0, so iteration yields 4, 2, 3 while ascending
        /// id says 2.</b> An earlier version of this test registered three servers and asserted
        /// the first — which passes under BOTH rules, because the lowest id was also the first
        /// yielded. Mutation-testing caught it: degrading the tie-break to "first seen wins"
        /// left that version green (green-that-proves-nothing.md).
        /// </para>
        /// </remarks>
        [Fact]
        public void AllocationIsDeterministicWhenLoadTies()
        {
            var registry = new GameServerRegistry(SharedSecret);
            long now = 1_000_000;

            registry.TryRegister(1, SharedSecret, "10.0.0.7", 27015, 16, Dustbowl, now, out GameServerRecord? reaped);
            registry.TryRegister(2, SharedSecret, "10.0.0.8", 27016, 16, Dustbowl, now, out GameServerRecord? lowest);
            registry.TryRegister(3, SharedSecret, "10.0.0.9", 27017, 16, Dustbowl, now, out GameServerRecord? third);

            // Its connection drops; the dictionary slot it held goes on the free list.
            registry.RemoveConnection(1);
            registry.TryRegister(4, SharedSecret, "10.0.0.10", 27018, 16, Dustbowl, now, out GameServerRecord? newest);

            Assert.NotNull(newest);
            Assert.True(newest!.ServerId > lowest!.ServerId, "the reused slot must carry a HIGHER id");
            Assert.True(lowest.ServerId < third!.ServerId);
            Assert.Equal(3, registry.Count);
            Assert.False(registry.TryGet(reaped!.ServerId, out _));

            // All three idle and identical, so only the tie-break can choose.
            registry.Heartbeat(2, lowest.ServerId, 0, -1f, 5f, 0, now);
            registry.Heartbeat(3, third.ServerId, 0, -1f, 5f, 0, now);
            registry.Heartbeat(4, newest.ServerId, 0, -1f, 5f, 0, now);

            // Lowest id, NOT the one the dictionary yields first (which is `newest`, in the
            // slot the reaped server vacated).
            Assert.Same(lowest, registry.Allocate(1, 42, now));
            Assert.Same(third, registry.Allocate(1, 43, now));
            Assert.Same(newest, registry.Allocate(1, 44, now));
        }

        private static int CreateAccount(SqliteDatabase database, string username, string displayName)
        {
            Assert.True(database.InsertAccount(username, "hash", displayName, 1_700_000_000_000));
            return database.FindAccount(username)!.PlayerId;
        }

        private static Session SessionFor(int playerId, string name) => new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue
        };
    }
}
