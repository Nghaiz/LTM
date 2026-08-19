using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Transport;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Drives the match lifecycle and broadcasts it. Phase-03 tasks 1-3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This class coordinates; it does not decide.</b> Every rule — when warmup ends, how
    /// fast a point is taken, when a message is worth sending, what a reset clears — lives in
    /// <see cref="MatchStateMachine"/>, <see cref="CapturePointState"/> and
    /// <see cref="ServerStateAudit"/>, all engine-free and all unit-tested. What is left here
    /// is reading positions out of the scene, putting bytes on the transport, and telling the
    /// world to despawn. That split is decision C-01-6, and it is why "does the match end when
    /// a team runs out of tickets" and "are five rounds back to back clean" are answered by
    /// <c>dotnet test</c> rather than by somebody watching a build.
    /// </para>
    /// <para>
    /// At execution order +100: after <see cref="ServerInputStage"/> (-200) and the default-order
    /// gameplay <c>FixedUpdate</c>s, so capture headcounts are read from post-simulation
    /// positions; before <see cref="ServerSnapshotStage"/> (+200), so a phase change is
    /// broadcast in the same tick the snapshot describing it goes out.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ServerTickLoop))]
    public sealed class MatchController : MonoBehaviour
    {
        [Header("Capture points")]
        [Tooltip("Scene capture points, in id order. Empty = deathmatch: no points, no bleed.")]
        [SerializeField] private Transform[] _capturePoints = Array.Empty<Transform>();

        [Tooltip("Radius used only when the point itself leaves captureRange unset (0).")]
        [SerializeField] private float _captureRadius = 15f;

        [Tooltip("Capture speed used only when the point itself leaves CaptureSpeed unset (0).")]
        [SerializeField] private float _captureSpeed = 0.2f;

        [Header("Rules")]
        [SerializeField] private int _minPlayersToStart = 2;
        [SerializeField] private float _warmupSeconds = 20f;
        [SerializeField] private float _postMatchSeconds = 20f;
        [SerializeField] private int _startTickets = 200;

        private ServerTickLoop _loop;
        private MatchStateMachine _match;
        private ActorIdPool _actorIds;
        private ICapturePointDirectory _points;
        private CapturePointSlave _slave;

        // Refilled every tick from the registry, never reallocated. A List<T> would be simpler
        // and would allocate its backing array the first time it grew past the initial
        // capacity — in the one loop that must not allocate (M1 criterion 9).
        private ActorPresence[] _presence = new ActorPresence[ProtocolConstants.MAX_ACTORS];
        private int _presenceCount;

        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        /// <summary>The authoritative match. Null until <c>Awake</c>.</summary>
        public MatchStateMachine Match => _match;

        /// <summary>Actor ids, with the phase-03 trap-2 quarantine.</summary>
        public ActorIdPool ActorIds => _actorIds;

        /// <summary>Raised when the world must be torn down. The spawner subscribes.</summary>
        public event System.Action WorldResetRequested;

        private void Awake()
        {
            _loop = GetComponent<ServerTickLoop>();

            var rules = new MatchRules
            {
                MinPlayersToStart = _minPlayersToStart,
                WarmupSeconds     = _warmupSeconds,
                PostMatchSeconds  = _postMatchSeconds,
                StartTickets      = _startTickets,
            };

            _actorIds = new ActorIdPool(
                ProtocolConstants.MAX_ACTORS, rules.ActorIdQuarantineSeconds);

            // Without this the pool is decoration: the registry allocated from its own counter
            // and nothing ever called TryAcquire, so the quarantine never ran and the audit's
            // ActorIdsInUse was always zero.
            ServerActorRegistry.Instance.UseIdPool(_actorIds);

            CapturePointState[] points = BuildCapturePoints();

            _match = new MatchStateMachine(rules, points);
            _match.PhaseChanged   += OnPhaseChanged;
            _match.ResetRequested += OnResetRequested;

            if (_points != null && points.Length > 0)
                _slave = new CapturePointSlave(_points, points.Length);

            _loop.BindMatch(_actorIds);

            // So an authoritative kill reaches the scoreboard. Before phase-05 nothing resolved
            // damage on the server at all, so ReportDeath had no caller and the ticket count
            // never moved in a networked match.
            _loop.BindMatchController(this);
        }

        private void OnDestroy()
        {
            if (_match == null) return;
            _match.PhaseChanged   -= OnPhaseChanged;
            _match.ResetRequested -= OnResetRequested;
        }

        private void FixedUpdate()
        {
            if (_loop == null || _loop.Transport == null || _match == null) return;

            CollectPresence();
            ReportSpawnPointCounts();

            var presence = new ReadOnlySpan<ActorPresence>(_presence, 0, _presenceCount);
            _match.Tick(Time.fixedDeltaTime, _loop.PlayerCount, presence);

            // AFTER the tick and BEFORE the broadcasts, so the value written onto
            // SpawnPoint.owner and the value put on the wire are the same one -- and so a
            // respawn resolved later this frame reads the ownership this tick produced rather
            // than the previous tick's.
            _slave?.Apply(_match.CapturePoints, presence);

            BroadcastMatchStateIfDirty();
            BroadcastDirtyCapturePoints();
        }

        /// <summary>
        /// Records a death against the dying actor's team. Called by whatever resolves damage;
        /// routed through here rather than straight to the machine so the controller stays the
        /// single place gameplay talks to the match.
        /// </summary>
        public void ReportDeath(byte team) => _match?.ReportDeath(team);

        /// <summary>The state a joining client needs before its first snapshot.</summary>
        public void SendFullMatchStateTo(ushort connectionId)
        {
            if (_loop == null || _loop.Transport == null || _match == null) return;

            SendTo(connectionId, ServerEventWriter.WriteMatchState(_payload, _match.ToMessage()));

            IReadOnlyList<CapturePointState> points = _match.CapturePoints;
            for (int i = 0; i < points.Count; i++)
                SendTo(connectionId,
                       ServerEventWriter.WriteCapturePoint(_payload, points[i].ToMessage()));
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Builds the authoritative points from the scene, per point. Phase-V8 task 2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Radius and capture speed now come from the component a level designer already
        /// authors, and <see cref="_captureRadius"/> / <see cref="_captureSpeed"/> demote to the
        /// values used only where a point left its own at zero. An uncapturable HQ reports a
        /// capture speed of zero, so <c>CapturePointState.Tick</c> moves it nowhere while it
        /// still counts for spawning, bleed and elimination — the minimum-change way to honour
        /// <c>canBeCaptured</c> without adding a branch to a well-tested engine-free class.
        /// </para>
        /// <para>
        /// <b>Ids stay the slot index</b> (D6). The id is the wire id and it must not become
        /// discovery-order dependent, which is also why the fallback below sorts by name rather
        /// than trusting <c>FindObjectsOfType</c>'s order.
        /// </para>
        /// </remarks>
        private CapturePointState[] BuildCapturePoints()
        {
            _points = NetServerBindings.CapturePoints;

            if (_points == null)
            {
                // No game assembly registered a directory. Every headless test rig is in this
                // state by design, and so is a deathmatch map -- it is not an error.
                if (_capturePoints != null && _capturePoints.Length > 0)
                    Debug.LogError(
                        "[net] capture points are authored on this controller but no "
                        + "ICapturePointDirectory is registered, so the objectives cannot be "
                        + "driven. Check that IronfrontNetBindings.Install ran.");

                return Array.Empty<CapturePointState>();
            }

            int count = _points.Bind(_capturePoints, out bool discovered, out int skipped);

            if (skipped > 0)
                Debug.LogError(
                    $"[net] {skipped} authored capture-point slot(s) held no CapturePoint. Ids "
                    + "are the slot index, so a hole renumbers every point after it and "
                    + "desynchronises the client's flags. Fill or remove the slot(s).");

            if (discovered && count > 0)
            {
                // D7. Logged at error and never silent: a server that comes up, accepts players
                // and plays a mode nobody chose is the quiet failure this phase exists to
                // remove. The resolved order is printed so a client/server flag mismatch is one
                // log line rather than an investigation.
                Debug.LogError(
                    "[net] MatchController._capturePoints is unbound, so the "
                    + $"{count} capture point(s) in the scene were discovered by name instead. "
                    + "Ids below are what the clients will be told; assign the array on this "
                    + "component to author them. Resolved order: " + DescribeIds(count));
            }

            if (count == 0) return Array.Empty<CapturePointState>();

            var states = new CapturePointState[count];
            for (int i = 0; i < count; i++)
            {
                CapturePointDefinition definition = _points.GetDefinition(i);

                Vector3 p = definition.Position;
                float radius = definition.Radius > 0f ? definition.Radius : _captureRadius;

                // Zero speed is MEANINGFUL -- it is how an uncapturable HQ is expressed -- so it
                // cannot also mean "unset". A negative value is the unset signal instead, which
                // the directory returns for a point that authored nothing.
                float speed = definition.CaptureSpeed >= 0f ? definition.CaptureSpeed : _captureSpeed;

                states[i] = new CapturePointState(
                    (byte)i, new Vec3(p.x, p.y, p.z), radius, speed);
            }

            return states;
        }

        /// <summary>The <c>id -> name</c> order, for the D7 fallback's one log line.</summary>
        private string DescribeIds(int count)
        {
            var text = new System.Text.StringBuilder(count * 16);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(i).Append(" -> ").Append(_points.GetDefinition(i).Name);
            }

            return text.ToString();
        }

        /// <summary>
        /// Feeds the match the spawn-point counts elimination is decided on. Phase-V8 task 4.
        /// </summary>
        /// <remarks>
        /// Skipped entirely when no directory is registered, which leaves the counts unreported
        /// and elimination inert — see <c>MatchStateMachine.SetSpawnPointCounts</c> for why
        /// "unknown" must not be read as "both teams are wiped out".
        /// </remarks>
        private void ReportSpawnPointCounts()
        {
            if (_points == null) return;

            _match.SetSpawnPointCounts(
                _points.CountSpawnPointsOwnedBy(0),
                _points.CountSpawnPointsOwnedBy(1));
        }

        private void CollectPresence()
        {
            _presenceCount = 0;

            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;
            for (int i = 0; i < actors.Count && _presenceCount < _presence.Length; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || !actor.isActiveAndEnabled) continue;

                Vector3 p = actor.transform.position;
                _presence[_presenceCount++] =
                    new ActorPresence(new Vec3(p.x, p.y, p.z), actor.Team, actor.IsAlive);
            }
        }

        private void BroadcastMatchStateIfDirty()
        {
            if (!_match.MatchStateIsDirty) return;

            int written = ServerEventWriter.WriteMatchState(_payload, _match.ToMessage());
            if (written < 0)
            {
                Debug.LogError("[net] match state did not frame");
                return;
            }

            // Marked sent only after the broadcast, not before. Clearing the flag first and
            // then failing to send leaves every client on the previous phase with nothing to
            // re-trigger it — the same build-then-confirm ordering the capture points use.
            if (Broadcast(written)) _match.MarkMatchStateSent();
        }

        private void BroadcastDirtyCapturePoints()
        {
            IReadOnlyList<byte> dirty = _match.DirtyCapturePoints;
            for (int i = 0; i < dirty.Count; i++)
            {
                if (!_match.TryGetPoint(dirty[i], out CapturePointState point) || point == null)
                    continue;

                int written = ServerEventWriter.WriteCapturePoint(_payload, point.ToMessage());
                if (written < 0)
                {
                    Debug.LogError($"[net] capture point {dirty[i]} did not frame");
                    continue;
                }

                if (Broadcast(written)) point.MarkSent();
            }
        }

        private bool Broadcast(int length)
        {
            if (_loop.Transport == null) return false;

            _loop.BroadcastReliable(
                new ReadOnlySpan<byte>(_payload, 0, length),
                (byte)ServerEventWriter.ReliableChannel);
            return true;
        }

        private void SendTo(ushort connectionId, int length)
        {
            if (length < 0) return;

            _loop.Transport.Send(
                connectionId,
                (byte)ServerEventWriter.ReliableChannel,
                new ReadOnlySpan<byte>(_payload, 0, length),
                reliable: true);
        }

        private void OnPhaseChanged(MatchPhase phase)
            => Debug.Log($"[net] match phase -> {phase} "
                         + $"({_match.Tickets0} / {_match.Tickets1} tickets)");

        private void OnResetRequested()
        {
            // Order matters. The world is torn down first so that nothing is still holding an
            // actor id when the netcode tables are cleared; clearing first would let a
            // still-live actor re-register itself into a table the audit is about to check.
            WorldResetRequested?.Invoke();

            // The static relay, for subscribers that live in Assembly-CSharp and so cannot hold
            // a reference to this component. The instance event above kept its subscribers
            // (none, historically) and is unchanged; NetWorldLifecycle is what the vehicle
            // spawners actually listen on. See NetWorldLifecycle for why it is not a serialized
            // reference per spawner.
            NetWorldLifecycle.RaiseReset();

            _slave?.Reset();
            _loop.ResetForNewMatch();

            ServerStateSnapshot state = _loop.AuditState();

            // IsCleanOfActorState, not IsClean: ResetForNewMatch deliberately keeps its sessions
            // because a reset is not a disconnect, so IsClean's Sessions == 0 could never hold
            // here and this line fired on every round transition with anyone connected.
            if (state.IsCleanOfActorState) return;

            // Phase-03 trap 1. Logged rather than asserted: the leak this catches shows up on
            // the second and third round of a server that has been up for an hour with nobody
            // watching, which is precisely when a development-only Debug.Assert is not running.
            Debug.LogError($"[net] match reset left state behind — {state}");
        }
    }
}
