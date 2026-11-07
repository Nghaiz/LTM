using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// What a vehicle seat is for, as the client netcode and the HUD need to know it: the
    /// <c>Seat.Type</c> of the original game, readable from assemblies that cannot name
    /// <c>Seat</c>.
    /// </summary>
    public enum NetSeatRole : byte
    {
        /// <summary>No such seat.</summary>
        None = 0,
        Driver = 1,
        Pilot = 2,
        Gunner = 3,
        Passenger = 4,
    }

    /// <summary>
    /// The order a player's seat key walks a vehicle's seats in, and the words the HUD names them by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Driver first, then the guns, then the benches.</b> The client cannot see who sits where
    /// (occupancy is not replicated), so it asks for one seat and walks on when refused. Walking
    /// by index put the RHIB's bow gun -- seat 4 of 5 -- behind every passenger bench, so the
    /// second player aboard always got a bench and the gun was manned only when the boat was full
    /// (owner report 2026-11-07). Battlefield and the original Ravenfield both fill the driver and
    /// then the gunner, because a crewed gun is worth more to the team than a passenger.
    /// </para>
    /// </remarks>
    public static class SeatEntryOrder
    {
        /// <summary>
        /// The seat to ask for after <paramref name="refused"/> was refused, or -1 when the walk
        /// is over.
        /// </summary>
        /// <param name="roleOf">The role of each seat index, 0 to <paramref name="seatCount"/> - 1.</param>
        public static int Next(Func<int, NetSeatRole> roleOf, int seatCount, int refused)
        {
            if (roleOf == null || seatCount <= 0) return -1;

            bool passedRefused = false;
            foreach (int seat in Walk(roleOf, seatCount))
            {
                if (passedRefused) return seat;
                if (seat == refused) passedRefused = true;
            }

            return -1;
        }

        /// <summary>Every seat index once, in boarding order.</summary>
        public static System.Collections.Generic.IEnumerable<int> Walk(
            Func<int, NetSeatRole> roleOf, int seatCount)
        {
            if (seatCount <= 0) yield break;

            yield return 0;
            for (int i = 1; i < seatCount; i++)
                if (roleOf(i) == NetSeatRole.Gunner) yield return i;
            for (int i = 1; i < seatCount; i++)
                if (roleOf(i) != NetSeatRole.Gunner) yield return i;
        }

        /// <summary>The seat's name on the HUD's seat list.</summary>
        public static string Label(NetSeatRole role)
        {
            switch (role)
            {
                case NetSeatRole.Driver:    return "DRIVER";
                case NetSeatRole.Pilot:     return "PILOT";
                case NetSeatRole.Gunner:    return "GUNNER";
                case NetSeatRole.Passenger: return "PASSENGER";
                default:                    return "SEAT";
            }
        }
    }
}
