using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Latches the last <see cref="MatchStateMessage"/> and answers the two questions the HUD
    /// asks between broadcasts: how many seconds are left, and is this number still worth
    /// believing. phase-V10 task 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The phase timer is interpolated, and during <see cref="MatchPhase.Playing"/> there is
    /// no timer at all.</b> <c>PhaseSecondsRemaining</c> is 0 in that phase by design — it ends
    /// on tickets, not a clock — so a HUD that renders the field unconditionally shows every
    /// player "0:00" for the whole round. <see cref="HasTimer"/> is the gate; rendering a zero
    /// is the bug this type exists to prevent.
    /// </para>
    /// <para>
    /// <b>Staleness is reported, never smoothed away.</b> The message arrives at most once a
    /// second, so the count-down between broadcasts is this type's arithmetic. If broadcasts
    /// stop, the arithmetic keeps running and would happily reach zero and announce a round
    /// that did not end. <see cref="IsStale"/> lets the HUD dim rather than lie —
    /// "Errors Over Silent Fallbacks", applied to a clock.
    /// </para>
    /// </remarks>
    public sealed class MatchStateModel
    {
        /// <summary>
        /// How long after the last broadcast the latched state stops being believable. The
        /// server sends on any phase change and otherwise at most once a second, so three
        /// seconds is three missed sends — well past jitter, well short of a hung HUD.
        /// </summary>
        public const float DefaultStaleAfterSeconds = 3f;

        private MatchStateMessage _current;
        private float _receivedAtSeconds;
        private bool _hasAny;

        /// <summary>Seconds without a broadcast before <see cref="IsStale"/> turns true.</summary>
        public float StaleAfterSeconds { get; set; } = DefaultStaleAfterSeconds;

        /// <summary>Whether any match state has arrived yet.</summary>
        public bool HasState => _hasAny;

        /// <summary>The last message received. Default until <see cref="HasState"/> is true.</summary>
        public MatchStateMessage Current => _current;

        /// <summary>Broadcasts applied this connection.</summary>
        public long AppliedCount { get; private set; }

        /// <summary>Latches a broadcast and restarts the interpolation clock.</summary>
        public void Apply(in MatchStateMessage message, float nowSeconds)
        {
            _current           = message;
            _receivedAtSeconds = nowSeconds;
            _hasAny            = true;
            AppliedCount++;
        }

        /// <summary>
        /// Whether this phase has a countdown to draw at all. False during
        /// <see cref="MatchPhase.Playing"/> and before the first broadcast.
        /// </summary>
        public bool HasTimer => _hasAny && _current.Phase != MatchPhase.Playing;

        /// <summary>
        /// Seconds left in the current phase, counted down from the last broadcast so the
        /// display moves every frame rather than once a second. Clamped at zero; always zero
        /// when <see cref="HasTimer"/> is false, so a caller that forgets the gate renders a
        /// stopped clock rather than a wrong one.
        /// </summary>
        public float SecondsRemaining(float nowSeconds)
        {
            if (!HasTimer) return 0f;

            float elapsed = nowSeconds - _receivedAtSeconds;
            if (elapsed < 0f) elapsed = 0f;

            float left = _current.PhaseSecondsRemaining - elapsed;
            return left > 0f ? left : 0f;
        }

        /// <summary>
        /// Whether the latched state is too old to show as live. True before the first
        /// broadcast: unknown is not good.
        /// </summary>
        public bool IsStale(float nowSeconds)
            => !_hasAny || nowSeconds - _receivedAtSeconds >= StaleAfterSeconds;

        /// <summary>
        /// Who won, or <see cref="TeamId.None"/> while undecided or genuinely drawn. Delegates
        /// to the message's own computed property so the boundary is defined once, on the wire
        /// type, for both sides.
        /// </summary>
        public byte WinningTeam => _hasAny ? _current.WinningTeam : TeamId.None;

        /// <summary>Drops the latched state. Call when leaving a match.</summary>
        public void Reset()
        {
            _current           = default;
            _receivedAtSeconds = 0f;
            _hasAny            = false;
            AppliedCount       = 0;
        }
    }
}
