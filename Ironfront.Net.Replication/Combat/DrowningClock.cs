namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// How long one actor has been under water, and the single moment that becomes a death.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deep water is a rule now, not terrain.</b> Before this, an actor's head going under was
    /// a presentation state and nothing else: the shipped single-player body ragdolls and swims,
    /// and the networked body -- since the fix that stopped water taking its controls away --
    /// simply walks the bottom indefinitely, because buoyancy and the swim animation both live on
    /// ragdoll rigidbodies it no longer enables. An actor that can stand on the seabed forever is
    /// not a swimming model; it is the absence of one. This supplies the missing half as a rule
    /// the server owns.
    /// </para>
    /// <para>
    /// <b><c>CauseOfDeath.Drown</c> already existed on the wire and nothing had ever produced
    /// it.</b> Zero references across the repository: declared in the protocol, never emitted,
    /// never consumed. So this is a wiring job rather than a protocol change -- no version bump,
    /// no new enum value, and every client already knows how to read the cause it will now start
    /// receiving.
    /// </para>
    /// <para>
    /// <b>Engine-free, for the same reason <c>VehicleBurnClock</c> is.</b> The rule is arithmetic
    /// over a bool and a delta, so it belongs where CI can run it rather than behind an Editor.
    /// The Unity side supplies "is this actor's head under water" and the elapsed time, and owns
    /// nothing else about the decision.
    /// </para>
    /// <para>
    /// <b>It fires exactly once per submersion.</b> <see cref="Tick"/> returns true on the tick
    /// that crosses the limit and never again until the actor surfaces or is
    /// <see cref="Reset"/>. Without that latch the caller would emit a death every tick for as
    /// long as the corpse stayed under, which is the shape that turns one drowning into a
    /// killfeed that never stops.
    /// </para>
    /// </remarks>
    public sealed class DrowningClock
    {
        /// <summary>
        /// Seconds a head may stay under water before it kills.
        /// </summary>
        /// <remarks>
        /// Long enough to swim a river and short enough that nobody is stuck: the failure this
        /// rule replaces was a player with no way out at all, so a limit that reads as generous
        /// is the correct side to err on. It is a named constant rather than a literal because
        /// it is a tuning decision, and the one place to change it is here.
        /// </remarks>
        public const float DefaultSecondsUnderwater = 8f;

        private readonly float _limit;
        private float _submergedSeconds;
        private bool _fired;

        public DrowningClock(float secondsUnderwater = DefaultSecondsUnderwater)
        {
            _limit = secondsUnderwater > 0f ? secondsUnderwater : DefaultSecondsUnderwater;
        }

        /// <summary>Seconds the head has been continuously under water.</summary>
        public float SubmergedSeconds => _submergedSeconds;

        /// <summary>The limit this clock was built with.</summary>
        public float Limit => _limit;

        /// <summary>Whether this submersion has already produced its death.</summary>
        public bool HasDrowned => _fired;

        /// <summary>
        /// Advances the clock and reports whether this is the tick the actor drowns on.
        /// </summary>
        /// <param name="submerged">Whether the actor's head is under water right now.</param>
        /// <param name="deltaSeconds">Elapsed time since the previous call.</param>
        /// <remarks>
        /// <para>
        /// <b>Surfacing resets rather than pauses.</b> A player who comes up for air has survived;
        /// carrying the accumulated seconds forward would drown them on a later, shorter dip for
        /// reasons they cannot see. Partial credit is how a rule stops being legible.
        /// </para>
        /// <para>
        /// A non-positive <paramref name="deltaSeconds"/> advances nothing. A paused or
        /// rewound clock must not drown anybody.
        /// </para>
        /// </remarks>
        public bool Tick(bool submerged, float deltaSeconds)
        {
            if (!submerged)
            {
                Reset();
                return false;
            }

            if (deltaSeconds > 0f) _submergedSeconds += deltaSeconds;

            if (_fired || _submergedSeconds < _limit) return false;

            _fired = true;
            return true;
        }

        /// <summary>
        /// Forgets this submersion entirely. Call on respawn.
        /// </summary>
        /// <remarks>
        /// A body that drowned and respawned must start from zero even if the new life begins in
        /// water, or the latch above would suppress the second death for ever.
        /// </remarks>
        public void Reset()
        {
            _submergedSeconds = 0f;
            _fired = false;
        }
    }
}
