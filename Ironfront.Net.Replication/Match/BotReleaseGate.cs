using System;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// Decides when AI bodies are allowed to exist: never before a human player is in the
    /// world, and not until <see cref="DelaySeconds"/> after the first one arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> The capture arithmetic is phase-gated to
    /// <see cref="MatchPhase.Playing"/>; the bot roster never was. <c>ActorManager.StartGame</c>
    /// instantiated <c>team0Bots + team1Bots</c> at SCENE LOAD and started a respawn wave every
    /// <c>spawnTime</c> seconds, so a round opened onto a map that 32 unsupervised bots had
    /// already been walking across for the whole of WaitingForPlayers plus warmup. Measured on
    /// Dustbowl 2026-09-20: the map opened correctly at one point per team, and then the four
    /// neutral points fell to bots at roughly 34s, 65s, 92s and 115s while the three human
    /// clients captured nothing. By 118s every point on the map was owned and none were
    /// neutral. The objective game was being played, and decided, by bots — before a player
    /// had finished choosing a loadout.
    /// </para>
    /// <para>
    /// <b>Why a gate on EXISTENCE rather than on orders.</b> Holding bots at their HQ during
    /// warmup leaves 32 bodies that walk out the instant the round opens, which moves the
    /// takeover by seconds rather than preventing it. Resetting point ownership at the whistle
    /// is worse: the bodies are still standing inside the radii, so the points re-flip
    /// immediately. Only "no bot exists yet" actually leaves the opening state observable.
    /// </para>
    /// <para>
    /// <b>No timeout, by decision.</b> If nobody ever spawns, nothing is ever released: no
    /// humans, no bots. That cannot strand a server, because an unattended one sits in
    /// <see cref="MatchPhase.WaitingForPlayers"/> where capture does not tick at all, so there
    /// is no state left to corrupt while the gate is shut.
    /// </para>
    /// <para>
    /// <b>One anchor for both teams.</b> The first player body to enter the world releases
    /// BOTH sides' bots, whichever team it belonged to. Anchoring each side on its own first
    /// player would hand the map to whichever team happened to be occupied first, which is the
    /// same unopposed-capture failure one level over.
    /// </para>
    /// <para>
    /// <b>Queried, not ticked.</b> <see cref="IsReleasedAt"/> is a pure comparison, so the
    /// caller needs no update loop and the gate allocates nothing. That matters: the shipped
    /// <c>spawnTime</c> is 0.1, so the respawn wave consults this ten times a second.
    /// </para>
    /// </remarks>
    public sealed class BotReleaseGate
    {
        /// <summary>
        /// Seconds between the first player entering the world and bots being allowed to spawn.
        /// </summary>
        public const float DefaultDelaySeconds = 30f;

        private readonly float _delaySeconds;

        // Nullable rather than a sentinel: 0f is a legitimate anchor (Time.time is 0 on the
        // frame a scene loads), so "unset" and "set at zero" must not be the same value.
        private float? _anchorSeconds;

        public BotReleaseGate(float delaySeconds = DefaultDelaySeconds)
        {
            if (delaySeconds < 0f) throw new ArgumentOutOfRangeException(nameof(delaySeconds));

            _delaySeconds = delaySeconds;
        }

        /// <summary>Seconds the gate waits after <see cref="NotifyPlayerSpawned"/>.</summary>
        public float DelaySeconds => _delaySeconds;

        /// <summary>Whether a player body has entered the world since the last <see cref="Reset"/>.</summary>
        public bool HasAnchor => _anchorSeconds.HasValue;

        /// <summary>
        /// Records that a player body has entered the world.
        /// </summary>
        /// <remarks>
        /// Idempotent within a round: only the FIRST call anchors the clock. Every later spawn
        /// — a respawn, or another player joining — must not push the release further out, or a
        /// busy server would never release at all.
        /// </remarks>
        public void NotifyPlayerSpawned(float nowSeconds)
        {
            if (_anchorSeconds.HasValue) return;

            _anchorSeconds = nowSeconds;
        }

        /// <summary>Whether bots may exist at <paramref name="nowSeconds"/>.</summary>
        public bool IsReleasedAt(float nowSeconds)
            => _anchorSeconds.HasValue && nowSeconds - _anchorSeconds.Value >= _delaySeconds;

        /// <summary>
        /// Seconds remaining before release, or <see cref="float.PositiveInfinity"/> while no
        /// player has spawned.
        /// </summary>
        /// <remarks>
        /// Infinity rather than the delay: "nobody has spawned" is not "30 seconds to go", and
        /// a caller logging a countdown must be able to tell the two apart.
        /// </remarks>
        public float SecondsUntilRelease(float nowSeconds)
        {
            if (!_anchorSeconds.HasValue) return float.PositiveInfinity;

            float remaining = _delaySeconds - (nowSeconds - _anchorSeconds.Value);
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>
        /// Re-arms the gate for a new round.
        /// </summary>
        /// <remarks>
        /// Called on the match reset. Without it the delay protects the FIRST round only:
        /// <c>MatchStateMachine.PerformReset</c> restores every point's opening owner but does
        /// nothing to the bodies, so round two would reopen with the previous round's bots
        /// standing exactly where they finished and a gate that had nothing left to delay.
        /// </remarks>
        public void Reset() => _anchorSeconds = null;
    }
}
