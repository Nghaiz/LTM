using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Reports this server's liveness and match results to the master. Phase-03 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>It talks to a port, not to a socket.</b> The component holds an
    /// <see cref="IMatchReporter"/> and defaults to <see cref="NullMatchReporter"/>, which is
    /// standalone mode: the server plays complete matches and is simply not advertised, with
    /// clients connecting by IP. That is the phase-03 risk table's contingency for the master
    /// not being ready, and it is reached by construction rather than by a null check on every
    /// call.
    /// </para>
    /// <para>
    /// <b>Wiring the real link is a two-line change and a plugin drop.</b>
    /// <c>Ironfront.Net.MasterLink.GameServerMatchReporter</c> adapts Dev D's TCP client onto
    /// this port; handing one to <see cref="SetReporter"/> from a boot script is all this
    /// component needs. It is not referenced here because the Unity project consumes prebuilt
    /// DLLs from <c>Assets/Plugins</c> and neither <c>Ironfront.Net.MasterLink.dll</c> nor
    /// <c>Ironfront.MasterClient.dll</c> is dropped there yet — a <c>.meta</c>-file change,
    /// which Dev C is not permitted to make (plan.md section 2.2). Checklist item A11.
    /// </para>
    /// <para>
    /// Pacing is <see cref="HeartbeatPacer"/> rather than <c>InvokeRepeating</c>, which the
    /// phase-03 sketch uses. <c>InvokeRepeating</c> keeps firing after the server stops, drifts
    /// against the tick clock, and cannot be asserted on from a test.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(150)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ServerTickLoop))]
    [RequireComponent(typeof(MatchController))]
    public sealed class ServerMasterReporter : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Room this server is hosting, as assigned by the master. 0 in standalone.")]
        [SerializeField] private int _roomId;

        [Header("Heartbeat")]
        [SerializeField] private float _heartbeatSeconds = HeartbeatPacer.DefaultIntervalSeconds;

        private ServerTickLoop _loop;
        private MatchController _controller;
        private HeartbeatPacer _pacer;

        private readonly List<MatchPlayerScore> _scores = new List<MatchPlayerScore>(
            ProtocolConstants.MAX_PLAYERS);

        /// <summary>Where match reports go. Never null — standalone is a reporter, not a null.</summary>
        public IMatchReporter Reporter { get; private set; } = new NullMatchReporter();

        /// <summary>The id the master assigned, or 0 in standalone mode.</summary>
        public ushort ServerId => Reporter.ServerId;

        private void Awake()
        {
            _loop       = GetComponent<ServerTickLoop>();
            _controller = GetComponent<MatchController>();
            _pacer      = new HeartbeatPacer(_heartbeatSeconds);
        }

        private void OnEnable()
        {
            if (_controller != null && _controller.Match != null)
                _controller.Match.MatchEnded += OnMatchEnded;
        }

        private void OnDisable()
        {
            if (_controller != null && _controller.Match != null)
                _controller.Match.MatchEnded -= OnMatchEnded;
        }

        /// <summary>
        /// Installs a live reporter. Call from a boot script once the master link is connected
        /// and registered; until then the component runs standalone.
        /// </summary>
        public void SetReporter(IMatchReporter reporter)
        {
            Reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));

            Debug.Log(Reporter.IsConnected
                ? $"[net] reporting to master as server {Reporter.ServerId}"
                : "[net] master reporter installed but not connected; running standalone");
        }

        private void Update()
        {
            if (_loop == null || _controller == null || _controller.Match == null) return;
            if (!_pacer.IsDue(Time.unscaledDeltaTime)) return;

            ServerTickScheduler scheduler = _loop.Scheduler;

            Reporter.Heartbeat(new MatchHeartbeat(
                (byte)Mathf.Min(_loop.PlayerCount, byte.MaxValue),
                // No CPU percentage. Unity exposes no portable process-CPU counter, and a
                // fabricated number on a matchmaking input is worse than an absent one — the
                // master would sort servers by it. Tick time is the real load signal and is
                // measured, so that is what is sent. Checklist item A12.
                cpuPercent: -1f,
                (float)scheduler.TickTimes.Mean(),
                _controller.Match.Phase));
        }

        private void OnMatchEnded(byte winningTeam)
        {
            CollectScores();
            Reporter.MatchEnded(_roomId, _scores);

            Debug.Log($"[net] match ended, winner "
                      + (winningTeam == TeamId.None ? "draw" : $"team {winningTeam}"));
        }

        private void CollectScores()
        {
            _scores.Clear();

            // Per-player kill/death accounting is not tracked yet — S_DEATH carries the killer
            // but nothing tallies it. Reporting an empty list is deliberate over reporting
            // zeroes for every player: the master stores what it is given, and rows of
            // all-zero scores are indistinguishable from a match where nobody scored.
            // Checklist item A13.
        }
    }
}
