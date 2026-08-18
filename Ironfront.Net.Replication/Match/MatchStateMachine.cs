using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// Where one actor is and whose side it is on, as far as capturing a point is concerned.
    /// </summary>
    /// <remarks>
    /// A flat struct rather than an interface over the live actor, so the state machine has no
    /// opinion about what an actor <i>is</i> — the Unity wrapper fills a reusable array from
    /// the scene and the tests fill the same array by hand, and both drive identical code.
    /// </remarks>
    public readonly struct ActorPresence
    {
        public readonly Vec3 Position;
        public readonly byte Team;
        public readonly bool IsAlive;

        public ActorPresence(in Vec3 position, byte team, bool isAlive)
        {
            Position = position;
            Team     = team;
            IsAlive  = isAlive;
        }
    }

    /// <summary>
    /// The authoritative match lifecycle: warmup, play, ticket bleed, win condition, reset.
    /// Phase-03 tasks 1-3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Engine-free for the reason every server rule in this library is
    /// (decision C-01-6): a MonoBehaviour cannot be reached from CI, so "does the match end
    /// when a team runs out of tickets" and "is the world clean after five rounds" are
    /// answerable from <c>dotnet test</c> instead of from somebody watching a build.
    /// </para>
    /// <para>
    /// <b>The machine owns no world state.</b> It does not despawn actors, free ids or clear
    /// history — it raises <see cref="ResetRequested"/> and the host does that, because the
    /// things needing cleanup live in the engine. Everything the machine <i>does</i> own
    /// (phase, timers, tickets, capture points) it clears itself, so a host that forgets to
    /// subscribe gets a visibly stuck round rather than a subtly leaking one.
    /// </para>
    /// <para>
    /// Allocation-free per tick after construction: the capture-point list is fixed at
    /// construction and the broadcast queue is a pre-sized list that is cleared, never
    /// reallocated.
    /// </para>
    /// <para>
    /// <b>What this machine deliberately does NOT own, and why</b> (phase-V8 D9). The original
    /// game keeps its match state in <c>ScoreUi</c>, a UI component that a headless server
    /// never instantiates, so on a dedicated server the original neither scores nor ends.
    /// V8 moved the one piece that is a <i>loss condition</i> — elimination by losing every
    /// spawn point (<see cref="SetSpawnPointCounts"/>) — into this class, and left the rest
    /// where it is:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Score and the <c>victoryPoints</c> race.</b> <c>ScoreUi.AddScore</c> accumulates per
    /// kill and declares a winner at a score gap. The networked match is decided by tickets
    /// instead, so there is nothing here to port to — porting it would give the server two
    /// competing win conditions, which is the shape of the bug V8 exists to close.
    /// </description></item>
    /// <item><description>
    /// <b><c>ScoreMultiplier(flags)</c> returns the flag count</b>, so a team holding no flags
    /// scores zero for every kill and a team being eliminated cannot score its way out.
    /// Faithful to the original, and irrelevant to a ticket-based match.
    /// </description></item>
    /// <item><description>
    /// <b><c>GameManager</c>'s modes are five loose booleans</b> (<c>reverseMode</c>,
    /// <c>assaultMode</c>, <c>nightMode</c>, <c>noVehicles</c>) plus <c>victoryPoints</c>, not
    /// an enum, so there is no single value to replicate as "the mode". The two that change
    /// gameplay state — <c>reverseMode</c> and <c>assaultMode</c> — are consumed once in
    /// <c>CapturePoint.Start()</c> and decide the <i>opening</i> ownership, which the server
    /// then adopts as its own initial value. They are therefore already covered.
    /// </description></item>
    /// </list>
    /// <para>
    /// Closing those is a rendering-and-rules redesign for the client track, not a netcode
    /// defect. Recorded here so the next reader does not go looking for the port.
    /// </para>
    /// </remarks>
    public sealed class MatchStateMachine
    {
        /// <summary>Spawn-point counts have not been reported; elimination stays inert.</summary>
        private const int CountsNotReported = -1;

        private readonly MatchRules _rules;
        private readonly CapturePointState[] _points;
        private readonly List<byte> _dirtyPoints;

        private float _phaseTimer;
        private float _ticketsFloat0;
        private float _ticketsFloat1;
        private float _sinceLastBroadcast;
        private byte _lastBroadcastHumans;
        private int _spawnPoints0 = CountsNotReported;
        private int _spawnPoints1 = CountsNotReported;
        private float _playingElapsed;

        /// <summary>
        /// Seconds between unsolicited <c>S_MATCH_STATE</c> messages while nothing changes.
        /// A phase change or a ticket change sends immediately regardless.
        /// </summary>
        public const float HeartbeatBroadcastSeconds = 1f;

        public MatchStateMachine(MatchRules? rules = null, params CapturePointState[]? points)
        {
            _rules  = rules ?? MatchRules.Default;
            _points = points ?? Array.Empty<CapturePointState>();
            _dirtyPoints = new List<byte>(_points.Length);

            _ticketsFloat0 = _rules.StartTickets;
            _ticketsFloat1 = _rules.StartTickets;
        }

        /// <summary>Raised when the phase changes. The argument is the NEW phase.</summary>
        public event Action<MatchPhase>? PhaseChanged;

        /// <summary>
        /// Raised once per reset, before the phase returns to
        /// <see cref="MatchPhase.WaitingForPlayers"/>. The host despawns actors, frees ids and
        /// clears per-client tables here.
        /// </summary>
        public event Action? ResetRequested;

        /// <summary>
        /// Raised when a round is decided, with the winning team
        /// (<see cref="TeamId.Team0"/> / <see cref="TeamId.Team1"/> / <see cref="TeamId.None"/>
        /// for a draw). This is where GS_MATCH_ENDED is reported to the master.
        /// </summary>
        public event Action<byte>? MatchEnded;

        public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForPlayers;

        public MatchRules Rules => _rules;

        public IReadOnlyList<CapturePointState> CapturePoints => _points;

        /// <summary>Capture points whose value moved enough to be worth a message this tick.</summary>
        public IReadOnlyList<byte> DirtyCapturePoints => _dirtyPoints;

        public int Tickets0 => (int)Math.Ceiling(_ticketsFloat0);

        public int Tickets1 => (int)Math.Ceiling(_ticketsFloat1);

        /// <summary>Humans connected, as last reported to <see cref="Tick"/>.</summary>
        public int HumanPlayerCount { get; private set; }

        /// <summary>Rounds completed since construction. The load scenario asserts on this.</summary>
        public int CompletedMatches { get; private set; }

        /// <summary>Seconds left in the current phase, or 0 for a phase with no timer.</summary>
        public float PhaseSecondsRemaining => _phaseTimer > 0f ? _phaseTimer : 0f;

        /// <summary>True when the current state is worth pushing to clients this tick.</summary>
        public bool MatchStateIsDirty { get; private set; }

        /// <summary>
        /// Records a death. Costs the dead actor's own team a ticket — the Ravenfield rule,
        /// where dying is what drains you, not killing.
        /// </summary>
        /// <remarks>
        /// Ignored outside <see cref="MatchPhase.Playing"/>. A warmup kill that quietly cost a
        /// ticket would make the scoreboard disagree with the round before it began, and the
        /// natural place for that to surface is a match that ends slightly early for no visible
        /// reason.
        /// </remarks>
        public void ReportDeath(byte team)
        {
            if (Phase != MatchPhase.Playing) return;

            if (team == TeamId.Team0) _ticketsFloat0 -= _rules.TicketsPerDeath;
            else if (team == TeamId.Team1) _ticketsFloat1 -= _rules.TicketsPerDeath;
            else return;

            ClampTickets();
            MatchStateIsDirty = true;
        }

        /// <summary>
        /// Reports how many spawn points each team currently holds. Call before
        /// <see cref="Tick"/>; until it is called, elimination cannot fire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Spawn points, not capture points</b> (phase-V8 D10). The faithful port of
        /// <c>ScoreUi.AddFlag</c>'s condition is <c>ActorManager.HasSpawnPoint(team)</c>, which
        /// counts every <c>SpawnPoint</c> whose <c>owner</c> is that team — including
        /// uncapturable HQs. Counting only capture points would end the match on a map where a
        /// team still holds its base, and never end it on a map whose HQ cannot be taken.
        /// </para>
        /// <para>
        /// <b>Not reported is not zero.</b> A host that never calls this leaves the counts at
        /// <see cref="CountsNotReported"/> and elimination stays off, because the alternative —
        /// treating "I have no idea" as "both teams are wiped out" — ends every round on the
        /// first tick past the grace window, on exactly the deployments that forgot to wire it.
        /// </para>
        /// </remarks>
        public void SetSpawnPointCounts(int team0, int team1)
        {
            _spawnPoints0 = team0 < 0 ? 0 : team0;
            _spawnPoints1 = team1 < 0 ? 0 : team1;
        }

        /// <summary>
        /// Advances the match by one server tick.
        /// </summary>
        /// <param name="deltaSeconds">Elapsed simulated time.</param>
        /// <param name="humanPlayerCount">Connected humans. Bots never count toward starting.</param>
        /// <param name="actors">
        /// Every actor that could be standing on a capture point. Dead ones are ignored;
        /// passing them anyway is fine and is what the Unity wrapper does, since filtering at
        /// the call site would mean a second pass over the same list.
        /// </param>
        public void Tick(float deltaSeconds, int humanPlayerCount, ReadOnlySpan<ActorPresence> actors)
        {
            if (deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

            MatchPhase before = Phase;
            HumanPlayerCount = humanPlayerCount;
            _dirtyPoints.Clear();

            switch (Phase)
            {
                case MatchPhase.WaitingForPlayers:
                    if (humanPlayerCount >= _rules.MinPlayersToStart) EnterPhase(MatchPhase.Warmup);
                    break;

                case MatchPhase.Warmup:
                    // Dropping back is deliberate. The alternative — starting a round for one
                    // player because two were briefly connected — produces a match that is over
                    // before anyone can join it, and then resets, repeatedly.
                    if (humanPlayerCount < _rules.MinPlayersToStart)
                    {
                        EnterPhase(MatchPhase.WaitingForPlayers);
                        break;
                    }

                    _phaseTimer -= deltaSeconds;
                    if (_phaseTimer <= 0f) EnterPhase(MatchPhase.Playing);
                    break;

                case MatchPhase.Playing:
                    _playingElapsed += deltaSeconds;
                    UpdateCapturePoints(actors, deltaSeconds);
                    DrainTickets(deltaSeconds);
                    ApplyElimination();
                    // A live round is NOT abandoned when the humans leave. The bots are still
                    // fighting, the match still resolves, and the master still gets its
                    // GS_MATCH_ENDED — which is what keeps the server's advertised state honest
                    // rather than stuck mid-round.
                    if (_ticketsFloat0 <= 0f || _ticketsFloat1 <= 0f) EnterPhase(MatchPhase.Ended);
                    break;

                case MatchPhase.Ended:
                    _phaseTimer -= deltaSeconds;
                    if (_phaseTimer <= 0f) EnterPhase(MatchPhase.Resetting);
                    break;

                case MatchPhase.Resetting:
                    // One tick long: the host cleans up on the event and the machine is
                    // immediately ready for the next round. Lingering here would leave clients
                    // watching a phase that does nothing.
                    PerformReset();
                    EnterPhase(MatchPhase.WaitingForPlayers);
                    break;
            }

            _sinceLastBroadcast += deltaSeconds;

            if (Phase != before
                || (byte)Math.Min(humanPlayerCount, byte.MaxValue) != _lastBroadcastHumans
                || _sinceLastBroadcast >= HeartbeatBroadcastSeconds)
                MatchStateIsDirty = true;
        }

        /// <summary>The message describing the current state.</summary>
        public MatchStateMessage ToMessage()
            => new MatchStateMessage(
                Phase,
                (ushort)Math.Max(0, Math.Min(Tickets0, ushort.MaxValue)),
                (ushort)Math.Max(0, Math.Min(Tickets1, ushort.MaxValue)),
                (ushort)Math.Min(Math.Ceiling(PhaseSecondsRemaining), ushort.MaxValue),
                (byte)Math.Min(HumanPlayerCount, byte.MaxValue));

        /// <summary>
        /// Records that the current match state has been broadcast. Separate from
        /// <see cref="ToMessage"/> so a send failure does not clear the flag — the same
        /// build-then-confirm split <see cref="CapturePointState.MarkSent"/> uses.
        /// </summary>
        public void MarkMatchStateSent()
        {
            MatchStateIsDirty   = false;
            _sinceLastBroadcast = 0f;
            _lastBroadcastHumans = (byte)Math.Min(HumanPlayerCount, byte.MaxValue);
        }

        /// <summary>Finds a point by id.</summary>
        public bool TryGetPoint(byte pointId, out CapturePointState? point)
        {
            for (int i = 0; i < _points.Length; i++)
            {
                if (_points[i].PointId != pointId) continue;
                point = _points[i];
                return true;
            }

            point = null;
            return false;
        }

        /// <summary>
        /// Forces a reset without waiting out the post-match timer. Used by the load scenario
        /// and by an operator command; the normal path is the <see cref="MatchPhase.Ended"/>
        /// timer.
        /// </summary>
        public void ForceReset()
        {
            PerformReset();
            EnterPhase(MatchPhase.WaitingForPlayers);
            MatchStateIsDirty = true;
        }

        // ------------------------------------------------------------------ internals

        private void UpdateCapturePoints(ReadOnlySpan<ActorPresence> actors, float deltaSeconds)
        {
            for (int i = 0; i < _points.Length; i++)
            {
                CapturePointState point = _points[i];

                int count0 = 0, count1 = 0;
                for (int a = 0; a < actors.Length; a++)
                {
                    ref readonly ActorPresence actor = ref actors[a];
                    if (!actor.IsAlive) continue;
                    if (!point.Contains(actor.Position)) continue;

                    if (actor.Team == TeamId.Team0) count0++;
                    else if (actor.Team == TeamId.Team1) count1++;
                }

                if (point.Tick(count0, count1, deltaSeconds, _rules))
                    _dirtyPoints.Add(point.PointId);
            }
        }

        private void DrainTickets(float deltaSeconds)
        {
            int owned0 = 0, owned1 = 0;
            for (int i = 0; i < _points.Length; i++)
            {
                byte owner = _points[i].OwningTeam;
                if (owner == TeamId.Team0) owned0++;
                else if (owner == TeamId.Team1) owned1++;
            }

            if (owned0 == owned1) return;

            float rate = Math.Abs(owned0 - owned1) * _rules.BleedPerPointPerSecond * deltaSeconds;

            int before0 = Tickets0, before1 = Tickets1;
            if (owned0 > owned1) _ticketsFloat1 -= rate;
            else _ticketsFloat0 -= rate;

            ClampTickets();

            // Only dirty when the WHOLE ticket count moved. The float bleeds continuously but
            // the wire carries integers, so flagging every tick would put a message on channel 2
            // thirty times a second to say a number that has not changed.
            if (Tickets0 != before0 || Tickets1 != before1) MatchStateIsDirty = true;
        }

        /// <summary>
        /// A team holding no spawn points has lost. Phase-V8 task 4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Expressed as zeroing the loser's tickets, not as a separate end path.</b> The
        /// round then ends through the line immediately below the call site, so the phase
        /// change, <see cref="MatchEnded"/>, the broadcast and the reset all behave exactly as
        /// they do for ticket exhaustion — there is no second way for a match to end that a
        /// handler could have been written against and forgotten.
        /// </para>
        /// <para>
        /// It also keeps <c>MatchStateMessage.WinningTeam</c> honest without a wire change.
        /// The winner is derived from the two ticket counts, so an eliminated team that still
        /// had 180 tickets would otherwise be broadcast as the <i>winner</i> of the round it
        /// just lost.
        /// </para>
        /// <para>
        /// Both teams at zero — a degenerate map, or a mid-match teardown — zeroes both, which
        /// reads as a draw rather than awarding the round to whichever index is tested first.
        /// </para>
        /// </remarks>
        private void ApplyElimination()
        {
            if (_playingElapsed <= _rules.EliminationGraceSeconds) return;
            if (_spawnPoints0 == CountsNotReported || _spawnPoints1 == CountsNotReported) return;

            bool eliminated0 = _spawnPoints0 == 0;
            bool eliminated1 = _spawnPoints1 == 0;
            if (!eliminated0 && !eliminated1) return;

            if (eliminated0) _ticketsFloat0 = 0f;
            if (eliminated1) _ticketsFloat1 = 0f;

            MatchStateIsDirty = true;
        }

        private void ClampTickets()
        {
            if (_ticketsFloat0 < 0f) _ticketsFloat0 = 0f;
            if (_ticketsFloat1 < 0f) _ticketsFloat1 = 0f;
        }

        private void EnterPhase(MatchPhase phase)
        {
            Phase = phase;

            _phaseTimer = phase switch
            {
                MatchPhase.Warmup => _rules.WarmupSeconds,
                MatchPhase.Ended  => _rules.PostMatchSeconds,
                _                 => 0f,
            };

            // Reset on ENTRY to Playing, not in PerformReset: ForceReset can drop a live round
            // straight back to WaitingForPlayers without ever passing through PerformReset's
            // caller, and a grace window left running from the previous round would let the
            // next one end on its own first tick.
            if (phase == MatchPhase.Playing) _playingElapsed = 0f;

            MatchStateIsDirty = true;

            if (phase == MatchPhase.Ended)
            {
                CompletedMatches++;
                MatchEnded?.Invoke(ToMessage().WinningTeam);
            }

            PhaseChanged?.Invoke(phase);
        }

        private void PerformReset()
        {
            _ticketsFloat0 = _rules.StartTickets;
            _ticketsFloat1 = _rules.StartTickets;
            _phaseTimer    = 0f;

            for (int i = 0; i < _points.Length; i++) _points[i].Reset();
            _dirtyPoints.Clear();

            // Fired after the machine's own state is clean, so a handler that inspects the
            // machine (the clean-state audit does) sees the post-reset values rather than a
            // half-reset one.
            ResetRequested?.Invoke();
        }
    }
}
