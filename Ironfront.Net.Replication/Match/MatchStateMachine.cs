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
    /// The authoritative match lifecycle: warmup, play, scoring, win condition, reset.
    /// Phase-03 tasks 1-3; re-pointed at the game's own rule by P11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Engine-free for the reason every server rule in this library is
    /// (decision C-01-6): a MonoBehaviour cannot be reached from CI, so "does the match end
    /// when a team leads by the victory margin" and "is the world clean after five rounds" are
    /// answerable from <c>dotnet test</c> instead of from somebody watching a build.
    /// </para>
    /// <para>
    /// <b>The machine owns no world state.</b> It does not despawn actors, free ids or clear
    /// history — it raises <see cref="ResetRequested"/> and the host does that, because the
    /// things needing cleanup live in the engine. Everything the machine <i>does</i> own
    /// (phase, timers, scores, capture points) it clears itself, so a host that forgets to
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
    /// <b>Score and the <c>victoryPoints</c> race are now OWNED HERE — P11 closed this bullet,
    /// and it is kept only to say so.</b> V8 read the networked match as ticket-based and
    /// concluded there was "nothing here to port to". That was the defect, not the resolution:
    /// the two runtimes were playing different games, and the ticket rule was the one nothing
    /// else in the project implements. <see cref="ReportDeath"/> now accumulates upward through
    /// <see cref="ConquestScoreRule.Award"/> and the round ends on
    /// <see cref="ConquestScoreRule.Decide"/> — the same statics <c>MatchScoreboard</c> calls,
    /// so there is one rule rather than two competing win conditions.
    /// </description></item>
    /// <item><description>
    /// <b><c>ScoreMultiplier(flags)</c> returns the flag count</b>, so a team holding no flags
    /// scores zero for every kill and a team being eliminated cannot score its way out.
    /// Faithful to the original — and, since P11, live here too rather than irrelevant.
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

        // ASCENDING score accumulators, starting at 0 -- not tickets. Integers, not floats:
        // nothing subtracts from them and nothing accrues continuously any more (P11 deleted
        // the bleed), so the float accumulator that existed to carry sub-ticket bleed would now
        // be a fraction that can never be non-zero.
        private int _score0;
        private int _score1;

        /// <summary>Both teams eliminated at once: the round is over and nobody won.</summary>
        private bool _drawn;

        private float _sinceLastBroadcast;
        private byte _lastBroadcastHumans;
        /// <summary>
        /// Raised once when elimination fires against BOTH teams — no team holds a spawn point,
        /// so the round ends in a draw the instant the grace period expires and does so again
        /// every round. A host subscribes to say so out loud. See <c>ApplyElimination</c>.
        /// </summary>
        public event Action? BothTeamsEliminated;

        private bool _warnedBothEliminated;

        private int _spawnPoints0 = CountsNotReported;
        private int _spawnPoints1 = CountsNotReported;
        private float _playingElapsed;

        /// <summary>
        /// Seconds between unsolicited <c>S_MATCH_STATE</c> messages while nothing changes.
        /// A phase change or a score change sends immediately regardless.
        /// </summary>
        public const float HeartbeatBroadcastSeconds = 1f;

        public MatchStateMachine(MatchRules? rules = null, params CapturePointState[]? points)
        {
            _rules  = rules ?? MatchRules.Default;
            _points = points ?? Array.Empty<CapturePointState>();
            _dirtyPoints = new List<byte>(_points.Length);
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

        /// <summary>
        /// Adopts the map's OPENING ownership for one capture point, and for every later round.
        /// </summary>
        /// <param name="index">Point index, matching <see cref="CapturePoints"/> order.</param>
        /// <param name="owner">-1 fully team 0, +1 fully team 1, 0 neutral.</param>
        /// <remarks>
        /// Here rather than on the caller so the dirty flag moves with the value: the host
        /// adopts during <c>Start</c>, before any client can have been told anything, and a
        /// point whose opening state was never broadcast renders neutral on every client that
        /// joins before somebody walks onto it. See <c>CapturePointState.AdoptOpeningOwner</c>
        /// for the defect this closes (X-53).
        /// </remarks>
        public void AdoptOpeningOwner(int index, float owner)
        {
            if (index < 0 || index >= _points.Length) return;

            _points[index].AdoptOpeningOwner(owner);
            MatchStateIsDirty = true;
        }

        /// <summary>Capture points whose value moved enough to be worth a message this tick.</summary>
        public IReadOnlyList<byte> DirtyCapturePoints => _dirtyPoints;

        /// <summary>Team 0's score. Ascends from 0; never spent.</summary>
        public int Score0 => _score0;

        /// <summary>Team 1's score. Ascends from 0; never spent.</summary>
        public int Score1 => _score1;

        /// <summary>The lead one team needs over the other to win. Crosses the wire.</summary>
        public int VictoryPoints => _rules.VictoryPoints;

        /// <summary>Humans connected, as last reported to <see cref="Tick"/>.</summary>
        public int HumanPlayerCount { get; private set; }

        /// <summary>Rounds completed since construction. The load scenario asserts on this.</summary>
        public int CompletedMatches { get; private set; }

        /// <summary>Seconds left in the current phase, or 0 for a phase with no timer.</summary>
        public float PhaseSecondsRemaining => _phaseTimer > 0f ? _phaseTimer : 0f;

        /// <summary>True when the current state is worth pushing to clients this tick.</summary>
        public bool MatchStateIsDirty { get; private set; }

        /// <summary>
        /// Records a death, and awards the victim's OPPONENT for it.
        /// </summary>
        /// <param name="team">The team of the actor that died.</param>
        /// <remarks>
        /// <para>
        /// <b>The direction reversed in P11 and that is the whole defect.</b> This used to
        /// subtract a ticket from the victim's own side — the Ravenfield rule, which nothing
        /// else in this project implements. The game's own rule, the one
        /// <c>MatchScoreboard.AddScore</c> implements and <c>Actor.Die</c> feeds, awards the
        /// team OPPOSITE the victim's. The three invariants, each cheap to state and expensive
        /// to lose:
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// <b>One award, to the team opposite the victim's, keyed on the victim's team and on
        /// nothing else.</b> No branch below reads the killer, deliberately: friendly fire
        /// therefore scores for the enemy, which is the intended penalty and a stiffer one than
        /// a blocked shot. <b>Bots count the same as humans.</b>
        /// </description></item>
        /// <item><description>
        /// <b>Only an actual death scores, and a death scores once.</b> The single-fire edge is
        /// structural rather than a convention: <c>ServerActorDamageSink.ApplyDamage</c> flips
        /// <c>IsAlive</c> false, so the next call for the same actor reports <c>died:false</c>
        /// and never reaches here. <b>Do not add a scoring call in the damage path.</b>
        /// </description></item>
        /// <item><description>
        /// <b>The award is multiplied by the SCORING team's capture-point count</b>, through
        /// <see cref="ConquestScoreRule.Award"/> — the same static the offline scoreboard calls.
        /// Holding ground is what makes a kill worth anything.
        /// </description></item>
        /// </list>
        /// <para>
        /// Ignored outside <see cref="MatchPhase.Playing"/>. A warmup kill that quietly scored
        /// would make the scoreboard disagree with the round before it began, and the natural
        /// place for that to surface is a match that ends slightly early for no visible reason.
        /// </para>
        /// </remarks>
        public void ReportDeath(byte team)
        {
            if (Phase != MatchPhase.Playing) return;

            if (team == TeamId.Team0)
                _score1 += ConquestScoreRule.Award(
                    _rules.PointsPerKill, OwnedPointCount(TeamId.Team1));
            else if (team == TeamId.Team1)
                _score0 += ConquestScoreRule.Award(
                    _rules.PointsPerKill, OwnedPointCount(TeamId.Team0));
            else return;

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
                    ApplyElimination();
                    // A live round is NOT abandoned when the humans leave. The bots are still
                    // fighting, the match still resolves, and the master still gets its
                    // GS_MATCH_ENDED — which is what keeps the server's advertised state honest
                    // rather than stuck mid-round.
                    if (IsDecided()) EnterPhase(MatchPhase.Ended);
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
                (ushort)Math.Max(0, Math.Min(_score0, ushort.MaxValue)),
                (ushort)Math.Max(0, Math.Min(_score1, ushort.MaxValue)),
                (ushort)Math.Min(Math.Ceiling(PhaseSecondsRemaining), ushort.MaxValue),
                (byte)Math.Min(HumanPlayerCount, byte.MaxValue),
                (ushort)Math.Max(0, Math.Min(_rules.VictoryPoints, ushort.MaxValue)));

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

        /// <summary>Capture points <paramref name="team"/> currently holds.</summary>
        /// <remarks>
        /// <para>
        /// <b>This replaced the ticket bleed, and the replacement is the point of P11.</b>
        /// <c>DrainTickets</c> counted the same two numbers and subtracted 0.5 tickets a second
        /// from the side holding fewer. Under the margin rule the flag count is already in the
        /// score, through <see cref="ConquestScoreRule.Award"/> — holding more points makes
        /// every kill worth more, which is the offline game's own answer and the reason the
        /// bleed had nothing left to do.
        /// </para>
        /// <para>
        /// <b>The bleed was DELETED rather than kept as an ascending trickle, and that is a
        /// decision with a cost.</b> It was the only pressure that made a stalemate resolve on
        /// its own, so two evenly-matched sides that stop killing each other now play until
        /// somebody scores. That is exactly what the offline match does — it has no bleed
        /// either — and matching the offline rule is the whole purpose of this phase; a second
        /// pressure the offline game does not have would have re-opened the divergence one
        /// mechanism over. Elimination remains as the second way a round ends.
        /// </para>
        /// </remarks>
        private int OwnedPointCount(byte team)
        {
            int owned = 0;
            for (int i = 0; i < _points.Length; i++)
                if (_points[i].OwningTeam == team) owned++;

            return owned;
        }

        /// <summary>Whether the round is over, by either of the two win conditions.</summary>
        /// <remarks>
        /// <see cref="_drawn"/> is not folded into the margin test because a draw is precisely
        /// the state the margin test reports as "nobody has won yet" — reading it off the scores
        /// alone would leave a both-teams-eliminated round running forever, which is the
        /// unbounded end/reset loop X-53 was.
        /// </remarks>
        private bool IsDecided()
            => _drawn
            || ConquestScoreRule.Decide(_score0, _score1, _rules.VictoryPoints) != TeamId.None;

        /// <summary>
        /// A team holding no spawn points has lost. Phase-V8 task 4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Expressed as moving the SCORE, not as a separate end path.</b> The round then
        /// ends through the line immediately below the call site, so the phase change,
        /// <see cref="MatchEnded"/>, the broadcast and the reset all behave exactly as they do
        /// for a margin win — there is no second way for a match to end that a handler could
        /// have been written against and forgotten. Before P11 the same idea was expressed as
        /// zeroing the loser's TICKETS; the direction changed, the design did not.
        /// </para>
        /// <para>
        /// It also keeps <c>MatchStateMessage.WinningTeam</c> honest. The winner is derived from
        /// the two scores against the victory margin, so a team wiped off the map while merely
        /// level on points would otherwise be broadcast as an undecided round it had in fact
        /// just lost. Raising the survivor to exactly the margin — rather than to some larger
        /// number — is what makes the broadcast score legible: the scoreboard reads as "won by
        /// the victory margin", which is what happened.
        /// </para>
        /// <para>
        /// <see cref="Math.Max(int,int)"/> guards the survivor's own score: a team already
        /// further ahead than the margin does not have its score pulled DOWN by winning.
        /// </para>
        /// <para>
        /// Both teams eliminated — a degenerate map, or a mid-match teardown — is a draw, and
        /// it cannot be expressed in the scores at all: any pair of numbers either meets a
        /// margin (naming a winner) or does not (leaving the round running forever). So it sets
        /// <see cref="_drawn"/> and leaves the scores alone.
        /// </para>
        /// </remarks>
        private void ApplyElimination()
        {
            if (_playingElapsed <= _rules.EliminationGraceSeconds) return;
            if (_spawnPoints0 == CountsNotReported || _spawnPoints1 == CountsNotReported) return;

            bool eliminated0 = _spawnPoints0 == 0;
            bool eliminated1 = _spawnPoints1 == 0;
            if (!eliminated0 && !eliminated1) return;

            // Both at once is not a match ending, it is a map with no bases -- and read as a
            // draw it produces an unbounded end/reset loop rather than an error. That loop ran
            // 34 times in four hours on the deployed server and nothing said why, so the
            // degenerate case is announced (X-53). Once per machine lifetime: it is true on
            // every tick of every round it happens in.
            if (eliminated0 && eliminated1 && !_warnedBothEliminated)
            {
                _warnedBothEliminated = true;
                BothTeamsEliminated?.Invoke();
            }

            if (eliminated0 && eliminated1)
                _drawn = true;
            else if (eliminated0)
                _score1 = Math.Max(_score1, _score0 + _rules.VictoryPoints);
            else
                _score0 = Math.Max(_score0, _score1 + _rules.VictoryPoints);

            MatchStateIsDirty = true;
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
            _score0     = 0;
            _score1     = 0;
            _drawn      = false;
            _phaseTimer = 0f;

            for (int i = 0; i < _points.Length; i++) _points[i].Reset();
            _dirtyPoints.Clear();

            // Fired after the machine's own state is clean, so a handler that inspects the
            // machine (the clean-state audit does) sees the post-reset values rather than a
            // half-reset one.
            ResetRequested?.Invoke();
        }
    }
}
