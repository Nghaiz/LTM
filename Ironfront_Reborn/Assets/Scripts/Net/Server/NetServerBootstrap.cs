using System;
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
        /// The join-ticket validator, or null when running with unsigned tickets. The
        /// connection lifecycle needs it to confirm and release player claims.
        /// </summary>
        public TicketValidator Validator { get; private set; }

        private void Awake()
        {
            TickLoop = GetComponent<ServerTickLoop>();

            ResolveConfiguration();

            NetContext.SetRole(NetRole.Server);
            Time.maximumDeltaTime = MaxDeltaTime;

            // Only in a real headless run. Capping the Editor to 30 fps would make the client track's
            // two-client test miserable to watch for no benefit, and vSync is meaningless
            // without a display.
            if (Application.isBatchMode)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = ProtocolConstants.SIM_TICK_RATE * 2;
            }

            // NOT set here, deliberately: Time.fixedDeltaTime. Decision A5 chose option B — the
            // netcode owns its own 30 Hz accumulator and the physics rate is left alone. Forcing
            // 1/30 here would also lose the argument anyway: IngameMenuUi.cs:29 and
            // FpsActorController.cs:497 both assign Time.timeScale / 60f at runtime, so the
            // value would be overwritten before the first physics step.

            if (_startOnAwake) StartServer();
        }

        private void OnDestroy() => StopServer();

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
                udp.Start(Config.UdpPort, Config.MaxConnections);

                // Null clock pump: a real socket runs on the wall clock and needs no advancing.
                TickLoop.Bind(udp, null);

                Debug.Log($"[net] server up on UDP :{Config.UdpPort}, {Config.MaxConnections} slots");
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

            Debug.Log($"[net] server up on the loopback wire, {Config.MaxConnections} slots");
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

            // The ticket names a serverId, and the validator only enforces it once we have been
            // told our own — which is GS_REGISTER's answer and arrives later, if at all. 0 here
            // means "signature and expiry only", which is the correct standalone behaviour;
            // ServerMasterReporter re-registers a stricter validator once it has an id.
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
