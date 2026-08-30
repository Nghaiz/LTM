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
    /// <b>It talks to a port, not to a socket.</b> The component holds an
    /// <see cref="IMatchReporter"/> and defaults to <see cref="NullMatchReporter"/>, which is
    /// standalone mode: the server plays complete matches and is simply not advertised, with
    /// clients connecting by IP. That is the phase-03 risk table's contingency for the master
    /// not being ready, and it is reached by construction rather than by a null check on every
    /// call.
    /// </para>
    /// <para>
    /// <b>Wiring the real link is a two-line change and a plugin drop — both landed 2026-08-15,
    /// closing A11.</b> <c>Ironfront.Net.MasterLink.GameServerMatchReporter</c> adapts the master-server track's
    /// TCP client onto this port, and <see cref="MasterLinkBootstrap"/> is the boot script that
    /// builds one and hands it to <see cref="SetReporter"/>. It is still not referenced from
    /// this file, and deliberately so: keeping the transport-facing half in its own component is
    /// what lets this one stay network-agnostic, and it confines the <c>System.Text.Json</c>
    /// dependency chain that <c>GameServerLink</c> drags in to a single file.
    /// <c>Ironfront.Net.MasterLink.dll</c> and <c>Ironfront.MasterClient.dll</c> now ship from
    /// <c>Assets/Plugins</c> via <c>tools/build-libs.ps1</c>.
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

        /// <summary>
        /// Fills <see cref="_scores"/> from the match's tally. Phase P6 task 3.2, checklist A13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The empty list survives, and its original reason with it.</b> Before P6 nothing
        /// tallied kills at all, and reporting an empty list was chosen over reporting zeroes
        /// for every player because the master stores what it is given, and rows of all-zero
        /// scores are indistinguishable from a match where nobody scored. That reasoning was
        /// right and it still binds: a player who neither killed nor died is OMITTED rather than
        /// reported at zero, so a match nobody scored in still produces no rows — the difference
        /// is that now it is because nothing happened, not because nothing was counted.
        /// </para>
        /// <para>
        /// <b>The population is connected players, not the tally's id space.</b> Bots kill and
        /// die and land in <see cref="MatchScoreTally"/> like anybody else, and a bot has no
        /// account for <c>MatchPlayerResult.PlayerId</c> to name. Walking the players is what
        /// keeps the report to rows the master can do something with, and it is also what makes
        /// the id resolution a single lookup per row rather than a reverse search.
        /// </para>
        /// <para>
        /// <b>Ticket accounting is untouched.</b> <c>MatchController.ReportDeath</c> still costs
        /// the dying team a ticket, on the same call that feeds this tally
        /// (<c>ServerTickLoop.EmitDeath</c>). This is a second reader of one resolved death, not
        /// a second path.
        /// </para>
        /// </remarks>
        private void CollectScores()
        {
            _scores.Clear();

            if (_loop == null) return;

            MatchScoreTally tally = _loop.Scores;

            IReadOnlyList<ServerTickLoop.ServerPlayerScoreRow> rows = _loop.ScoreRows;
            for (int i = 0; i < rows.Count; i++)
            {
                ushort actorId = rows[i].ActorId;
                if (tally.IsUntouched(actorId)) continue;

                int kills = tally.KillsOf(actorId);

                _scores.Add(new MatchPlayerScore(
                    rows[i].PlayerId,
                    kills,
                    tally.DeathsOf(actorId),

                    // Computed at the use site, never stored: there is no scoring rule beyond
                    // kills yet, and a Score field kept alongside Kills would be a second copy
                    // of one number that the first rule to arrive would immediately desynchronise
                    // (code-conventions.md, "No Derived Fields"). When objectives start scoring,
                    // the rule lands in MatchStateMachine with every other rule and this line
                    // reads it.
                    kills * PointsPerKill));
            }
        }

        /// <summary>
        /// What one kill is worth on the end-of-match report.
        /// </summary>
        /// <remarks>
        /// Named rather than inlined as a bare <c>1</c>, so that the moment a capture or an
        /// assist is worth points there is one place already asking the question. It is not a
        /// balance number and does not belong in a config: nothing reads it but the line above.
        /// </remarks>
        private const int PointsPerKill = 1;
    }
}
