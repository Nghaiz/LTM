using System;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// When a game server should try to register with the master again after losing the link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registration used to be a once-per-process event.</b> <c>MasterLinkBootstrap</c>
    /// connected during <c>Start</c> and, whatever happened afterwards, never dialled again —
    /// and losing the link was silent, because <c>GameServerMatchReporter</c> guards Heartbeat,
    /// MatchStarted and MatchEnded with <c>if (!IsConnected) return;</c>. So a server whose link
    /// died went on playing complete matches while reporting nothing, and the master, seeing the
    /// heartbeats stop, dropped it. Both ends then agreed there was no server and disagreed
    /// about whether anything was wrong.
    /// </para>
    /// <para>
    /// <b>Observed on 2026-09-14.</b> A VMware host holding two registered game servers was
    /// suspended for some hours. The master marked both unhealthy and dropped them
    /// (<c>gsRegistered</c> 2 to 0). On resume both processes carried on with dead sockets, never
    /// re-registered, and stayed invisible to matchmaking for the rest of their lifetime. A
    /// suspended VM is only the cheapest way to produce this — a master restart, a NAT rebind or
    /// a dropped wireless link do the same thing.
    /// </para>
    /// <para>
    /// <b>A plain class, not part of the MonoBehaviour.</b> Every decision here is arithmetic
    /// over elapsed time, and a MonoBehaviour would put all of it out of reach of the EditMode
    /// suite — the component's own entry point is <c>Start</c>, which resolves configuration
    /// from the environment and needs a scene. The component keeps the parts that genuinely need
    /// Unity: the clock, the socket and the logging.
    /// </para>
    /// </remarks>
    public sealed class MasterLinkKeepAlive
    {
        private readonly float _minRetrySeconds;
        private readonly float _maxRetrySeconds;

        private float _retryIn = -1f;
        private int _attempt;

        /// <param name="minRetrySeconds">
        /// The first delay. Short, because the common case is a master that bounced and is
        /// already back.
        /// </param>
        /// <param name="maxRetrySeconds">
        /// The ceiling. A minute is well inside the master's own grace for a returning server and
        /// is far enough apart that an hour of downtime costs a few dozen attempts rather than a
        /// few thousand.
        /// </param>
        public MasterLinkKeepAlive(float minRetrySeconds = 5f, float maxRetrySeconds = 60f)
        {
            if (minRetrySeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(minRetrySeconds));

            if (maxRetrySeconds < minRetrySeconds)
                throw new ArgumentOutOfRangeException(nameof(maxRetrySeconds));

            _minRetrySeconds = minRetrySeconds;
            _maxRetrySeconds = maxRetrySeconds;
        }

        /// <summary>
        /// This process decided it should be advertised.
        /// </summary>
        /// <remarks>
        /// Distinct from "is it currently linked". Everything that makes a server standalone by
        /// design — a declared client, no configured master, no shared secret, a rejected
        /// configuration — leaves this false, so no amount of elapsed time produces an attempt.
        /// A master that is merely down leaves it true, and that is the case that must retry.
        /// </remarks>
        public bool WantsLink { get; set; }

        /// <summary>Seconds until the next attempt, or a negative value when none is armed.</summary>
        public float RetryInSeconds => _retryIn;

        /// <summary>Consecutive failures since the last registration that landed.</summary>
        public int Attempt => _attempt;

        /// <summary>A registration landed: disarm the retry and forget the backoff.</summary>
        /// <remarks>
        /// The counter is cleared on success rather than accumulated over the process lifetime,
        /// so the backoff measures CONSECUTIVE failures. A link that flaps once an hour would
        /// otherwise creep up to the ceiling and take a full minute to recover from a blip it
        /// used to recover from in five seconds.
        /// </remarks>
        public void OnRegistered()
        {
            _attempt = 0;
            _retryIn = -1f;
        }

        /// <summary>A live registration was lost, or an attempt failed. Arms the next one.</summary>
        /// <remarks>
        /// Doubling from the floor to the ceiling. No jitter, deliberately: the thundering herd
        /// it would defend against needs many servers losing the same master in the same instant,
        /// and a fleet this size reconnecting within a second of each other costs the master less
        /// than an unpredictable delay costs an operator reading the log.
        /// </remarks>
        public void OnLinkDown()
        {
            _attempt++;

            // Shifted rather than looped, and capped at 8 so the shift cannot overflow into a
            // negative multiplier on a server that has been retrying for a long time. The Min
            // below would then hand back a NEGATIVE delay, which reads as "due immediately" and
            // turns the backoff into a hot loop against a master that is already struggling.
            float delay = _minRetrySeconds * (1 << Math.Min(_attempt - 1, 8));

            _retryIn = Math.Min(delay, _maxRetrySeconds);
        }

        /// <summary>
        /// Ages the armed retry by one frame and reports whether an attempt is due now.
        /// </summary>
        /// <remarks>
        /// Returns true at most once per arming: the countdown is disarmed as it fires, so a
        /// caller that keeps ticking does not start a second attempt while the first is still in
        /// flight. The next attempt is armed by <see cref="OnLinkDown"/> when that one fails.
        /// </remarks>
        public bool ShouldAttempt(float deltaSeconds)
        {
            if (!WantsLink || _retryIn < 0f) return false;

            _retryIn -= deltaSeconds;
            if (_retryIn > 0f) return false;

            _retryIn = -1f;
            return true;
        }
    }
}
