using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;

namespace Ironfront.Tools.E2E
{
    /// <summary>
    /// One account, walked the whole way: connect -> login -> join -> UDP snapshot. M2
    /// criterion 14, carried since phase 02 and never verified end to end until this existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The four legs are graded separately because they fail for different reasons.</b> A
    /// master that is down, an account that cannot log in, a matchmaker with no registered
    /// game server, and a game server that admits a connection but sends nothing are four
    /// distinct outages, and a single "e2e failed" line makes the operator re-derive which one
    /// they have. Each leg prints its own verdict and the exit code names the first that broke.
    /// </para>
    /// <para>
    /// <b>Leg 4 requires an inbound datagram, not just a handshake.</b> A server that completes
    /// the handshake and then broadcasts nothing is precisely the failure the Linux dedicated
    /// build shipped with for weeks — clean start, quiet log, container Up, and no match. A
    /// handshake-only assertion is green for that server, so it is not the assertion.
    /// </para>
    /// <para>
    /// <b><c>--negative</c> is what makes the pass mean something.</b> It corrupts one byte of
    /// the master's signed ticket and requires leg 4 to be REFUSED. Without that run, a game
    /// server left with <c>IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1</c> would admit
    /// anybody and the positive run would still print PASS — the gate would be measuring that
    /// a UDP port is open, which it already knew. See tools/run-e2e.ps1, which runs both.
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>Exit codes. The number names the leg, so a CI log line is diagnostic.</summary>
        private const int ExitPass = 0;
        private const int ExitMasterUnreachable = 1;
        private const int ExitLoginFailed = 2;
        private const int ExitJoinFailed = 3;
        private const int ExitUdpFailed = 4;

        /// <summary>--room-start only: the room never reached <c>Starting</c>. P14 criterion 2.</summary>
        private const int ExitNeverStarted = 5;

        /// <summary>--room-start only: it started, and the master never saw the match begin.</summary>
        private const int ExitNeverInMatch = 6;

        private const int ExitUsage = 64;

        /// <summary>A 64-char hex string is what <c>AuthService.IsValidSha256</c> accepts.</summary>
        private const string PasswordHash =
            "e2e00000000000000000000000000000000000000000000000000000000000e2";

        private const int PollYieldsBeforeSleeping = 512;

        public static async Task<int> Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Options.PrintUsage();
                return ExitUsage;
            }

            if (options.ShowHelp)
            {
                Options.PrintUsage();
                return ExitPass;
            }

            Console.WriteLine($"e2e: master {options.MasterHost}:{options.MasterPort}  " +
                              $"account {options.Username}  budget {options.TimeoutSeconds}s" +
                              (options.Negative ? "  [NEGATIVE: a corrupted ticket must be refused]" : string.Empty));
            Console.WriteLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                if (options.Partner)
                    return await WalkPartnerAsync(options, cts.Token).ConfigureAwait(false);

                return options.RoomStart
                    ? await WalkRoomStartAsync(options, cts.Token).ConfigureAwait(false)
                    : await WalkAsync(options, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"E2E FAIL — the whole walk exceeded its {options.TimeoutSeconds}s budget.");
                Console.Error.WriteLine("  The last leg printed above is the one that hung.");
                return ExitUdpFailed;
            }
        }

        private static async Task<int> WalkAsync(Options options, CancellationToken ct)
        {
            using var master = new Ironfront.MasterClient.MasterClient();
            using var game = new UdpTransportClient();

            // The room has to be held open by SOMEBODY ELSE, and that is not a quirk of the
            // harness -- it is what the lobby actually does. CreateRoom puts the creator IN the
            // room, so the same account then gets AlreadyInAnotherRoom (2004) from JoinRoom; and
            // leaving to fix that empties the room, which LobbyService deletes on the spot. A
            // single account can therefore never reach the join path that issues a ticket.
            // So a second account creates the room and stays in it, which is also the honest
            // shape of the thing being tested: this is a multiplayer game, and the walk being
            // verified is a player joining a room somebody else is sitting in.
            using var host = new Ironfront.MasterClient.MasterClient();

            // ---- leg 1: the master is reachable ------------------------------------------
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await master.ConnectAsync(options.MasterHost, options.MasterPort, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(1, "master", false, $"could not reach {options.MasterHost}:{options.MasterPort} — {ex.Message}");
                return ExitMasterUnreachable;
            }

            if (master.State != MasterConnectionState.Connected)
            {
                Leg(1, "master", false, $"connect returned but state is {master.State}");
                return ExitMasterUnreachable;
            }

            Leg(1, "master", true, $"connected in {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 2: an account logs in -----------------------------------------------
            // Register first and ignore its verdict: a re-run of this harness against a
            // database that survived the last one is the ordinary case, and "already exists"
            // is not a failure of the leg being graded. Login is the assertion.
            stopwatch.Restart();
            try
            {
                await PumpAsync(master, master.RegisterAsync(options.Username, PasswordHash, options.Username, ct), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       (register said: {ex.Message} — continuing to login, which is the graded step)");
            }

            LoginResult login;
            try
            {
                login = await PumpAsync(master, master.LoginAsync(options.Username, PasswordHash, ct), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(2, "login", false, $"login threw — {ex.Message}");
                return ExitLoginFailed;
            }

            if (!login.Ok)
            {
                Leg(2, "login", false, $"master refused the account, errorCode {login.ErrorCode}");
                return ExitLoginFailed;
            }

            Leg(2, "login", true, $"playerId {login.PlayerId} in {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 3: a join allocates a real game server ------------------------------
            // This is the leg that proves matchmaking had a REGISTERED, heartbeating server to
            // hand out: the master answers NoGameServerAvailable / GameServerNotResponding
            // rather than an address when it does not.
            stopwatch.Restart();
            int roomId;
            try
            {
                roomId = await ResolveRoomAsync(master, host, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(3, "join", false, $"could not get a room to join — {ex.Message}");
                return ExitJoinFailed;
            }

            if (roomId == 0)
            {
                Leg(3, "join", false, "no room existed and the host account could not create one");
                return ExitJoinFailed;
            }

            JoinResult joined;
            try
            {
                joined = await PumpAsync(master, master.JoinRoomAsync(roomId, null, ct), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(3, "join", false, $"join threw — {ex.Message}");
                return ExitJoinFailed;
            }

            if (!joined.Ok)
            {
                Leg(3, "join", false, $"master refused the join, errorCode {joined.ErrorCode} — {ExplainJoinError(joined.ErrorCode)}");
                return ExitJoinFailed;
            }

            if (string.IsNullOrWhiteSpace(joined.GameServerIp) || joined.GameServerPort <= 0)
            {
                Leg(3, "join", false, $"join said ok but named no server: '{joined.GameServerIp}':{joined.GameServerPort}");
                return ExitJoinFailed;
            }

            if (joined.JoinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
            {
                Leg(3, "join", false, $"ticket is {joined.JoinTicket.Length} bytes, " +
                                      $"expected {ProtocolConstants.JOIN_TICKET_SIZE}");
                return ExitJoinFailed;
            }

            Leg(3, "join", true, $"room {roomId} -> {joined.GameServerIp}:{joined.GameServerPort}, " +
                                 $"{joined.JoinTicket.Length}-byte signed ticket, {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 4: the UDP dial, with that ticket, reaching a live match -------------
            byte[] ticket = joined.JoinTicket;
            if (options.Negative)
            {
                ticket = (byte[])ticket.Clone();
                // One byte, in the signature's half rather than the payload's, so the failure
                // is an HMAC rejection and not a parse error that would fail for a second
                // reason and prove the wrong thing.
                ticket[^1] ^= 0xFF;
            }

            UdpOutcome outcome = await DialAsync(game, joined.GameServerIp, joined.GameServerPort, ticket, options, ct)
                .ConfigureAwait(false);

            if (options.Negative)
            {
                // Inverted grading: being let in with a corrupted ticket is the failure.
                if (outcome.Connected)
                {
                    Leg(4, "udp", false, "NEGATIVE RUN WAS ADMITTED — the game server accepted a corrupted " +
                                         "ticket, so the positive run proves only that a UDP port is open. " +
                                         "Check IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS is 0.");
                    return ExitUdpFailed;
                }

                Leg(4, "udp", true, $"refused, as it must be — {outcome.Detail}");
                Console.WriteLine();
                Console.WriteLine("E2E NEGATIVE PASS — a corrupted ticket does not get in.");
                return ExitPass;
            }

            if (!outcome.Connected)
            {
                Leg(4, "udp", false, outcome.Detail);
                return ExitUdpFailed;
            }

            if (!outcome.ReceivedPayload)
            {
                Leg(4, "udp", false, $"handshake completed (connectionId {outcome.ConnectionId}, " +
                                     $"mapId {outcome.MapId}) but no snapshot arrived within " +
                                     $"{options.PayloadWaitSeconds}s — the server admitted the client " +
                                     "into a match it is not simulating");
                return ExitUdpFailed;
            }

            Leg(4, "udp", true, $"connectionId {outcome.ConnectionId}, mapId {outcome.MapId}, " +
                                $"playerId {outcome.MyPlayerId}, {outcome.PayloadsReceived} payload(s) " +
                                $"in {outcome.ElapsedMs} ms");

            Console.WriteLine();
            Console.WriteLine("E2E PASS — one account walked login -> join -> UDP and is receiving a match.");
            return ExitPass;
        }

        /// <summary>
        /// The P14 walk: two accounts sit in a room, mark themselves ready, and are carried
        /// into a live match by the master's own room-state push. Criterion 2, which the phase
        /// calls "the phase".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What it is allowed to touch, and why that is the whole assertion.</b> After the
        /// joins, the ONLY call this harness makes is <c>SetReadyAsync(true)</c>. There is no
        /// "enter match" call, no key press, and no debug button — the button it replaces was
        /// deleted in the same commit. If the room never reaches <c>Starting</c>, nothing here
        /// can rescue the walk, which is exactly the property being graded.
        /// </para>
        /// <para>
        /// <b>Three accounts, not two.</b> Creating a room joins you to it, so the creator is a
        /// member and the start rule requires EVERY member ready. The host therefore readies
        /// too. It also has to stay connected: <c>LobbyService.LeaveRoom</c> deletes a room the
        /// moment its last member leaves, and a disconnect leaves.
        /// </para>
        /// <para>
        /// <b>Its own accounts and its own room.</b> Run after the positive walk in the same
        /// master process, the shared host account would already be in the <c>e2e</c> room and
        /// answer <c>AlreadyInAnotherRoom</c>, and that room may sit in a state
        /// <c>CanJoinRoom</c> refuses. Distinct names cost nothing and make the mode
        /// order-independent.
        /// </para>
        /// <para>
        /// <b>Leg 5 waits for <c>InMatch</c>, and that is not a formality.</b> <c>Starting</c>
        /// is the master's decision; <c>InMatch</c> is the game server reporting back through
        /// <c>GsMatchStarted</c>, and that report is dropped in silence unless the room the
        /// server names is the room the master allocated it. So leg 5 is what proves the server
        /// learned its room from the join ticket — the half of P14 that no unit test can reach,
        /// because the failure it guards against has no error and no log.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Be the SECOND player in a room somebody else made. P16 criterion 2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists at all.</b> P16 shipped a room browser, a create-room form and a
        /// room lobby, and criterion 2 grades them with a second player present: the roster has
        /// to show both, both have to mark ready, and both have to be carried into the match.
        /// Every other walk in this file creates its own room, so none of them can be the other
        /// half of that -- and lane B skips the menu entirely, because IRONFRONT_LANEB_ROLE
        /// makes ClientFlowBootstrap bypass it. So the UI had no way to be graded by anything.
        /// </para>
        /// <para>
        /// <b>It joins rather than creates, and that is the whole point.</b> The room is
        /// expected to be sitting there already, made by a human pressing CREATE on the shipped
        /// form. Finding none is a FAILURE and not an invitation to make one -- a harness that
        /// quietly created its own room would pass while grading nothing, which is exactly the
        /// shape of green this project keeps finding.
        /// </para>
        /// </remarks>
        private static async Task<int> WalkPartnerAsync(Options options, CancellationToken ct)
        {
            using var partner = new Ironfront.MasterClient.MasterClient();
            using var game    = new UdpTransportClient();

            IMasterClient[] all = { partner };

            partner.OnError += (code, message) =>
                Console.WriteLine($"       (master error to partner: {code} {message})");

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await partner.ConnectAsync(options.MasterHost, options.MasterPort, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(1, "master", false, $"could not reach {options.MasterHost}:{options.MasterPort} - {ex.Message}");
                return ExitMasterUnreachable;
            }

            Leg(1, "master", true, $"connected in {stopwatch.ElapsedMilliseconds} ms");

            stopwatch.Restart();
            try
            {
                await LoginAsync(partner, options.Username, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(2, "login", false, ex.Message);
                return ExitLoginFailed;
            }

            Leg(2, "login", true, $"{options.Username} in {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 3: the room the OTHER player made ------------------------------------
            stopwatch.Restart();
            RoomInfo[] rooms = await PumpAsync(partner, partner.GetRoomsAsync(ct), ct).ConfigureAwait(false);
            RoomInfo? target = null;
            foreach (RoomInfo room in rooms)
            {
                if (room.RoomId == 0 || !room.IsJoinable) continue;
                target = room;
                break;
            }

            if (target is null)
            {
                Leg(3, "room", false,
                    $"the master lists {rooms.Length} room(s) and none is joinable. This walk is the " +
                    "SECOND player -- the first one has to have created a room from the UI first. " +
                    "It will not create one, because a room it made itself would prove nothing.");
                return ExitJoinFailed;
            }

            JoinResult joined = await PumpAsync(
                partner, partner.JoinRoomAsync(target.RoomId, null, ct), ct).ConfigureAwait(false);

            if (!joined.Ok)
            {
                Leg(3, "room", false, $"join refused, errorCode {joined.ErrorCode} - {ExplainJoinError(joined.ErrorCode)}");
                return ExitJoinFailed;
            }

            if (joined.JoinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
            {
                Leg(3, "room", false, "the join said ok and issued no usable ticket");
                return ExitJoinFailed;
            }

            Leg(3, "room", true,
                $"joined '{target.Name}' (room {target.RoomId}) as player 2; ticket for " +
                $"{joined.GameServerIp}:{joined.GameServerPort}, {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 4: ready, then wait for the OTHER player to ready too ----------------
            var seen = new List<RoomLifecycleState>();
            partner.OnRoomStatePush += state => Record(seen, state.Lifecycle);

            stopwatch.Restart();
            await PumpVoidAsync(partner, partner.SetReadyAsync(true, ct), ct).ConfigureAwait(false);
            Console.WriteLine("       (player 2 is ready; waiting for the human to press READY on the room lobby)");

            bool started = await PumpUntilAsync(
                all, () => seen.Contains(RoomLifecycleState.Starting), options.ReadyWaitSeconds, ct)
                .ConfigureAwait(false);

            if (!started)
            {
                Leg(4, "ready", false,
                    $"no Starting push within {options.ReadyWaitSeconds}s. Saw [{Describe(seen)}]. " +
                    "Either the other player never marked ready, or the room's ready rule did not fire.");
                return ExitNeverStarted;
            }

            Leg(4, "ready", true, $"Starting observed {stopwatch.ElapsedMilliseconds} ms after this ready");

            // ---- the ticket is re-requested, because the shipped client re-requests --------
            //
            // A ticket lives JoinTicket.ValidityMs (60s) from the moment it is minted, and the
            // wait here is a HUMAN pressing READY on another machine -- unbounded by
            // construction. The ticket from leg 3 is therefore routinely expired by the time
            // the room starts, and the game server answers InvalidTicket.
            //
            // MasterSession.EnterMatchWithFreshTicketAsync does exactly this on the Starting
            // push, so a harness that dialled with the stale ticket would be failing where the
            // real client succeeds -- and would report it as a product defect. It also means
            // this leg now EXERCISES P16's ticket-refresh path: the master treats a re-join
            // from an existing member as a refresh rather than answering AlreadyInAnotherRoom.
            //
            // Observed before this was added: leg 5 refused with InvalidTicket after a 62s
            // wait, against a client that had entered the same match without trouble.
            JoinResult refreshed = await PumpAsync(
                partner, partner.JoinRoomAsync(target.RoomId, null, ct), ct).ConfigureAwait(false);

            if (!refreshed.Ok || refreshed.JoinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
            {
                Leg(5, "match", false,
                    $"the ticket refresh on Starting was refused, errorCode {refreshed.ErrorCode} - " +
                    ExplainJoinError(refreshed.ErrorCode));
                return ExitJoinFailed;
            }

            joined = refreshed;

            // ---- leg 5: in the match ------------------------------------------------------
            stopwatch.Restart();
            UdpOutcome outcome = await DialAsync(
                game, joined.GameServerIp, joined.GameServerPort, joined.JoinTicket, options, ct)
                .ConfigureAwait(false);

            if (!outcome.Connected || !outcome.ReceivedPayload)
            {
                Leg(5, "match", false, outcome.Connected ? $"connected, received NOTHING ({outcome.Detail})" : outcome.Detail);
                return ExitUdpFailed;
            }

            Leg(5, "match", true,
                $"in the match, {outcome.PayloadsReceived} payload(s) in {stopwatch.ElapsedMilliseconds} ms");

            Console.WriteLine();
            Console.WriteLine("E2E PARTNER PASS - joined a room made from the UI, readied, and was carried into the match.");
            return ExitPass;
        }

        private static async Task<int> WalkRoomStartAsync(Options options, CancellationToken ct)
        {
            using var host    = new Ironfront.MasterClient.MasterClient();
            using var alpha   = new Ironfront.MasterClient.MasterClient();
            using var beta    = new Ironfront.MasterClient.MasterClient();
            using var gameA   = new UdpTransportClient();
            using var gameB   = new UdpTransportClient();

            IMasterClient[] all = { host, alpha, beta };

            // The master answers a refused SetReady with an ERROR_PUSH and nothing else — no
            // return value, because SetReadyAsync is a send rather than a request. A harness
            // that does not subscribe therefore reports "the ready rule did not fire" for a
            // request the master rejected outright, which is a different bug entirely and sends
            // the reader to the wrong file.
            string[] labels = { "host", "alpha", "beta" };
            for (int i = 0; i < all.Length; i++)
            {
                string label = labels[i];
                all[i].OnError += (code, message) =>
                    Console.WriteLine($"       (master error to {label}: {code} {message})");
            }

            // ---- leg 1: the master is reachable ------------------------------------------
            var stopwatch = Stopwatch.StartNew();
            try
            {
                foreach (IMasterClient client in all)
                    await client.ConnectAsync(options.MasterHost, options.MasterPort, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(1, "master", false, $"could not reach {options.MasterHost}:{options.MasterPort} — {ex.Message}");
                return ExitMasterUnreachable;
            }

            Leg(1, "master", true, $"three connections in {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 2: three accounts log in --------------------------------------------
            stopwatch.Restart();
            string hostName  = options.RoomStartHostUsername;
            string alphaName = options.RoomStartAlphaUsername;
            string betaName  = options.RoomStartBetaUsername;

            try
            {
                await LoginAsync(host,  hostName,  ct).ConfigureAwait(false);
                await LoginAsync(alpha, alphaName, ct).ConfigureAwait(false);
                await LoginAsync(beta,  betaName,  ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(2, "login", false, ex.Message);
                return ExitLoginFailed;
            }

            Leg(2, "login", true, $"{hostName}, {alphaName}, {betaName} in {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 3: a room, and a signed ticket for each player -----------------------
            stopwatch.Restart();
            int roomId;
            JoinResult joinedA;
            JoinResult joinedB;
            try
            {
                CreateRoomResult created = await PumpAsync(
                    host,
                    host.CreateRoomAsync(
                        new CreateRoomRequest
                        {
                            Name = options.RoomStartRoomName,
                            MapId = options.MapId,
                            MaxPlayers = options.RoomStartMaxPlayers,
                        },
                        ct),
                    ct).ConfigureAwait(false);

                if (!created.Ok)
                    throw new InvalidOperationException($"the host could not create a room (errorCode {created.ErrorCode})");

                roomId  = created.RoomId;
                joinedA = await PumpAsync(alpha, alpha.JoinRoomAsync(roomId, null, ct), ct).ConfigureAwait(false);
                joinedB = await PumpAsync(beta,  beta.JoinRoomAsync(roomId, null, ct),  ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Leg(3, "room", false, $"could not seat two players — {ex.Message}");
                return ExitJoinFailed;
            }

            if (!joinedA.Ok || !joinedB.Ok)
            {
                int code = joinedA.Ok ? joinedB.ErrorCode : joinedA.ErrorCode;
                Leg(3, "room", false, $"a join was refused, errorCode {code} — {ExplainJoinError(code)}");
                return ExitJoinFailed;
            }

            if (joinedA.JoinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE ||
                joinedB.JoinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
            {
                Leg(3, "room", false, "a join said ok and issued no usable ticket");
                return ExitJoinFailed;
            }

            Leg(3, "room", true, $"room {roomId} holds 3 members; both players hold a signed ticket for " +
                                 $"{joinedA.GameServerIp}:{joinedA.GameServerPort}, {stopwatch.ElapsedMilliseconds} ms");

            // ---- leg 4: ready, and nothing else, until the room says Starting -------------
            var seenByAlpha = new List<RoomLifecycleState>();
            var seenByBeta  = new List<RoomLifecycleState>();
            alpha.OnRoomStatePush += state => Record(seenByAlpha, state.Lifecycle);
            beta.OnRoomStatePush  += state => Record(seenByBeta,  state.Lifecycle);

            stopwatch.Restart();
            await PumpVoidAsync(host,  host.SetReadyAsync(true, ct),  ct).ConfigureAwait(false);
            await PumpVoidAsync(alpha, alpha.SetReadyAsync(true, ct), ct).ConfigureAwait(false);
            await PumpVoidAsync(beta,  beta.SetReadyAsync(true, ct),  ct).ConfigureAwait(false);

            Console.WriteLine($"       (all three ready; the master holds the countdown — no client does)");

            bool started = await PumpUntilAsync(
                all,
                () => seenByAlpha.Contains(RoomLifecycleState.Starting)
                   && seenByBeta.Contains(RoomLifecycleState.Starting),
                options.ReadyWaitSeconds,
                ct).ConfigureAwait(false);

            if (!started)
            {
                Leg(4, "ready", false,
                    $"no Starting push within {options.ReadyWaitSeconds}s. " +
                    $"alpha saw [{Describe(seenByAlpha)}], beta saw [{Describe(seenByBeta)}]. " +
                    "The ready rule did not fire, or the state was not broadcast.");
                return ExitNeverStarted;
            }

            Leg(4, "ready", true, $"both clients observed Starting {stopwatch.ElapsedMilliseconds} ms after " +
                                  "the last ready — no key press, no debug button");

            // ---- leg 5: both dial in, and the master is told the match began ---------------
            stopwatch.Restart();
            UdpOutcome outcomeA = await DialAsync(
                gameA, joinedA.GameServerIp, joinedA.GameServerPort, joinedA.JoinTicket, options, ct)
                .ConfigureAwait(false);
            UdpOutcome outcomeB = await DialAsync(
                gameB, joinedB.GameServerIp, joinedB.GameServerPort, joinedB.JoinTicket, options, ct)
                .ConfigureAwait(false);

            if (!outcomeA.Connected || !outcomeB.Connected)
            {
                Leg(5, "match", false,
                    $"alpha: {(outcomeA.Connected ? "in" : outcomeA.Detail)}; " +
                    $"beta: {(outcomeB.Connected ? "in" : outcomeB.Detail)}");
                return ExitUdpFailed;
            }

            if (!outcomeA.ReceivedPayload || !outcomeB.ReceivedPayload)
            {
                // Named per client, not "at least one". They fail for different reasons — a
                // refused body, a room the server will not adopt, a server simulating nothing —
                // and a message that will not say which one went quiet sends the reader to the
                // wrong half of the log.
                Leg(5, "match", false,
                    $"alpha {(outcomeA.ReceivedPayload ? $"received {outcomeA.PayloadsReceived}" : "received NOTHING")}"
                    + $" ({outcomeA.Detail}); "
                    + $"beta {(outcomeB.ReceivedPayload ? $"received {outcomeB.PayloadsReceived}" : "received NOTHING")}"
                    + $" ({outcomeB.Detail})");
                return ExitUdpFailed;
            }

            // Both are connected, so the match machine has its two humans and will leave Warmup
            // for Playing. That entry is what sends GsMatchStarted, and the master turns it into
            // InMatch only if the room the server names is the room it allocated.
            bool inMatch = await PumpUntilAsync(
                all,
                () => seenByAlpha.Contains(RoomLifecycleState.InMatch)
                   && seenByBeta.Contains(RoomLifecycleState.InMatch),
                options.MatchWaitSeconds,
                ct,
                gameA,
                gameB).ConfigureAwait(false);

            if (!inMatch)
            {
                Leg(5, "match", false,
                    $"both players are in and receiving snapshots, but no InMatch push arrived within " +
                    $"{options.MatchWaitSeconds}s. alpha saw [{Describe(seenByAlpha)}], beta saw " +
                    $"[{Describe(seenByBeta)}]. Either the server never entered Playing, or its " +
                    "GsMatchStarted named a room the master did not allocate it — which " +
                    "HandleMatchStarted drops in silence.");
                return ExitNeverInMatch;
            }

            Leg(5, "match", true,
                $"alpha connectionId {outcomeA.ConnectionId} playerId {outcomeA.MyPlayerId}, " +
                $"beta connectionId {outcomeB.ConnectionId} playerId {outcomeB.MyPlayerId}; " +
                $"room reached InMatch in {stopwatch.ElapsedMilliseconds} ms");

            Console.WriteLine();
            Console.WriteLine($"       alpha room-state sequence: {Describe(seenByAlpha)}");
            Console.WriteLine($"       beta  room-state sequence: {Describe(seenByBeta)}");
            Console.WriteLine();
            Console.WriteLine("E2E ROOM-START PASS — two players marked ready and were carried into a live " +
                              "match by the room's own state push.");
            return ExitPass;
        }

        /// <summary>Appends a lifecycle value, collapsing the repeats a re-broadcast produces.</summary>
        private static void Record(List<RoomLifecycleState> seen, RoomLifecycleState state)
        {
            if (seen.Count > 0 && seen[^1] == state) return;
            seen.Add(state);
        }

        private static string Describe(List<RoomLifecycleState> seen)
            => seen.Count == 0 ? "nothing" : string.Join(" -> ", seen);

        /// <summary>
        /// <see cref="PumpAsync{T}"/> for the calls that answer with an acknowledgement rather
        /// than a value. <c>SetReadyAsync</c> is one, and it is the only call the room-start
        /// walk makes after the joins.
        /// </summary>
        private static async Task PumpVoidAsync(IMasterClient client, Task task, CancellationToken ct)
        {
            int yields = 0;
            while (!task.IsCompleted)
            {
                client.Poll();
                if (yields++ < PollYieldsBeforeSleeping) await Task.Yield();
                else await Task.Delay(1, ct).ConfigureAwait(false);
            }

            client.Poll();
            await task.ConfigureAwait(false);
        }

        /// <summary>Registers (ignoring "already exists") and then logs in, which is the assertion.</summary>
        private static async Task LoginAsync(IMasterClient client, string username, CancellationToken ct)
        {
            try
            {
                await PumpAsync(client, client.RegisterAsync(username, PasswordHash, username, ct), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A re-run against a surviving database. Login is what has to work.
            }

            LoginResult login = await PumpAsync(client, client.LoginAsync(username, PasswordHash, ct), ct)
                .ConfigureAwait(false);

            if (!login.Ok)
                throw new InvalidOperationException($"'{username}' could not log in (errorCode {login.ErrorCode})");
        }

        /// <summary>
        /// Polls every client until <paramref name="condition"/> holds or the budget runs out.
        /// </summary>
        /// <remarks>
        /// The UDP clients are polled too, and must be: a game server drops a peer that stops
        /// acknowledging, so a walk that waited on a TCP push while ignoring its own UDP
        /// connections would be timed out by the very server it is waiting to hear about.
        /// </remarks>
        private static async Task<bool> PumpUntilAsync(
            IMasterClient[] clients,
            Func<bool> condition,
            int budgetSeconds,
            CancellationToken ct,
            params UdpTransportClient[] transports)
        {
            var deadline = Stopwatch.StartNew();
            var budget = TimeSpan.FromSeconds(budgetSeconds);

            while (deadline.Elapsed < budget)
            {
                for (int i = 0; i < clients.Length; i++) clients[i].Poll();
                for (int i = 0; i < transports.Length; i++) transports[i].Poll();
                if (condition()) return true;
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            for (int i = 0; i < clients.Length; i++) clients[i].Poll();
            return condition();
        }

        /// <summary>
        /// Returns a room the walking account can join: an existing one, or one the separate
        /// host account creates and then stays in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reusing an existing room matters on a re-run: creating a second room every time
        /// leaves the master carrying dead lobbies, and a matchmaker with a preferred-map
        /// filter then has more rooms than servers.
        /// </para>
        /// <para>
        /// <b>The host stays connected for the rest of the walk, and must.</b>
        /// <c>LobbyService.LeaveRoom</c> deletes a room the moment its last member leaves, and
        /// a disconnect leaves. If the host dropped after creating, the room would be gone
        /// before the walking account's join reached it and the failure would read as
        /// RoomNotFound — a confusing way to be told the harness hung up on itself.
        /// </para>
        /// </remarks>
        private static async Task<int> ResolveRoomAsync(
            IMasterClient master, IMasterClient host, Options options, CancellationToken ct)
        {
            RoomInfo[] rooms = await PumpAsync(master, master.GetRoomsAsync(ct), ct).ConfigureAwait(false);
            RoomInfo? existing = rooms.FirstOrDefault();
            if (existing is not null && existing.RoomId != 0)
            {
                Console.WriteLine($"       (joining the room {existing.RoomId} that already exists)");
                return existing.RoomId;
            }

            await host.ConnectAsync(options.MasterHost, options.MasterPort, ct).ConfigureAwait(false);

            try
            {
                await PumpAsync(host, host.RegisterAsync(options.HostUsername, PasswordHash, options.HostUsername, ct), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already registered from an earlier run against a surviving database. Login is
                // what has to work.
            }

            LoginResult hostLogin = await PumpAsync(
                host, host.LoginAsync(options.HostUsername, PasswordHash, ct), ct).ConfigureAwait(false);

            if (!hostLogin.Ok)
                throw new InvalidOperationException(
                    $"the host account '{options.HostUsername}' could not log in (errorCode {hostLogin.ErrorCode})");

            CreateRoomResult created = await PumpAsync(
                host,
                host.CreateRoomAsync(
                    new CreateRoomRequest { Name = options.RoomName, MapId = options.MapId, MaxPlayers = 16 },
                    ct),
                ct).ConfigureAwait(false);

            if (!created.Ok)
                throw new InvalidOperationException($"the host could not create a room (errorCode {created.ErrorCode})");

            Console.WriteLine($"       (host '{options.HostUsername}' created and is holding room {created.RoomId})");
            return created.RoomId;
        }

        /// <summary>Dials the game server and pumps the transport until it settles or times out.</summary>
        private static async Task<UdpOutcome> DialAsync(
            UdpTransportClient game,
            string host,
            int port,
            byte[] ticket,
            Options options,
            CancellationToken ct)
        {
            var outcome = new UdpOutcome();
            var stopwatch = Stopwatch.StartNew();

            ConnectResult connectResult = default;
            bool connected = false;
            bool disconnected = false;
            DisconnectReason reason = DisconnectReason.LocalRequest;
            int payloads = 0;

            game.OnConnected += r => { connectResult = r; connected = true; };
            game.OnDisconnected += r => { reason = r; disconnected = true; };
            game.OnMessage += _ => payloads++;

            try
            {
                game.Connect(host, port, ticket);
            }
            catch (Exception ex)
            {
                outcome.Detail = $"the dial threw before a packet left — {ex.Message}";
                return outcome;
            }

            // Phase one: wait for the handshake to resolve either way.
            var handshakeBudget = TimeSpan.FromSeconds(options.ConnectWaitSeconds);
            while (!connected && !disconnected && stopwatch.Elapsed < handshakeBudget)
            {
                game.Poll();
                await Task.Delay(5, ct).ConfigureAwait(false);
            }

            if (disconnected && !connected)
            {
                outcome.Detail = $"refused with {reason}";
                return outcome;
            }

            if (!connected)
            {
                outcome.Detail = $"no answer from {host}:{port} within {options.ConnectWaitSeconds}s " +
                                 "(the port is closed, filtered, or the server is not listening)";
                return outcome;
            }

            outcome.Connected = true;
            outcome.ConnectionId = connectResult.ConnectionId;
            outcome.MapId = connectResult.MapId;
            outcome.MyPlayerId = connectResult.MyPlayerId;

            // Phase two: a handshake is not a match. Wait for the server to actually send.
            var payloadDeadline = stopwatch.Elapsed + TimeSpan.FromSeconds(options.PayloadWaitSeconds);
            while (payloads == 0 && !disconnected && stopwatch.Elapsed < payloadDeadline)
            {
                game.Poll();
                await Task.Delay(5, ct).ConfigureAwait(false);
            }

            game.Poll();

            outcome.PayloadsReceived = payloads;
            outcome.ReceivedPayload = payloads > 0;
            outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;

            if (disconnected && payloads == 0)
                outcome.Detail = $"admitted, then dropped with {reason} before sending anything";

            return outcome;
        }

        /// <summary>
        /// Awaits a master-client task while pumping <c>Poll()</c>, because that is where the
        /// continuation runs.
        /// </summary>
        private static async Task<T> PumpAsync<T>(IMasterClient client, Task<T> task, CancellationToken ct)
        {
            int yields = 0;
            while (!task.IsCompleted)
            {
                client.Poll();
                if (yields++ < PollYieldsBeforeSleeping) await Task.Yield();
                else await Task.Delay(1, ct).ConfigureAwait(false);
            }

            client.Poll();
            return await task.ConfigureAwait(false);
        }

        /// <summary>
        /// Turns a refusal code into the thing an operator should go and look at.
        /// </summary>
        /// <remarks>
        /// Written after this harness reported "3=NoGameServerAvailable" for a 2004, which is
        /// AlreadyInAnotherRoom and sent the reader off to check a game server that was fine.
        /// A guessed legend is worse than a bare number: the number at least makes you look it
        /// up. These are <c>ErrorCode</c> in Ironfront.Net.Protocol.
        /// </remarks>
        private static string ExplainJoinError(int errorCode) => errorCode switch
        {
            2000 => "RoomNotFound: the room went away between the listing and the join. If the harness " +
                    "created it, the host account disconnected and emptied it.",
            2001 => "RoomFull",
            2002 => "WrongRoomPassword",
            2003 => "MatchAlreadyStarted",
            2004 => "AlreadyInAnotherRoom: this account is already a member, so it cannot join. " +
                    "Creating a room joins you to it — that is why a separate host account holds it.",
            3000 => "NoGameServerAvailable: no registered game server advertises this room's map. " +
                    "Check IRONFRONT_GAMESERVER_MAP_IDS on the server and the room's mapId here.",
            3001 => "GameServerNotResponding: a server was allocated but has stopped heartbeating.",
            9001 => "RateLimited",
            _    => "see ErrorCode in Ironfront.Net.Protocol",
        };

        private static void Leg(int number, string name, bool ok, string detail)
        {
            string verdict = ok ? "OK  " : "FAIL";
            Console.WriteLine($"  [{number}/4] {name,-6} {verdict}  {detail}");
        }

        private sealed class UdpOutcome
        {
            public bool Connected;
            public bool ReceivedPayload;
            public int PayloadsReceived;
            public ushort ConnectionId;
            public ushort MapId;
            public uint MyPlayerId;
            public long ElapsedMs;
            public string Detail = string.Empty;
        }

        private sealed class Options
        {
            public string MasterHost { get; private set; } = "127.0.0.1";
            public int MasterPort { get; private set; } = 27000;
            public string Username { get; private set; } = "e2e_walker";

            /// <summary>The account that creates the room and sits in it. See ResolveRoomAsync.</summary>
            public string HostUsername { get; private set; } = "e2e_host";
            public string RoomName { get; private set; } = "e2e";

            /// <summary>
            /// Join the room that ALREADY EXISTS, ready up, and be the second player in
            /// somebody else's match. P16 criterion 2.
            /// </summary>
            /// <remarks>
            /// The other walks are self-contained: they create every account and every room
            /// they use, which is what makes them a gate and also what makes them unable to
            /// grade a UI. Criterion 2 asks whether a HUMAN driving the shipped screens is
            /// joined by a second player, sees them on the roster, and is carried into the
            /// match -- and no harness that insists on creating its own room can stand on the
            /// other side of that. This mode supplies the second player and nothing else.
            /// </remarks>
            public bool Partner { get; private set; }
            public ushort MapId { get; private set; } = 1;
            public int TimeoutSeconds { get; private set; } = 90;
            public int ConnectWaitSeconds { get; private set; } = 15;
            public int PayloadWaitSeconds { get; private set; } = 20;
            public bool Negative { get; private set; }
            public bool ShowHelp { get; private set; }

            /// <summary>The P14 walk: two players ready themselves into a match. Criterion 2.</summary>
            public bool RoomStart { get; private set; }

            /// <summary>
            /// Accounts and room for <see cref="RoomStart"/>, deliberately distinct from the
            /// single-account walk's. Sharing them would make the mode order-dependent: the
            /// host would already be in the other room, and answer AlreadyInAnotherRoom.
            /// </summary>
            public string RoomStartHostUsername { get; private set; } = "e2e_rs_host";
            public string RoomStartAlphaUsername { get; private set; } = "e2e_rs_alpha";
            public string RoomStartBetaUsername { get; private set; } = "e2e_rs_beta";
            public string RoomStartRoomName { get; private set; } = "e2e room start";

            /// <summary>
            /// Seats the created room asks for. Odd on purpose is worth trying by hand: the
            /// master rounds it down and the lobby then advertises the number it will honour.
            /// </summary>
            public byte RoomStartMaxPlayers { get; private set; } = 8;

            /// <summary>
            /// Budget for the Starting push after the last ready. Must clear the master's
            /// countdown (10s by default) with room to spare, or this grades the clock rather
            /// than the rule.
            /// </summary>
            public int ReadyWaitSeconds { get; private set; } = 45;

            /// <summary>
            /// Budget for InMatch after both players are connected. It has to clear the match
            /// machine's warmup, which is 20s on the authored controller.
            /// </summary>
            public int MatchWaitSeconds { get; private set; } = 90;

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-h" or "--help": options.ShowHelp = true; return options;
                        case "--negative": options.Negative = true; break;
                        case "--room-start": options.RoomStart = true; break;
                        case "--partner": options.Partner = true; break;
                        case "--room-start-seats": options.RoomStartMaxPlayers = byte.Parse(Next(args, ref i)); break;
                        case "--ready-wait": options.ReadyWaitSeconds = int.Parse(Next(args, ref i)); break;
                        case "--match-wait": options.MatchWaitSeconds = int.Parse(Next(args, ref i)); break;
                        case "--master-host": options.MasterHost = Next(args, ref i); break;
                        case "--master-port": options.MasterPort = int.Parse(Next(args, ref i)); break;
                        case "--username": options.Username = Next(args, ref i); break;
                        case "--host-username": options.HostUsername = Next(args, ref i); break;
                        case "--room-name": options.RoomName = Next(args, ref i); break;
                        case "--map-id": options.MapId = ushort.Parse(Next(args, ref i)); break;
                        case "--timeout": options.TimeoutSeconds = int.Parse(Next(args, ref i)); break;
                        case "--connect-wait": options.ConnectWaitSeconds = int.Parse(Next(args, ref i)); break;
                        case "--payload-wait": options.PayloadWaitSeconds = int.Parse(Next(args, ref i)); break;
                        default: throw new ArgumentException($"unknown argument '{args[i]}'");
                    }
                }

                return options;
            }

            private static string Next(string[] args, ref int i)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"'{args[i]}' needs a value");
                return args[++i];
            }

            public static void PrintUsage() => Console.Out.WriteLine(@"Ironfront end-to-end walk (M2 criterion 14)

  Connects to a master, logs one account in, joins a room, and dials the game server
  the master allocated over UDP with the signed ticket it issued. Exits 0 only when all
  four legs pass.

  --master-host <host>     default 127.0.0.1
  --master-port <port>     default 27000
  --username <name>        default e2e_walker (3-16 chars, [a-z0-9_])
  --host-username <name>   the account that creates and holds the room; default e2e_host
  --room-name <name>       room to create when none exists; default e2e
  --map-id <id>            map for a created room; default 1
  --timeout <sec>          budget for the whole walk; default 90
  --connect-wait <sec>     budget for the UDP handshake; default 15
  --payload-wait <sec>     budget for the first snapshot after it; default 20
  --negative               corrupt the ticket and REQUIRE the game server to refuse it
  --help                   this text

  --room-start             the P14 walk instead: three accounts sit in one room, all mark
                           ready, and the two players are carried into a live match by the
                           master's room-state push. After the joins it calls NOTHING but
                           SetReady — no enter-match call, no debug button.
  --room-start-seats <n>   seats the created room asks for; default 8. An odd number is
                           worth trying: the master rounds it down and advertises the even one
  --ready-wait <sec>       budget for the Starting push after the last ready; default 45
                           (the master's countdown is 10)
  --match-wait <sec>       budget for InMatch after both players connect; default 90
                           (it has to clear the match machine's 20s warmup)

  Exit: 0 pass · 1 master unreachable · 2 login failed · 3 join failed · 4 UDP failed
        5 never reached Starting · 6 never reached InMatch · 64 usage

  Orchestrated by tools/run-e2e.ps1, which stands up the master and game server first
  and runs the negative case as well as the positive one.");
        }
    }
}
