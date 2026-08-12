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
    /// OWNER: Dev C.
    /// </para>
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

        [Header("Startup")]
        [SerializeField] private bool _startOnAwake = true;

        [Header("Transport")]
        [Tooltip("In-process wire with no socket. The only transport that exists until Dev B lands UDP.")]
        [SerializeField] private bool _useLoopbackTransport = true;

        [SerializeField] private int _port = 27015;
        [SerializeField] private int _maxConnections = 16;

        [Header("Diagnostics")]
        [Tooltip("Seconds between overload checks. 0 disables the warning.")]
        [SerializeField] private float _overloadCheckInterval = 5f;

        private float _nextOverloadCheck;
        private bool _ownsTransport;

        /// <summary>The loopback wire, when one was created. Hand <c>.Client</c> to a test client.</summary>
        public LoopbackTransport Loopback { get; private set; }

        /// <summary>The tick loop this bootstrap drives.</summary>
        public ServerTickLoop TickLoop { get; private set; }

        private void Awake()
        {
            TickLoop = GetComponent<ServerTickLoop>();

            NetContext.SetRole(NetRole.Server);
            Time.maximumDeltaTime = MaxDeltaTime;

            // Only in a real headless run. Capping the Editor to 30 fps would make Dev A's
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

        /// <summary>Creates the transport (unless one was injected) and starts the loop.</summary>
        public void StartServer()
        {
            if (TickLoop == null) TickLoop = GetComponent<ServerTickLoop>();
            if (TickLoop.Transport != null) return;

            if (!_useLoopbackTransport)
            {
                Debug.LogError(
                    "[net] no UDP transport exists yet (Dev B, phase-01). Either leave "
                    + "'Use Loopback Transport' on, or call ServerTickLoop.Bind with your own "
                    + "ITransportServer before this component's Awake.");
                return;
            }

            Loopback = new LoopbackTransport();
            _ownsTransport = true;

            ITransportServer server = Loopback.Server;
            server.Start(_port, _maxConnections);

            // The loopback clock is virtual and delivers nothing until advanced, so the loop
            // feeds it the real elapsed milliseconds once per fixed step.
            TickLoop.Bind(server, Loopback.Advance);

            Debug.Log($"[net] server up on the loopback wire, {_maxConnections} slots");
        }

        /// <summary>Unbinds the loop and disposes a transport this component created.</summary>
        public void StopServer()
        {
            if (TickLoop != null) TickLoop.Unbind();

            if (_ownsTransport && Loopback != null)
            {
                Loopback.Dispose();
                Loopback = null;
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
