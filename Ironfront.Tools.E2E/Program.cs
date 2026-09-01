using System;
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
                return await WalkAsync(options, cts.Token).ConfigureAwait(false);
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
            public ushort MapId { get; private set; } = 1;
            public int TimeoutSeconds { get; private set; } = 90;
            public int ConnectWaitSeconds { get; private set; } = 15;
            public int PayloadWaitSeconds { get; private set; } = 20;
            public bool Negative { get; private set; }
            public bool ShowHelp { get; private set; }

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-h" or "--help": options.ShowHelp = true; return options;
                        case "--negative": options.Negative = true; break;
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

  Exit: 0 pass · 1 master unreachable · 2 login failed · 3 join failed · 4 UDP failed · 64 usage

  Orchestrated by tools/run-e2e.ps1, which stands up the master and game server first
  and runs the negative case as well as the positive one.");
        }
    }
}
