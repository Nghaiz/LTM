using System.Globalization;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What the "press F" prompt over a vehicle says: the vehicle's name, what the key will do,
    /// and how full it is.
    /// </summary>
    /// <remarks>
    /// Engine-free so every string is tested here rather than read off a screenshot. The client
    /// asks for the driver's seat first and walks to the next free one when it is taken
    /// (<c>ClientSeatRequester</c>), so an empty vehicle is driven and a crewed one is boarded.
    /// </remarks>
    public static class SeatPromptWording
    {
        /// <summary>The seat key, as the input manager's "Use" button binds it.</summary>
        public const string Key = "F";

        /// <summary>
        /// A vehicle's name from its scene object's: "jeep (3)" and "quadbike(Clone)" become
        /// "JEEP" and "QUAD BIKE"; an empty or unknown name falls back to the kind.
        /// </summary>
        public static string VehicleName(string? objectName, VehicleKind kind)
        {
            string name = (objectName ?? string.Empty).Replace("(Clone)", string.Empty);
            int bracket = name.IndexOf('(');
            if (bracket >= 0) name = name.Substring(0, bracket);
            name = name.Replace('_', ' ').Trim().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Trim();

            switch (name.ToLowerInvariant())
            {
                case "": return KindNoun(kind);
                case "jeep": return "JEEP";
                case "quadbike": return "QUAD BIKE";
                case "rhib": return "RHIB";
                case "helicopter": return "HELICOPTER";
                case "tank": return "TANK";
                default: return name.ToUpperInvariant();
            }
        }

        /// <summary>The noun for a kind of vehicle with no usable name.</summary>
        public static string KindNoun(VehicleKind kind)
        {
            switch (kind)
            {
                case VehicleKind.Tank: return "TANK";
                case VehicleKind.Helicopter: return "HELICOPTER";
                case VehicleKind.Boat: return "BOAT";
                default: return "VEHICLE";
            }
        }

        /// <summary>Whether the key can put this player in a seat at all.</summary>
        public static bool CanBoard(int crew, int seats) => seats > 0 && crew < seats;

        /// <summary>
        /// The main line: "DRIVE THE JEEP" when the driver's seat is free, "BOARD THE TANK" when
        /// somebody already holds it, "THE RHIB IS FULL" when nothing is left.
        /// </summary>
        public static string Action(VehicleKind kind, string name, int crew, int seats)
        {
            if (!CanBoard(crew, seats)) return "THE " + name + " IS FULL";
            if (crew > 0) return "BOARD THE " + name;
            return DriverVerb(kind) + " THE " + name;
        }

        /// <summary>
        /// The second line: how many seats, how many taken, and a warning when the crew is the
        /// other side.
        /// </summary>
        public static string Detail(int crew, int seats, bool enemyCrew)
        {
            if (seats <= 0) return string.Empty;
            if (enemyCrew) return "ENEMY CREW ABOARD";
            if (crew <= 0)
                return seats == 1
                    ? "1 SEAT"
                    : seats.ToString(CultureInfo.InvariantCulture) + " SEATS  ·  EMPTY";
            return crew.ToString(CultureInfo.InvariantCulture) + " / "
                + seats.ToString(CultureInfo.InvariantCulture) + " SEATS TAKEN";
        }

        private static string DriverVerb(VehicleKind kind)
        {
            switch (kind)
            {
                case VehicleKind.Helicopter: return "FLY";
                case VehicleKind.Boat: return "PILOT";
                default: return "DRIVE";
            }
        }
    }
}
