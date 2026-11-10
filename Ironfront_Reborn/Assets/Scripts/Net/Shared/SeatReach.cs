using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How far a body is from a vehicle for the seat reach check: from its NEAREST seat, whichever
    /// seat it asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not the seat asked for.</b> The client asks for the driver's seat first and walks to
    /// the next on a refusal, and both sides measured the reach to the seat asked for. On the RHIB
    /// the driver sits aft, 4.9 m from the bow, a swimmer's capsule centre floats 0.7 m under the
    /// surface and the seats ride 0.5 m over it: for a swimmer 1 to 2 m off the hull, the driver's
    /// seat was out of the 6 m reach for 19 to 27 % of the way round it, the bow end, while some
    /// seat was always in reach. There the prompt never showed and the key did nothing (owner
    /// report 2026-11-10, "sometimes I can't get on the boat while swimming"). The
    /// reach check exists to refuse a client asking to board a vehicle across the map, not to
    /// decide which end of it a player may climb in at.
    /// </para>
    /// <para>
    /// One implementation for both readers, as X-67 requires of the seat position itself: the
    /// client deciding what to offer and the server deciding what to grant measure the same thing.
    /// </para>
    /// </remarks>
    public static class SeatReach
    {
        /// <summary>
        /// The squared distance from <paramref name="from"/> to the nearest of
        /// <paramref name="seatCount"/> seats, or positive infinity when none can be located.
        /// </summary>
        /// <param name="seatAt">A seat's world position; a coordinate with an infinite component for a seat that is not there.</param>
        public static float NearestSquared(Vector3 from, int seatCount, Func<int, Vector3> seatAt)
        {
            float best = float.PositiveInfinity;
            if (seatAt == null) return best;

            for (int i = 0; i < seatCount; i++)
            {
                Vector3 seat = seatAt(i);
                if (float.IsInfinity(seat.x) || float.IsInfinity(seat.y) || float.IsInfinity(seat.z)
                    || float.IsNaN(seat.x) || float.IsNaN(seat.y) || float.IsNaN(seat.z))
                {
                    continue;
                }
                float squared = (seat - from).sqrMagnitude;
                if (squared < best) best = squared;
            }
            return best;
        }
    }
}
