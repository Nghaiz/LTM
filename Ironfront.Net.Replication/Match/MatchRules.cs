namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// Every tunable number the match lifecycle uses, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="MatchStateMachine"/> so a test can state which rule it is
    /// exercising by name rather than by a literal, and so the two places that need
    /// <see cref="VictoryPoints"/> — the machine and the reset audit — cannot drift apart.
    /// </para>
    /// <para>
    /// These are game-design values, not protocol values, so they deliberately do NOT live in
    /// <c>ProtocolConstants</c>: changing the warmup length must not be a protocol change.
    /// Two of them are SENT rather than shared, and neither moves here: the capture ownership
    /// threshold lives on <c>CapturePointMessage</c>, next to the byte it is a threshold on,
    /// and <see cref="VictoryPoints"/> is written into every <c>S_MATCH_STATE</c> beside the
    /// two scores it scales — because a client cannot draw the score bar without it and a host
    /// can change it per match, so it can be neither assumed nor made constant.
    /// </para>
    /// </remarks>
    public sealed class MatchRules
    {
        /// <summary>Humans required before warmup begins. Bots do not count.</summary>
        public int MinPlayersToStart { get; set; } = 2;

        /// <summary>Countdown before the round opens.</summary>
        public float WarmupSeconds { get; set; } = 20f;

        /// <summary>Scoreboard time after the round is decided, before the world resets.</summary>
        public float PostMatchSeconds { get; set; } = 20f;

        /// <summary>
        /// The lead one team needs over the other to win. The original
        /// <c>GameManager.victoryPoints</c>.
        /// </summary>
        /// <remarks>
        /// <b>The number did not change; the verb did.</b> This was <c>StartTickets</c>, and its
        /// own remark already called it "the original <c>GameManager.victoryPoints</c>" — the
        /// same 200, under the wrong verb. P11 stopped it counting DOWN from 200 to zero and
        /// made it the ASCENDING margin the offline match has always used
        /// (<c>MatchScoreboard.VictoryPoints</c>), so the networked and offline runtimes stop
        /// playing two different games. Renamed rather than reused in place, because a field
        /// called <c>tickets</c> holding a score is how the next reader re-introduces the bug.
        /// </remarks>
        public int VictoryPoints { get; set; } = 200;

        /// <summary>Points a team is awarded when an actor of the OTHER team dies.</summary>
        /// <remarks>
        /// Multiplied by the SCORING team's capture-point count before it lands — see
        /// <c>ConquestScoreRule.Award</c>. The award is keyed on the victim's team and on
        /// nothing else, so a team-kill hands the enemy a point; that is deliberate and is the
        /// whole friendly-fire penalty.
        /// </remarks>
        public int PointsPerKill { get; set; } = 1;

        /// <summary>
        /// Seconds a released actor id stays unusable. Phase-03 trap 2: a client with stale
        /// packets in flight would otherwise apply the previous actor's state to whoever takes
        /// the id next.
        /// </summary>
        public float ActorIdQuarantineSeconds { get; set; } = 5f;

        /// <summary>
        /// Smallest ownership change worth a <c>S_CAPTURE_POINT</c> message. Phase-03 trap 3:
        /// sending every tick is 5 points x 30 Hz x 16 clients = 2400 messages a second for a
        /// bar that moves too slowly to see it.
        /// </summary>
        public float CaptureSendThreshold { get; set; } = 0.02f;

        /// <summary>
        /// Most attackers whose presence still speeds up a capture. Without the cap, sixteen
        /// players standing on a point take it instantly.
        /// </summary>
        public int MaxCaptureHeadcount { get; set; } = 4;

        /// <summary>
        /// Seconds after a round opens before losing every spawn point can end it.
        /// </summary>
        /// <remarks>
        /// Mirrors the original's <c>ElapsedGameTime() &gt; 1f</c> guard in
        /// <c>ScoreUi.AddFlag</c>. Without it a map whose points all start neutral has both
        /// teams at zero owned spawn points on tick one and the round ends as a draw before
        /// anybody has moved.
        /// </remarks>
        public float EliminationGraceSeconds { get; set; } = 1f;

        /// <summary>The shipped ruleset. Mutating this is a global change; prefer a new instance.</summary>
        public static MatchRules Default => new MatchRules();
    }
}
