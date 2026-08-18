using System;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Configuration;
using Ironfront.Net.MasterLink;
using Ironfront.Net.Replication.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Connects this game server to the master and hands the resulting reporter to
    /// <see cref="ServerMasterReporter"/>. Closes checklist item A11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the boot script <see cref="ServerMasterReporter"/> was written to expect.</b>
    /// That component holds an <see cref="IMatchReporter"/> port and defaults to
    /// <see cref="NullMatchReporter"/> — standalone mode, where the server plays complete
    /// matches and is simply not advertised. Its own remarks call the real wiring "a two-line
    /// change and a plugin drop"; the plugin drop landed with
    /// <c>Ironfront.Net.MasterLink.dll</c> and <c>Ironfront.MasterClient.dll</c> in
    /// <c>Assets/Plugins</c>, and this is the two-line change.
    /// </para>
    /// <para>
    /// <b>Standalone stays the default, and stays reachable by construction.</b> Leave
    /// <see cref="_masterHost"/> empty and this component does nothing at all — it does not
    /// connect, does not replace the reporter, and logs one line saying so. The phase-03 risk
    /// table's contingency for the master not being ready is therefore the behaviour you get by
    /// doing nothing, rather than something a null check has to notice on every call.
    /// </para>
    /// <para>
    /// <b>The secret comes from the environment.</b> Same rule and same variable as
    /// <see cref="NetServerBootstrap.SharedSecretVariable"/>: a secret in a scene asset is a
    /// secret in the repository. With no secret set, registration is skipped rather than
    /// attempted with an empty string, because the master would reject it and the log would
    /// read like a network fault instead of a missing configuration.
    /// </para>
    /// <para>
    /// <b>Why a component and not a call inside <c>ServerMasterReporter.Awake</c>.</b> Keeping
    /// the transport-facing half separate is what lets the reporter stay engine-and-network
    /// agnostic, and it is what makes standalone mode free. It also keeps the
    /// <c>System.Text.Json</c> dependency chain that <see cref="GameServerLink"/> drags in
    /// reachable from exactly one file.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    [RequireComponent(typeof(ServerMasterReporter))]
    public sealed class MasterLinkBootstrap : MonoBehaviour
    {
        [Header("Master")]
        [Tooltip("Defaults, all overridable from the environment. Leave empty for standalone mode: no connection, no advertisement, matches still play.")]
        [SerializeField] private string _masterHost = string.Empty;

        [Tooltip("The master's single TCP port. GS_REGISTER shares the MSP connection player logins use — there is no separate registration port.")]
        [SerializeField] private int _masterPort = 27000;

        [Header("What to advertise")]
        [Tooltip("The address clients dial. Empty means the master infers it from the connection.")]
        [SerializeField] private string _publicIp = string.Empty;

        [SerializeField] private int _udpPort = 27015;
        [SerializeField] private byte _maxPlayers = 16;

        [Tooltip("Maps this server can host. Drives the matchmaker's preferred-map filter.")]
        [SerializeField] private ushort[] _mapIds = new ushort[0];

        private ServerMasterReporter _reporter;
        private GameServerMatchReporter _link;
        private GameServerConfig _config;

        /// <summary>The id the master assigned, or 0 when standalone or not yet registered.</summary>
        public ushort ServerId { get; private set; }

        /// <summary>Whether this server is advertised to the master.</summary>
        public bool IsLinked => _link != null && _link.IsConnected;

        private void Awake() => _reporter = GetComponent<ServerMasterReporter>();

        private void Start()
        {
            // Resolved here rather than shared with NetServerBootstrap's instance on purpose:
            // this component works in a scene that has no NetServerBootstrap, and both read
            // the same variables through the same type, so the two cannot drift the way the
            // duplicated _udpPort and _maxPlayers fields could.
            //
            // The port default moved from 27100 to 27000 with this change. 27100 was a port
            // the master has never listened on — GS_REGISTER travels the ordinary MSP
            // connection — so every registration attempt was dialling a closed port and
            // reporting it below as "the master is down".
            var defaults = new GameServerConfig
            {
                MasterHost = _masterHost,
                MasterPort = _masterPort,
                PublicIp   = _publicIp,
                UdpPort    = _udpPort,
                MaxPlayers = _maxPlayers,
                MapIds     = _mapIds,

                // Not advertised, and not this component's business — but the resolver rejects
                // a slot count below the player count, so it needs a value that cannot trip it.
                MaxConnections = Math.Max(_maxPlayers, byte.MaxValue),
            };

            try
            {
                _config = defaults.ApplyEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogError($"[net] master link: configuration rejected, staying standalone. {ex.Message}");
                return;
            }

            if (!_config.IsLinkedToMaster)
            {
                Debug.Log("[net] master link: standalone — no host configured, matches will not be advertised.");
                return;
            }

            string secret = Environment.GetEnvironmentVariable(NetServerBootstrap.SharedSecretVariable);
            if (string.IsNullOrEmpty(secret))
            {
                Debug.LogWarning(
                    $"[net] master link: {NetServerBootstrap.SharedSecretVariable} is not set. " +
                    "Staying standalone rather than registering with an empty secret, which the " +
                    "master would reject as a bad credential rather than as missing configuration.");
                return;
            }

            _ = ConnectAsync(secret);
        }

        // async void is what a MonoBehaviour lifecycle method would force; an async Task the
        // caller discards keeps the exception observable instead of letting it reach the
        // Unity player loop as an unhandled crash.
        private async Task ConnectAsync(string secret)
        {
            var registration = new GameServerRegistration
            {
                ServerSecret = secret,
                PublicIp     = _config.PublicIp,
                UdpPort      = _config.UdpPort,
                MaxPlayers   = _config.MaxPlayers,
                MapIds       = _config.MapIds,
            };

            var reporter = new GameServerMatchReporter(new GameServerLink(), ownsLink: true);

            try
            {
                ServerId = await reporter.ConnectAndRegisterAsync(
                    _config.MasterHost,
                    _config.MasterPort,
                    registration,
                    CreateTlsOptions()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Caught, not rethrown, and the reporter is disposed rather than installed. A
                // master that is down must not stop a match from running -- that is the whole
                // point of the standalone contingency, and it is worth more than a stack trace.
                Debug.LogWarning($"[net] master link: registration failed, staying standalone. {ex.Message}");
                reporter.Dispose();
                return;
            }

            if (ServerId == 0)
            {
                Debug.LogWarning("[net] master link: the master refused registration. Staying standalone.");
                reporter.Dispose();
                return;
            }

            _link = reporter;
            _reporter.SetReporter(reporter);

            AdoptServerIdOnValidator();

            Debug.Log($"[net] master link: registered as server {ServerId} with {_config.MasterHost}:{_config.MasterPort}.");
        }

        /// <summary>
        /// Hands the master-assigned id to the ticket validator, so it can start enforcing that
        /// a ticket was issued for THIS server.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the re-registration <c>NetServerBootstrap</c> already promised in a comment
        /// and that nothing performed. Until it runs, the validator's serverId is 0, which
        /// disables the check — correct before registration, wrong forever after it. With a
        /// fleet sharing one secret, a ticket the master issued for another server verified
        /// here and was admitted, so matchmaking assignment was advisory rather than enforced.
        /// </para>
        /// <para>
        /// Absent bootstrap or validator is not an error: a scene may run this component alone,
        /// and an unsigned-ticket development build has no validator at all.
        /// </para>
        /// </remarks>
        private void AdoptServerIdOnValidator()
        {
            NetServerBootstrap bootstrap = FindObjectOfType<NetServerBootstrap>();
            if (bootstrap == null || bootstrap.Validator == null) return;

            bootstrap.Validator.AdoptServerId(ServerId);
            Debug.Log($"[net] join tickets are now checked against server id {ServerId}.");
        }

        // No `?` on the return type: this file has no `#nullable enable`, so the annotation
        // would only earn a CS8632 without telling the compiler anything. Null means the
        // plaintext LAN path, and the overload it feeds takes a nullable parameter.
        private MasterClientTlsOptions CreateTlsOptions()
        {
            if (!_config.MasterTlsEnabled) return null;

            return new MasterClientTlsOptions
            {
                Enabled = true,
                TargetHost = string.IsNullOrWhiteSpace(_config.MasterTlsTargetHost)
                    ? _config.MasterHost
                    : _config.MasterTlsTargetHost,
                PinnedFingerprintSha256 = _config.MasterTlsPinnedFingerprintSha256,
            };
        }

        // The Poll() contract from the master-server track's plan section 5: every event and Task continuation
        // fires on the thread that calls this, so Unity API use stays on the main thread and the
        // whole off-main-thread bug class disappears. One frame of latency, on a lobby link.
        private void Update() => _link?.Poll();

        private void OnDestroy()
        {
            _link?.Dispose();
            _link = null;
        }
    }
}
