namespace Ironfront.Net.Replication.Ai
{
    /// <summary>What a vehicle is for, as far as a squad deciding whether to take it cares.</summary>
    public enum VehicleKind
    {
        /// <summary>A car: a faster way to the objective.</summary>
        Transport = 0,

        /// <summary>A tank: firepower and armour, worth taking to any fight.</summary>
        Armour = 1,

        /// <summary>A helicopter: a faster way, over everything.</summary>
        Aircraft = 2,

        /// <summary>A boat: the way across water.</summary>
        Boat = 3,
    }

    /// <summary>
    /// When a squad takes a vehicle, and how a tank fights once it has one. Phase P28, part 3: the
    /// owner asked for vehicles used with a purpose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the original did.</b> A squad with nothing to do boarded any vehicle within 150 m
    /// that had seats for all of it -- a squad defending its own flag walked off to a jeep, a squad
    /// thirty metres from its objective walked a hundred to a car -- and a tank drove its path to
    /// the flag, into point-blank range of every defender and their rockets.
    /// </para>
    /// <para>
    /// <b>Now.</b> A squad holding a flag or sneaking round the side stays on foot; a transport is
    /// taken only for a trip long enough to be worth it; no vehicle is worth a detour of more than
    /// half the trip it saves; and a tank with an enemy in its sights inside
    /// <see cref="ArmourStandoff"/> stops and fires from there.
    /// </para>
    /// <para>
    /// <b>A squad sent to hold a FAR flag rides there (phase P32).</b> "Holding a flag stays on
    /// foot" was written for a squad standing at the flag it holds, and refused a squad ordered to
    /// hold one four hundred metres away just as firmly: bots respawned at an HQ beside a jeep for
    /// that and walked the whole way. Defending now refuses a vehicle only within
    /// <see cref="RideDistance"/> of the flag, the same line an attack rides from.
    /// </para>
    /// </remarks>
    public static class VehicleRules
    {
        /// <summary>A squad rides a transport only to an objective at least this far, in metres.</summary>
        public const float RideDistance = 150f;

        /// <summary>The longest walk to a vehicle, as a share of the trip to the objective.</summary>
        public const float MaxDetourShare = 0.5f;

        /// <summary>Metres of walk to a vehicle allowed on top of the share, so a tank at hand is taken.</summary>
        public const float DetourSlack = 25f;

        /// <summary>A tank with a target inside this range, in metres, stops and fires.</summary>
        public const float ArmourStandoff = 80f;

        /// <summary>
        /// Whether a squad takes a vehicle <paramref name="vehicleDistance"/> metres away, with its
        /// objective <paramref name="objectiveDistance"/> away (infinite when it has none, which
        /// keeps the original's "take what is near" for a squad nobody has given a job).
        /// </summary>
        public static bool ShouldBoard(VehicleKind kind, SquadRole role, float objectiveDistance, float vehicleDistance)
        {
            if (role == SquadRole.Flank)
            {
                return false;
            }
            if (role == SquadRole.Defend && objectiveDistance < RideDistance)
            {
                return false;
            }
            if (vehicleDistance > objectiveDistance * MaxDetourShare + DetourSlack)
            {
                return false;
            }
            return kind == VehicleKind.Armour || objectiveDistance >= RideDistance;
        }

        /// <summary>
        /// Where a kind of vehicle stands in a squad's choice, best first: a helicopter, then a
        /// tank, then a transport, then a boat. Owner, 2026-10-08: bots should look for the best
        /// vehicles first, helicopters and tanks, and only then jeeps and motorbikes.
        /// </summary>
        public static int PreferenceRank(VehicleKind kind)
        {
            switch (kind)
            {
                case VehicleKind.Aircraft: return 0;
                case VehicleKind.Armour: return 1;
                case VehicleKind.Transport: return 2;
                default: return 3;
            }
        }

        /// <summary>
        /// Orders two vehicles a squad could take: the better kind first (<see cref="PreferenceRank"/>),
        /// then the sturdier of the same kind (a jeep before a quad bike), then the nearer. The
        /// original took whichever was nearest.
        /// </summary>
        public static int ComparePreference(
            VehicleKind kindA, float maxHealthA, float distanceA,
            VehicleKind kindB, float maxHealthB, float distanceB)
        {
            int rank = PreferenceRank(kindA).CompareTo(PreferenceRank(kindB));
            if (rank != 0) return rank;
            int sturdier = maxHealthB.CompareTo(maxHealthA);
            if (sturdier != 0) return sturdier;
            return distanceA.CompareTo(distanceB);
        }

        /// <summary>Whether a vehicle's driver stops to let its gun work instead of driving on.</summary>
        public static bool HoldStandoff(VehicleKind kind, bool hasTarget, float targetDistance)
            => kind == VehicleKind.Armour && hasTarget && targetDistance < ArmourStandoff;
    }
}
