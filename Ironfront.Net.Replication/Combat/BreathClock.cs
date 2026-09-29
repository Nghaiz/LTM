using System;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// How long a body can stay in water: a breath that drains while it is in water -- swimming
    /// at the surface as much as sinking -- and refills on land, and the damage it takes once
    /// the breath is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner ruling 2026-09-29</b>: no more drowning after eight seconds with the head under
    /// (the X-90 clock this replaces). Players and bots swim instead, and a breath bar shows how
    /// long they have: long enough to reach land from a sinking boat, car or helicopter within a
    /// reasonable distance, and draining even at the surface, so nobody can sit in the water.
    /// When it is empty the body loses health until it dies or gets out; it does not simply fall
    /// dead at zero.
    /// </para>
    /// <para>
    /// <b>The numbers.</b> <see cref="CapacitySeconds"/> of swimming at the original's
    /// 2.4 m/s covers about 96 m, and the <see cref="DamagePerSecond"/> that follows gives a
    /// full-health body five more seconds: land within about a hundred metres is reachable,
    /// further is not. <see cref="RefillSeconds"/> on land fills it again from empty.
    /// </para>
    /// <para>
    /// <b>Shared by the server and the client</b>, like <c>MovementCore</c>: the server runs one
    /// per actor and deals the damage; the client runs one for its own player to draw the bar, so
    /// the bar empties when the server's does.
    /// </para>
    /// </remarks>
    public sealed class BreathClock
    {
        /// <summary>Seconds of breath a body has in water from full.</summary>
        public const float CapacitySeconds = 40f;

        /// <summary>Seconds on land that fill the breath from empty.</summary>
        public const float RefillSeconds = 10f;

        /// <summary>Health lost per second in water once the breath is gone.</summary>
        public const float DamagePerSecond = 20f;

        /// <summary>Seconds of breath left.</summary>
        public float Remaining { get; private set; } = CapacitySeconds;

        /// <summary>The breath left, from 1 (full) to 0 (none).</summary>
        public float Fraction => Remaining / CapacitySeconds;

        /// <summary>Whether the breath is gone.</summary>
        public bool IsEmpty => Remaining <= 0f;

        /// <summary>
        /// Advances the breath by <paramref name="deltaSeconds"/> spent in water or out of it,
        /// and returns the health that time costs: zero while any breath is left.
        /// </summary>
        public float Tick(bool inWater, float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return 0f;

            if (!inWater)
            {
                Remaining = Math.Min(CapacitySeconds, Remaining + deltaSeconds * (CapacitySeconds / RefillSeconds));
                return 0f;
            }

            float left = Remaining - deltaSeconds;
            if (left >= 0f)
            {
                Remaining = left;
                return 0f;
            }

            // Only the part of the step spent with no breath left hurts.
            Remaining = 0f;
            return -left * DamagePerSecond;
        }

        /// <summary>Fills the breath: a respawned body starts with all of it.</summary>
        public void Reset() => Remaining = CapacitySeconds;
    }
}
