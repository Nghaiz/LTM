namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// Every tunable number the match lifecycle uses, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="MatchStateMachine"/> so a test can state which rule it is
    /// exercising by name rather than by a literal, and so the two places that need
    /// <see cref="StartTickets"/> — the machine and the reset audit — cannot drift apart.
    /// </para>
    /// <para>
    /// These are game-design values, not protocol values, so they deliberately do NOT live in
    /// <c>ProtocolConstants</c>: changing the warmup length must not be a protocol change.
    /// The one number that crosses the wire, the capture ownership threshold, lives on
    /// <c>CapturePointMessage</c> instead, next to the byte it is a threshold on.
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
        /// Tickets each team starts with. The original <c>GameManager.victoryPoints</c>.
        /// </summary>
        public int StartTickets { get; set; } = 200;

        /// <summary>Tickets a team loses when one of its actors dies.</summary>
        public int TicketsPerDeath { get; set; } = 1;

        /// <summary>
        /// Tickets per second the losing side bleeds, per capture point of advantage.
        /// </summary>
        /// <remarks>
        /// At 0.5 and a three-point lead, a 200-ticket team is bled dry in roughly two
        /// minutes, which is the right order for a demo round and is what the phase-03 load
        /// scenario ("20 minutes of play") assumes when it expects several matches inside the
        /// window.
        /// </remarks>
        public float BleedPerPointPerSecond { get; set; } = 0.5f;

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

        /// <summary>The shipped ruleset. Mutating this is a global change; prefer a new instance.</summary>
        public static MatchRules Default => new MatchRules();
    }
}
