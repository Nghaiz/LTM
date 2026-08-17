using System;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// Per-turret slew rates and elevation stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces two bare constants in the shipped code — <c>TankTurret.MAX_TURN_DELTA</c>
    /// (5) and <c>MountedTurret.MAX_TURN_DELTA</c> (10) — and naming them is what makes the
    /// bug visible: those numbers were <b>degrees per rendered frame</b>. A 144 Hz client
    /// traversed 2.4× faster than a 30 Hz one from the same mouse movement, so no two peers
    /// ever agreed where a turret was pointing.
    /// </para>
    /// <para>
    /// The rates below are stated per second, so the same input traverses the same arc on
    /// every peer at every framerate. The shipped defaults are the original per-frame values
    /// multiplied by 60 — i.e. exactly what the game does at its design framerate.
    /// </para>
    /// <para>
    /// <c>[Serializable]</c> so the values are per-prefab data rather than code: retuning a
    /// turret is an Inspector edit, not a rebuild.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct TurretAimLimits
    {
        /// <summary>Traverse rate, degrees per second, at full stick deflection.</summary>
        public float YawRateDegPerSec;

        /// <summary>Elevation rate, degrees per second, at full stick deflection.</summary>
        public float PitchRateDegPerSec;

        /// <summary>Lower elevation stop, degrees. Depression is negative.</summary>
        public float PitchMin;

        /// <summary>Upper elevation stop, degrees.</summary>
        public float PitchMax;
    }
}
