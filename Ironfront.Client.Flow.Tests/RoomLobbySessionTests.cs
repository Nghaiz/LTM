using System;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Phase P16 tasks 3.1 to 3.5 — the four <c>MasterSession</c> wrappers the room screens call,
    /// the pushes they render, and the ticket refresh that lets a room's creator into its match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three of these opcodes had no client caller at all.</b> <c>RoomCreate</c>,
    /// <c>RoomReady</c> and <c>Chat</c> were implemented, routed and tested server-side with
    /// nothing in the game sending them; <c>RoomTeam</c> is new. These tests are what stops the
    /// wrappers being a fourth half-built endpoint.
    /// </para>
    /// <para>
    /// <b>No Unity here.</b> <c>MasterSession</c> is a plain class for exactly this reason, so
    /// every decision below is reachable by <c>dotnet test</c> and none of the screens' drawing
    /// is.
    /// </para>
    /// </remarks>
    public sealed class RoomLobbySessionTests
    {
        [Fact]
        public async Task CreateRoomLandsInTheRoomLobbyWithTheFormsMapAndName()
        {
            Fixture fixture = await Fixture.InTheRoomBrowserAsync();

            fixture.Master.NextCreateRoom = new CreateRoomResult(true, 77, 0);

            Assert.True(await fixture.Session.CreateRoomAsync("Scrims", 2, 8, 3, null));

            Assert.Equal(GameFlowState.RoomLobby, fixture.Flow.State);
            Assert.Equal(77, fixture.Session.JoinedRoomId);

            // Read off the FORM, not off a room-list row: the list this client holds predates
            // the room it just made, so a lookup would find nothing and load the wrong map.
            Assert.Equal(2, fixture.Session.JoinedMapId);

            CreateRoomRequest sent = fixture.Master.LastCreateRoom!;
            Assert.Equal("Scrims", sent.Name);
            Assert.Equal(8, sent.MaxPlayers);
            Assert.Equal(3, sent.BotCount);
            Assert.False(sent.IsPrivate);
            Assert.Null(sent.PasswordHash);
        }

        /// <summary>
        /// A private room's password is hashed with the same function a join uses.
        /// </summary>
        /// <remarks>
        /// The master bcrypt-verifies a joiner's hash against the creator's, so the two call
        /// sites must agree byte for byte. Asserted rather than assumed because a second hasher
        /// produces a room nobody can enter and no error anywhere — the join simply answers
        /// WrongRoomPassword forever (P16 risk table, score 12).
        /// </remarks>
        [Fact]
        public async Task APrivateRoomIsHashedTheSameWayAJoinHashesIt()
        {
            Fixture fixture = await Fixture.InTheRoomBrowserAsync();

            Assert.True(await fixture.Session.CreateRoomAsync("Locked", 1, 4, 0, "hunter2"));

            CreateRoomRequest sent = fixture.Master.LastCreateRoom!;
            Assert.True(sent.IsPrivate);
            Assert.Equal(PasswordHasher.HashRoomPassword("hunter2"), sent.PasswordHash);
        }

        [Fact]
        public async Task ARefusedCreateComesBackToTheBrowserWithAMessage()
        {
            Fixture fixture = await Fixture.InTheRoomBrowserAsync();

            fixture.Master.NextCreateRoom =
                new CreateRoomResult(false, 0, (int)ErrorCode.AlreadyInAnotherRoom);

            Assert.False(await fixture.Session.CreateRoomAsync("Scrims", 1, 8, 0, null));

            Assert.Equal(GameFlowState.RoomBrowser, fixture.Flow.State);
            Assert.Equal(0, fixture.Session.JoinedRoomId);
            Assert.Equal(MasterErrorText.Describe(ErrorCode.AlreadyInAnotherRoom), fixture.Session.LastError);
        }

        /// <summary>
        /// An "ok" carrying no room id is a failure, not a room.
        /// </summary>
        /// <remarks>
        /// The same shape <c>JoinRoomAsync</c> guards for a ticketless join. Carried forward, a
        /// room id of zero would make every later push about "our room" match nothing and the
        /// player would sit in a lobby that never updated.
        /// </remarks>
        [Fact]
        public async Task AnOkCreateWithNoRoomIdIsTreatedAsAFailure()
        {
            Fixture fixture = await Fixture.InTheRoomBrowserAsync();

            fixture.Master.NextCreateRoom = new CreateRoomResult(true, 0, 0);

            Assert.False(await fixture.Session.CreateRoomAsync("Ghost", 1, 8, 0, null));
            Assert.Equal(GameFlowState.RoomBrowser, fixture.Flow.State);
            Assert.Equal(0, fixture.Session.JoinedRoomId);
        }

        [Fact]
        public async Task ReadyTeamAndChatReachTheWire()
        {
            Fixture fixture = await Fixture.InARoomAsync();

            Assert.True(await fixture.Session.SetReadyAsync(true));
            Assert.True(fixture.Master.LastReady);

            Assert.True(await fixture.Session.SetTeamAsync(1));
            Assert.Equal((byte)1, fixture.Master.LastTeam);

            Assert.True(await fixture.Session.SendChatAsync(0, "  hello  "));
            Assert.Equal("hello", fixture.Master.LastChatText);
        }

        /// <summary>
        /// A blank chat line is dropped before the wire, not sent.
        /// </summary>
        /// <remarks>
        /// An accidental Enter would otherwise put an empty line carrying the sender's name in
        /// front of everybody in the room, which is worse than nothing happening.
        /// </remarks>
        [Fact]
        public async Task BlankChatIsNeverSent()
        {
            Fixture fixture = await Fixture.InARoomAsync();

            Assert.False(await fixture.Session.SendChatAsync(0, "   "));
            Assert.Equal(0, fixture.Master.ChatSendCalls);
        }

        /// <summary>
        /// An unsolicited ErrorPush reaches <c>LastError</c>. P16 3.5.
        /// </summary>
        /// <remarks>
        /// <b>Nothing was subscribed to <c>IMasterClient.OnError</c> before P16.</b> Ready, leave
        /// and team are sent with no response opcode, so their refusals arrive ONLY this way —
        /// and with no subscriber they were dropped. A refused side switch would have been a
        /// button that did nothing and said nothing, which is criteria 4 and 5 failing silently.
        /// </remarks>
        [Fact]
        public async Task AnUnsolicitedRefusalReachesTheErrorLine()
        {
            Fixture fixture = await Fixture.InARoomAsync();

            string seen = string.Empty;
            fixture.Session.OnError += message => seen = message;

            fixture.Master.PushError((int)ErrorCode.TeamsWouldUnbalance, "Cannot change team.");

            Assert.Equal(MasterErrorText.Describe(ErrorCode.TeamsWouldUnbalance), fixture.Session.LastError);
            Assert.Equal(fixture.Session.LastError, seen);
        }

        [Fact]
        public async Task ARoomPushForOurRoomIsResurfacedWithItsRoster()
        {
            Fixture fixture = await Fixture.InARoomAsync();

            RoomState? seen = null;
            fixture.Session.OnRoomState += room => seen = room;

            var members = new[]
            {
                new RoomMember { PlayerId = 42, Name = "Tester", Team = 0, Ready = true },
                new RoomMember { PlayerId = 7, Name = "Other", Team = 1, Ready = false },
            };

            fixture.Master.PushRoomState(Fixture.RoomId, RoomLifecycleState.Waiting, members);

            Assert.NotNull(seen);
            Assert.Equal(2, seen!.Members.Length);

            // Held as well as raised, so a screen that becomes visible between pushes has
            // something to draw rather than an empty roster.
            Assert.Same(seen, fixture.Session.Room);
        }

        [Fact]
        public async Task ARoomPushForSomebodyElsesRoomIsIgnored()
        {
            Fixture fixture = await Fixture.InARoomAsync();

            int raised = 0;
            fixture.Session.OnRoomState += _ => raised++;

            fixture.Master.PushRoomState(Fixture.RoomId + 1, RoomLifecycleState.Waiting);

            // The master BROADCASTS room state, so this is ordinary traffic and not an error.
            Assert.Equal(0, raised);
            Assert.Null(fixture.Session.Room);
        }

        [Fact]
        public async Task AChatPushIsResurfaced()
        {
            Fixture fixture = await Fixture.InARoomAsync();

            ChatMessage? seen = null;
            fixture.Session.OnChat += message => seen = message;

            fixture.Master.RaiseChat(new ChatMessage
            {
                Channel = 0, FromPlayerId = 7, FromName = "Other", Text = "hello",
            });

            Assert.NotNull(seen);
            Assert.Equal("Other", seen!.FromName);
            Assert.Equal("hello", seen.Text);
        }

        /// <summary>
        /// The start push fetches a FRESH ticket before dialling. P16 3.4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what lets a room's creator into its own match.</b> <c>RoomCreate</c>
        /// allocates no game server and mints no ticket, so the creator reaches
        /// <c>Starting</c> holding nothing to dial with. It is also what carries a side SWITCH
        /// into the match: a ticket carries the team the roster held when it was issued, so one
        /// minted at join time would seat a switched player on their old side.
        /// </para>
        /// <para>
        /// Asserted through <c>PendingJoin</c> rather than through the flow state, because the
        /// fixture's transport is not a real socket and the dial's outcome is not this test's
        /// subject — the ticket is.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task TheStartPushFetchesAFreshTicketBeforeDialling()
        {
            Fixture fixture = await Fixture.CreatedARoomAsync();

            // A creator holds nothing to dial with: no server was allocated for the room.
            Assert.False(fixture.Session.PendingJoin.IsValid);

            fixture.Master.PushRoomState(Fixture.RoomId, RoomLifecycleState.Starting);

            await fixture.Master.WaitForJoinAsync();

            Assert.Equal(Fixture.RoomId, fixture.Master.LastRoomId);
            Assert.True(fixture.Session.PendingJoin.IsValid);
        }

        /// <summary>
        /// A repeated start push does not start a second fetch or a second dial.
        /// </summary>
        /// <remarks>
        /// The push repeats on every member change and on a retransmit, and the flow stays
        /// <c>RoomLobby</c> across the fetch — so the state guard that made the old synchronous
        /// path idempotent no longer covers it. A second <c>EnterMatch</c> would reach
        /// <c>Transition(ConnectingGame)</c> from <c>ConnectingGame</c> and throw out of a
        /// network callback.
        /// </remarks>
        [Fact]
        public async Task RepeatedStartPushesFetchOneTicket()
        {
            Fixture fixture = await Fixture.CreatedARoomAsync();

            fixture.Master.PushRoomState(Fixture.RoomId, RoomLifecycleState.Starting);
            await fixture.Master.WaitForJoinAsync();

            int after = fixture.Master.JoinRoomCalls;

            fixture.Master.PushRoomState(Fixture.RoomId, RoomLifecycleState.Starting);
            fixture.Master.PushRoomState(Fixture.RoomId, RoomLifecycleState.InMatch);

            Assert.Equal(after, fixture.Master.JoinRoomCalls);
        }

        [Fact]
        public async Task TheMasterPingIsMeasuredOnARefreshAndIsUnsetBeforeOne()
        {
            var master = new FakeMasterClient();
            var flow = new GameFlowController();
            var session = new MasterSession(master, flow, new FakeTransportClient(), _ => 1);

            Assert.Equal(-1, session.MasterPingMs);

            flow.Transition(GameFlowState.LoginScreen);
            Assert.True(await session.ConnectAsync("host", 1));
            Assert.True(await session.LoginAsync("user", "pass"));
            Assert.True(await session.OpenRoomBrowserAsync());

            // Rounded UP, so a fast in-process fake reads 1 rather than 0 -- and 0 stays
            // distinguishable from "not measured", which is -1.
            Assert.True(session.MasterPingMs >= 0);
        }

        // ------------------------------------------------------------------ fixture

        private sealed class Fixture
        {
            internal const int RoomId = 12;

            internal FakeMasterClient Master { get; private set; } = null!;
            internal GameFlowController Flow { get; private set; } = null!;
            internal MasterSession Session { get; private set; } = null!;

            private static async Task<Fixture> SignedInAsync()
            {
                var fixture = new Fixture
                {
                    Master = new FakeMasterClient(),
                    Flow = new GameFlowController(),
                };

                fixture.Session = new MasterSession(
                    fixture.Master, fixture.Flow, new FakeTransportClient(), _ => 1);

                // Booting -> LoginScreen is the Title screen's edge, taken by the player rather
                // than by the session, so a test that starts at a login has to take it too.
                fixture.Flow.Transition(GameFlowState.LoginScreen);

                Assert.True(await fixture.Session.ConnectAsync("host", 1));
                Assert.True(await fixture.Session.LoginAsync("user", "pass"));
                return fixture;
            }

            internal static async Task<Fixture> InTheRoomBrowserAsync()
            {
                Fixture fixture = await SignedInAsync();
                Assert.True(await fixture.Session.OpenRoomBrowserAsync());
                return fixture;
            }

            /// <summary>In a room by JOINING one, so a ticket is already held.</summary>
            internal static async Task<Fixture> InARoomAsync()
            {
                Fixture fixture = await InTheRoomBrowserAsync();
                Assert.True(await fixture.Session.JoinRoomAsync(RoomId, null));
                return fixture;
            }

            /// <summary>In a room by CREATING one, so no ticket is held.</summary>
            internal static async Task<Fixture> CreatedARoomAsync()
            {
                Fixture fixture = await InTheRoomBrowserAsync();
                fixture.Master.NextCreateRoom = new CreateRoomResult(true, RoomId, 0);
                Assert.True(await fixture.Session.CreateRoomAsync("Made", 1, 4, 0, null));
                return fixture;
            }
        }
    }
}
