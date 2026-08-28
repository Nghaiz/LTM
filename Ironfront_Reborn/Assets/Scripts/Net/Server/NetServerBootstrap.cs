using System;
using System.Net.Sockets;
using System.Text;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Loopback;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Brings the authoritative server up: declares the role, sets the two engine knobs the
    /// tick loop depends on, creates a transport, and binds the loop to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At execution order -1000 so the role is set before any component's <c>Awake</c> can
    /// read it. Anything that must not run on a headless server checks
    /// <see cref="NetContext.IsServer"/>, and a check that runs before the role is assigned
    /// answers "client" on the server, once, on the first frame.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ServerTickLoop))]
    public sealed class NetServerBootstrap : MonoBehaviour
    {
        /// <summary>
        /// The ceiling on one frame's simulated time (phase-01 trap 2).
        /// </summary>
        /// <remarks>
        /// Without it, a 40 ms tick leaves Unity owing two FixedUpdates; running both takes
        /// 80 ms, which owes three, and the server never catches up — it just falls further
        /// behind while working harder. 0.1 s discards the backlog instead, visibly. The
        /// scheduler enforces the same bound on the netcode tick independently, because the
        /// two clocks are no longer the same clock.
        /// </remarks>
        public const float MaxDeltaTime = 0.1f;

        /// <summary>
        /// Environment variable holding the HMAC key that signs join tickets, shared with the
        /// master server (protocol-spec.md section 12).
        /// </summary>
        /// <remarks>
        /// From the environment, never from a serialized field or a committed file: a secret in
        /// a scene asset is a secret in the repository, and the whole point of the ticket is
        /// that a game server operated by a third party can verify one without holding a login
        /// credential. The name matches the phase-03 task-4 sketch.
        /// </remarks>
        public static readonly string SharedSecretVariable = EnvRegistry.SharedSecret.Name;

        [Header("Startup")]
        [SerializeField] private bool _startOnAwake = true;

        [Header("Transport")]
        [Tooltip("Defaults, all overridable from the environment. In-process wire with no socket, for a single-Editor test. Off = real UDP.")]
        [SerializeField] private bool _useLoopbackTransport = true;

        [Tooltip("Accept any join ticket when no shared secret is configured. Development only.")]
        [SerializeField] private bool _acceptUnsignedTickets = true;

        [SerializeField] private int _port = 27015;
        [SerializeField] private int _maxConnections = 16;

        [Header("Diagnostics")]
        [Tooltip("Seconds between overload checks. 0 disables the warning.")]
        [SerializeField] private float _overloadCheckInterval = 5f;

        private float _nextOverloadCheck;
        private bool _ownsTransport;
        private bool _misconfigured;

        // connectionId -> the playerId its join ticket named, so a disconnect releases the
        // right claim regardless of the order clients leave in.
        private readonly System.Collections.Generic.Dictionary<ushort, uint> _playerByConnection =
            new System.Collections.Generic.Dictionary<ushort, uint>(ProtocolConstants.MAX_PLAYERS);

        /// <summary>
        /// The settings this server actually resolved: the inspector fields with any
        /// <c>IRONFRONT_*</c> variable layered on top.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every field above used to be the last word, which meant a headless build's port,
        /// slot count and transport lived in a scene asset — running a second instance on one
        /// host meant editing the scene and rebuilding it. Now the scene carries the
        /// developer's convenient defaults and the deployment carries the truth.
        /// </para>
        /// <para>
        /// It is also where the UDP port stopped being two numbers.
        /// <see cref="MasterLinkBootstrap"/> had its own copy of the port and the player count
        /// to advertise, free to disagree with the ones actually bound here; both now read
        /// the same variables through the same object.
        /// </para>
        /// </remarks>
        public GameServerConfig Config { get; private set; }

        /// <summary>The loopback wire, when one was created. Hand <c>.Client</c> to a test client.</summary>
        public LoopbackTransport Loopback { get; private set; }

        /// <summary>The UDP server, when one was created.</summary>
        public UdpTransportServer Udp { get; private set; }

        /// <summary>The tick loop this bootstrap drives.</summary>
        public ServerTickLoop TickLoop { get; private set; }

        /// <summary>
        /// The claimable bodies this server hands to joining connections, one per transport
        /// slot. Phase-3A.
        /// </summary>
        public ServerPlayerSlotPool SlotPool { get; } = new ServerPlayerSlotPool();

        /// <summary>
        /// The join-ticket validator, or null when running with unsigned tickets. The
        /// connection lifecycle needs it to confirm and release player claims.
        /// </summary>
        public TicketValidator Validator { get; private set; }

        private void Awake()
        {
            // Defect 2 of the lane-B report: until this call the transport's warnings went to a
            // null delegate in every shipped build. Installed FIRST, so anything the rest of this
            // Awake logs is already reaching somewhere.
            NetLogUnitySink.Install();

            TickLoop = GetComponent<ServerTickLoop>();

            // ABOVE the declared-client guard, deliberately. This caps how much time one frame
            // may simulate, and it is an ENGINE knob rather than server startup: a client that
            // hitches owes the same physics backlog a server does, and its prediction re-simulates
            // that backlog. Every client set it before the guard below existed, so leaving it under
            // the guard would have quietly restored Unity's 0.333 s default on exactly the
            // processes this change was about -- a physics behaviour change nobody asked for,
            // shipped inside a networking fix.
            Time.maximumDeltaTime = MaxDeltaTime;

            // The mirror of NetClientBootstrap's dedicated-server guard, and the other half of
            // AD-1 ("server-authoritative, no host/listen-server"). X-50 stopped a headless host
            // dialling itself; this stops a rendered process launched to JOIN a match from
            // hosting one of its own. Measured on tmp/client-1.log + client-2.log, two clients
            // started by tools/play-lan.ps1 against the sandbox server: both logged
            // `[net] role = Client` and then went on to run a full authority anyway -- the first
            // took UDP 27015 and reported `16 player slots will not fit: 51 actors are already
            // registered`, the second threw an unhandled SocketException out of this very Awake
            // because the first already held the port.
            //
            // NOT NetContext.IsClient, for NetContext.IsDeclaredClient's own reason: the ROLE is
            // an Awake ordering between two components that defer to each other, so gating on it
            // would make an Editor Play session stop hosting depending on component order --
            // the race X-9 closed. IsDeclaredClient has one meaning and one setter.
            //
            // ABOVE ResolveConfiguration, like G11's guard and for the same reason: everything
            // below is server startup, and a client has no business parsing a server's port,
            // slot count or shared secret, nor logging a physics rate it is not the authority
            // for. Left ENABLED rather than switched off -- `enabled = false` assigned inside
            // Awake does not survive the activation pass that called it (DedicatedServerDeclines-
            // LocalClientTests measured that), so a line claiming to disable this would lie. It
            // costs nothing: every Update path here returns on a null Transport.
            if (NetContext.IsDeclaredClient)
            {
                Debug.Log("[net] declared client: no local server will be started (AD-1).");
                return;
            }

            ResolveConfiguration();

            // Deference, mirroring NetClientBootstrap's `if (!NetContext.IsServer)`. Dustbowl
            // carries an ACTIVE NetServer and an ACTIVE NetClient, both at -1000, so before this
            // guard existed the role was decided by whichever Awake Unity happened to run
            // second — and the client half always lost, because only the client deferred.
            //
            // That race is not cosmetic: every presenter guarded by
            // NetClientPresenterGuard.IsPresentable latches `enabled = false` during the SAME
            // Awake pass and never re-checks, so a process that becomes a client one callback
            // later still has a dead combat driver and a dead killfeed for the rest of its life.
            // Measured on lane-b/combat-fix01: `[net] role = Server` at driver.log:70,
            // `role = Client` only at :173 — every client Awake in between read "server".
            //
            // A process that has DECLARED itself a client (see LaneBHarness.DeclareRole, which
            // runs at BeforeSceneLoad — ahead of every scene Awake) now wins. With no
            // declaration the role is Offline here and the server still claims it, so the
            // Editor sandbox and the dedicated build behave exactly as they did.
            if (!NetContext.IsClient) NetContext.SetRole(NetRole.Server);

            // Only in a real headless run. Capping the Editor to 30 fps would make the client track's
            // two-client test miserable to watch for no benefit, and vSync is meaningless
            // without a display.
            if (Application.isBatchMode)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = ProtocolConstants.SIM_TICK_RATE * 2;
            }

            // NOT set here, deliberately: Time.fixedDeltaTime. Decision A5 chose option B — the
            // netcode owns its own 30 Hz accumulator and the physics rate is left alone.
            //
            // The second half of that argument is now obsolete and is recorded here rather than
            // deleted, because it was true for a long time: IngameMenuUi and FpsActorController
            // each used to assign `Time.timeScale / 60f` directly, so anything set here would
            // have been overwritten before the first physics step. Both now go through
            // PhysicsRate, which scales the PROJECT SETTING rather than declaring a rate of its
            // own — so there is exactly one number, in TimeManager.asset, and this server and a
            // rendered client no longer disagree about it. Issue #123.
            //
            // Logged rather than assumed. A server whose fixed step drifts from the clients'
            // integrates rigidbodies differently for the same inputs, and that presents as a
            // replication defect several layers away with nothing naming the cause.
            Debug.Log(
                $"[net] physics fixed step {Time.fixedDeltaTime * 1000f:F3} ms "
                + $"({1f / Time.fixedDeltaTime:F1} Hz) — the project setting; the netcode's own "
                + $"tick is {ProtocolConstants.SIM_TICK_RATE} Hz and is unrelated");

            if (_startOnAwake) StartServer();
        }

        /// <summary>
        /// Builds the player slots, once every other component in the scene has awoken.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Start, not Awake, and the execution order is why.</b> This component runs at
        /// -1000 so the role is set before anything can read it, which puts its <c>Awake</c>
        /// ahead of <c>LevelTester</c>'s — and <c>LevelTester</c> is what instantiates the
        /// <c>_Managers</c> prefab that <c>ActorManager.instance</c> comes from. The body
        /// factory needs that instance to reach the AI character prefab, so filling in
        /// <c>Awake</c> would find nothing and build zero slots on a server that starts
        /// perfectly cleanly.
        /// </para>
        /// <para>
        /// Nothing can connect before this runs. The transport is bound in <c>Awake</c>, but
        /// connections are only admitted when the tick loop polls it, and that is
        /// <c>FixedUpdate</c> — after every <c>Start</c>.
        /// </para>
        /// </remarks>
        private void Start()
        {
            if (_misconfigured || TickLoop == null || TickLoop.Transport == null) return;

            FillPlayerSlots();
        }

        private void OnDestroy() => StopServer();

        /// <summary>
        /// Creates one claimable body per admitted connection and reports what actually exists.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The count is read back from the registry, not restated from configuration.</b>
        /// This line used to print <c>Config.MaxConnections</c> — sixteen — beside a world that
        /// contained exactly one claimable body, and no code anywhere compared the two.
        /// <c>ServerActorRegistry.ClaimableCount</c> is what <c>TryClaimPlayerSlot</c> will
        /// actually walk, so a disagreement between the log and the world is now a disagreement
        /// this method reports rather than one it prints.
        /// </para>
        /// </remarks>
        private void FillPlayerSlots()
        {
            SlotPool.Fill(Config.MaxConnections, CreatePlayerBody);

            int claimable = ServerActorRegistry.Instance.ClaimableCount;

            if (claimable != Config.MaxConnections)
            {
                Debug.LogError(
                    $"[net] {claimable} claimable player bodies against {Config.MaxConnections} "
                    + "admitted connections. The server will refuse the difference with "
                    + "ServerFull. This is the phase-3A defect, not a warning about one.");
                return;
            }

            Debug.Log($"[net] {claimable} player slots ready");
        }

        /// <summary>
        /// One player-slot body, built by the game's own spawn path on the far side of the seam.
        /// </summary>
        private static NetServerActor CreatePlayerBody(byte team)
        {
            GameObject body = NetServerBindings.CreatePlayerBody(team);
            if (body == null) return null;

            NetServerActor actor = body.GetComponent<NetServerActor>();

            if (actor == null)
            {
                // Loud, and cleaned up: a body with no NetServerActor is invisible to the
                // registry, so leaving it standing would put an unreplicated character in the
                // map that no client ever sees and every bot can shoot.
                Debug.LogError(
                    $"[net] player body '{body.name}' carries no NetServerActor, so it can "
                    + "never be claimed. Add the component to the AI character prefab.");
                Destroy(body);
                return null;
            }

            return actor;
        }

        /// <summary>
        /// Loads a <c>.env</c> if one is reachable, then layers the environment over the
        /// inspector fields.
        /// </summary>
        /// <remarks>
        /// A malformed value leaves <see cref="_misconfigured"/> set and the server does not
        /// start. Falling back to the inspector default instead would be worse than not
        /// starting: a server told to bind 2705 and quietly binding 27015 is one the
        /// matchmaker keeps sending players who cannot reach it, and nothing in the log says
        /// why.
        /// </remarks>
        private void ResolveConfiguration()
        {
            // A player runs with its working directory set to the build output and the Editor
            // runs from the Unity project folder, so the repository-root .env is above both.
            // Missing file is not an error — a systemd unit sets the environment directly.
            DotEnv.LoadFromAncestors(null, out _);

            var defaults = new GameServerConfig
            {
                UseLoopbackTransport  = _useLoopbackTransport,
                AcceptUnsignedTickets = _acceptUnsignedTickets,
                UdpPort               = _port,
                MaxConnections        = _maxConnections,
            };

            try
            {
                Config = defaults.ApplyEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                _misconfigured = true;
                Config = defaults;

                Debug.LogError($"[net] configuration rejected, the server will not start. {ex.Message}");
            }
        }

        /// <summary>Creates the transport (unless one was injected) and starts the loop.</summary>
        public void StartServer()
        {
            if (_misconfigured) return;
            if (Config == null) ResolveConfiguration();
            if (TickLoop == null) TickLoop = GetComponent<ServerTickLoop>();
            if (TickLoop.Transport != null) return;

            if (!Config.UseLoopbackTransport)
            {
                var udp = new UdpTransportServer();
                _ownsTransport = true;
                Udp = udp;

                RegisterTicketValidator(udp);

                try
                {
                    udp.Start(Config.UdpPort, Config.MaxConnections);
                }
                catch (SocketException ex)
                {
                    // Caught rather than allowed to escape, and the difference is not
                    // cosmetic: this runs from Awake, so an escaping exception abandons the
                    // REST of Awake -- leaving _ownsTransport set, Udp assigned and the tick
                    // loop never bound, a half-built server that OnDestroy then tries to stop.
                    // Observed exactly that in tmp/client-2.log.
                    //
                    // Still an error and still a stop, per errors-over-silent-fallbacks: the
                    // server does not quietly fall back to another port or to loopback, because
                    // a server the matchmaker keeps sending players who cannot reach it is
                    // worse than one that refused to start and said why.
                    _misconfigured = true;
                    Udp = null;
                    _ownsTransport = false;
                    udp.Dispose();

                    Debug.LogError(
                        $"[net] UDP :{Config.UdpPort} could not be bound, so this server will "
                        + $"not start: {ex.Message} Something already holds the port -- another "
                        + "game server on this machine, or a client that is also hosting. "
                        + $"Set {EnvRegistry.GameServerUdpPort.Name} to a free port, or stop the "
                        + "other process.");
                    return;
                }

                // Null clock pump: a real socket runs on the wall clock and needs no advancing.
                TickLoop.Bind(udp, null);

                Debug.Log($"[net] server up on UDP :{Config.UdpPort}, {Config.MaxConnections} connections");
                return;
            }

            Loopback = new LoopbackTransport();
            _ownsTransport = true;

            ITransportServer server = Loopback.Server;
            RegisterTicketValidator(server);
            server.Start(Config.UdpPort, Config.MaxConnections);

            // The loopback clock is virtual and delivers nothing until advanced, so the loop
            // feeds it the real elapsed milliseconds once per fixed step.
            TickLoop.Bind(server, Loopback.Advance);

            Debug.Log($"[net] server up on the loopback wire, {Config.MaxConnections} connections");
        }

        /// <summary>
        /// Installs the join-ticket validator the transport requires before it will accept
        /// anybody.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The transport is fail-closed: with no validator registered it rejects every
        /// connection.</b> That is the right default — it is the HMAC check from
        /// protocol-spec.md section 12, and a server that silently accepted unsigned tickets
        /// would be one nobody noticed was open. But it also means forgetting this call
        /// produces a server that starts cleanly, logs nothing, and turns away every client, so
        /// it is done here rather than left to whoever wires the scene.
        /// </para>
        /// <para>
        /// The transport deliberately does not know the shared secret. Validating the HMAC
        /// belongs to whoever holds it — the master-server integration, which is the master-server track's and is
        /// not built yet. Until then <see cref="_acceptUnsignedTickets"/> accepts any ticket and
        /// says so loudly on every start, because a development shortcut that goes quiet is one
        /// that ships.
        /// </para>
        /// </remarks>
        private void RegisterTicketValidator(ITransportServer server)
        {
            string secret = Environment.GetEnvironmentVariable(SharedSecretVariable);

            if (string.IsNullOrEmpty(secret))
            {
                if (!Config.AcceptUnsignedTickets)
                {
                    Debug.LogError(
                        $"[net] {SharedSecretVariable} is not set and unsigned tickets are "
                        + "disabled, so the server will reject every connection. Set the "
                        + "variable to the master server's shared secret, or set "
                        + $"{EnvRegistry.GameServerAcceptUnsignedTickets.Name}=1 for local testing.");
                    return;
                }

                Debug.LogWarning(
                    "[net] accepting UNSIGNED join tickets. Development only — this bypasses "
                    + "the protocol-spec section 12 HMAC check and must not reach a public "
                    + $"server. Set {SharedSecretVariable} to turn validation on.");

                server.OnValidateTicket += _ => true;
                return;
            }

            // A secret is present, so signed validation is what gets installed -- and the
            // accept-unsigned flag is about to be ignored. Say so.
            //
            // It used to be ignored SILENTLY. The flag is only consulted on the branch above,
            // where the secret is missing; with one present this method installed signed
            // validation and never looked at the flag again. An operator who set it got the
            // opposite of what they asked for, and the only evidence was a per-connection
            // "join rejected: BadSignature" that names the symptom and not the cause. That trail
            // consumed phase 3B and part of #152's. Issue #151.
            //
            // The flag is still ignored rather than honoured, deliberately: a server holding a
            // real secret admitting unsigned tickets is a server anyone can join as anyone, and
            // this method's whole contract is fail-closed. What changes is that the contradiction
            // is now reported at start-up, once, by name.
            if (Config.AcceptUnsignedTickets)
            {
                Debug.LogError(
                    $"[net] {EnvRegistry.GameServerAcceptUnsignedTickets.Name} is set, but "
                    + $"{SharedSecretVariable} is also set, so the flag is IGNORED and every "
                    + "join ticket must carry a valid signature. Unsign an unsigned client by "
                    + $"clearing {SharedSecretVariable}, or sign its ticket -- a Unity client "
                    + $"mints one from {SharedSecretVariable} on its own.");
            }

            // The ticket names a serverId, and the validator only enforces it once we have been
            // told our own — which is GS_REGISTER's answer and arrives later, if at all. 0 here
            // means "signature and expiry only", which is the correct standalone behaviour.
            //
            // This is the ONLY validator anything registers. An earlier remark here claimed
            // ServerMasterReporter re-registers a stricter one once it has a server id; it does
            // not — it subscribes to MatchEnded and nothing else, and no second TicketValidator
            // is constructed anywhere outside the tests. The claim cost phase 3B a hypothesis:
            // because OnValidateTicket is a multicast event whose walk in
            // UdpTransportServer.ValidateTicket refuses if ANY subscriber refuses, a second
            // stricter validator would have been a plausible source of an unexplained
            // BadSignature. Measured at runtime, the subscriber count is 1. If a serverId-aware
            // validator is ever added, decide replace-vs-accumulate deliberately and pin it —
            // an accumulating hook where every subscriber must agree is a footgun.
            Validator = new TicketValidator(Encoding.UTF8.GetBytes(secret), serverId: 0);

            Debug.Log("[net] join-ticket validation ON (HMAC + expiry + one-session-per-player)");

            server.OnValidateTicket += ticket =>
            {
                bool admitted = Validator.TryAdmit(
                    ticket.Span,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    out uint playerId,
                    out TicketRejection reason);

                // Logged here and reported to the client as a bare InvalidTicket, never as the
                // specific reason: a handshake that says which check failed is an oracle for
                // forging a ticket one byte at a time.
                if (!admitted) Debug.LogWarning($"[net] join rejected: {reason}");
                else Debug.Log($"[net] join admitted for player {playerId}");

                return admitted;
            };

            // A connection has to be paired with the player its ticket named, so the claim can
            // be released when that connection goes away.
            //
            // Pair on ConnectionInfo.PlayerId, which the UDP transport reads out of the signed
            // ticket during the handshake (checklist B7). The positional fallback below is for
            // the loopback transport, which admits without a ticket and reports 0. Pairing
            // positionally on a real transport mis-pairs whenever an admitted handshake dies
            // before connecting, and a mis-paired claim is then confirmed as permanent — the
            // real owner cannot rejoin until the other connection drops.
            server.OnClientConnected += (connectionId, info) =>
            {
                uint playerId = info.PlayerId;

                if (playerId != 0)
                {
                    // Drop the matching admission so the pending list does not grow. A miss is
                    // not an error: an unsigned-ticket build admits through OnValidateTicket
                    // without ever recording one.
                    Validator.TryTakePendingAdmission(playerId);
                }
                else if (!Validator.TryTakePendingAdmission(out playerId))
                {
                    return;
                }

                Validator.ConfirmConnected(playerId);
                _playerByConnection[connectionId] = playerId;
            };

            server.OnClientDisconnected += (connectionId, _) =>
            {
                if (!_playerByConnection.TryGetValue(connectionId, out uint playerId)) return;

                _playerByConnection.Remove(connectionId);
                Validator.Release(playerId);
            };
        }

        /// <summary>Unbinds the loop and disposes a transport this component created.</summary>
        public void StopServer()
        {
            SlotPool.Clear();

            if (TickLoop != null) TickLoop.Unbind();

            if (_ownsTransport)
            {
                if (Loopback != null)
                {
                    Loopback.Dispose();
                    Loopback = null;
                }

                if (Udp != null)
                {
                    Udp.Dispose();
                    Udp = null;
                }

                _ownsTransport = false;
            }

            NetContext.Clear();
        }

        private void Update()
        {
            if (_overloadCheckInterval <= 0f || TickLoop == null) return;
            if (Time.unscaledTime < _nextOverloadCheck) return;

            _nextOverloadCheck = Time.unscaledTime + _overloadCheckInterval;

            ServerTickScheduler scheduler = TickLoop.Scheduler;
            if (!scheduler.IsOverloaded()) return;

            Debug.LogWarning(
                $"[net] server over budget: p99 {scheduler.TickTimes.Percentile(99):F1} ms "
                + $"against a {scheduler.MsPerTick:F1} ms tick, {scheduler.DroppedTicks} ticks "
                + "dropped. Shed bots or lower the actor count.");
        }
    }
}
