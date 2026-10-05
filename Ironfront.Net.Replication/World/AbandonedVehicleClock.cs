using System;

namespace Ironfront.Net.Replication.World
{
    /// <summary>
    /// How long a pad's vehicle has stood empty AWAY from its pad: the clock that decides when a
    /// game server may take it back and spawn a fresh one where it belongs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rule for every pad on every map (2026-10-05).</b> A pad respawns only when
    /// its vehicle is destroyed or abandoned. Abandoned is empty for
    /// <see cref="AbandonAfterSeconds"/> with nobody playing near it; the player half is a
    /// physics query the caller asks after this clock has run out, because it is the expensive
    /// half and the cheap one rules most vehicles out.
    /// </para>
    /// <para>
    /// <b>A vehicle still on its pad is never abandoned.</b> Reclaiming it would destroy a
    /// vehicle and spawn the same one in the same place: churn for every client and no vehicle
    /// gained. Bot litter is the case this exists for, and bot litter is somewhere else.
    /// </para>
    /// <para>
    /// Engine-free so <c>dotnet test</c> can hold the rule; <c>VehicleSpawner</c> lives in
    /// <c>Assembly-CSharp</c>, which no test project can reference.
    /// </para>
    /// </remarks>
    public sealed class AbandonedVehicleClock
    {
        private float _inUseAt;

        /// <param name="abandonAfterSeconds">Zero or less never abandons anything.</param>
        public AbandonedVehicleClock(float abandonAfterSeconds)
        {
            if (float.IsNaN(abandonAfterSeconds)) throw new ArgumentOutOfRangeException(nameof(abandonAfterSeconds));
            AbandonAfterSeconds = abandonAfterSeconds;
        }

        /// <summary>Seconds empty and off the pad before a vehicle counts as abandoned.</summary>
        public float AbandonAfterSeconds { get; }

        /// <summary>When the vehicle was last occupied or on its pad.</summary>
        public float InUseAt => _inUseAt;

        /// <summary>A new vehicle stands on the pad: the clock starts over.</summary>
        public void Restart(float now) => _inUseAt = now;

        /// <summary>
        /// One observation of the vehicle. True when it has been empty and off its pad for
        /// <see cref="AbandonAfterSeconds"/> or longer.
        /// </summary>
        /// <param name="now">The current time, in the same clock as <see cref="Restart"/>.</param>
        /// <param name="occupied">Somebody is in one of its seats.</param>
        /// <param name="onItsPad">It still stands where the pad spawns.</param>
        public bool Observe(float now, bool occupied, bool onItsPad)
        {
            if (AbandonAfterSeconds <= 0f) return false;

            if (occupied || onItsPad)
            {
                _inUseAt = now;
                return false;
            }

            return now - _inUseAt >= AbandonAfterSeconds;
        }
    }
}
