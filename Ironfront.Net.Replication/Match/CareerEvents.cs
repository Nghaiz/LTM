using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>What one resolved kill was, as the career and the achievements need it.</summary>
    /// <remarks>
    /// A plain struct of fields rather than a constructor of eighteen arguments: the server fills
    /// what it knows at the death edge, and a field it cannot know stays at its "none" value
    /// (<see cref="NoVehicle"/>, <see cref="WeaponIds.NONE"/>, -1 metres).
    /// </remarks>
    public struct CareerKill
    {
        public const ushort NoVehicle = 0;

        public ushort Killer;
        public ushort Victim;
        public bool SameTeam;
        public bool VictimIsBot;
        public bool Headshot;
        public int DistanceMetres;
        public CauseOfDeath Cause;

        /// <summary>The hand weapon that killed, <see cref="WeaponIds.NONE"/> for a vehicle or the world.</summary>
        public byte WeaponId;

        /// <summary>The vehicle the death involved (<c>DeathAttribution</c>).</summary>
        public byte VehicleType;
        public DeathDetail Detail;

        /// <summary>The vehicle the killer sat in at the kill, <see cref="NoVehicle"/> when on foot.</summary>
        public ushort KillerVehicleId;
        public byte KillerSeat;
        public byte KillerVehicleType;

        /// <summary>The weapon the victim held when they died.</summary>
        public byte VictimWeaponId;

        /// <summary>
        /// When the victim was a helicopter's pilot: how high it was above the ground, in metres.
        /// -1 for anyone else.
        /// </summary>
        public float VictimPilotHeightMetres;

        public static CareerKill World(ushort victim, CauseOfDeath cause) => new CareerKill
        {
            Killer = ushort.MaxValue,
            Victim = victim,
            Cause = cause,
            VictimPilotHeightMetres = -1f,
        };
    }

    /// <summary>A vehicle's health reached zero: who did it and how.</summary>
    public struct CareerVehicleDown
    {
        public ushort VehicleId;
        public byte VehicleType;

        /// <summary>Whoever is credited with it, or <see cref="ushort.MaxValue"/> for nobody.</summary>
        public ushort Destroyer;

        /// <summary>The vehicle was the destroyer's enemy's: its crew, or its side when empty.</summary>
        public bool EnemyVehicle;

        /// <summary>An enemy of the destroyer sat in the pilot's seat.</summary>
        public bool EnemyPilotAboard;

        /// <summary>Its height above the ground, in metres.</summary>
        public float HeightMetres;

        /// <summary>The vehicle type the destroyer sat in, <see cref="VehicleIds.NONE"/> on foot.</summary>
        public byte DestroyerVehicleType;

        /// <summary>How high the destroyer's own vehicle was above the ground, in metres.</summary>
        public float DestroyerHeightMetres;

        /// <summary>The killing blow was a tank's main gun.</summary>
        public bool ByTankMainGun;
    }

    /// <summary>One actor still in the round at its end: who the ranks are taken among.</summary>
    public readonly struct RoundActor
    {
        public RoundActor(ushort actor, byte team, bool human)
        {
            Actor = actor;
            Team = team;
            Human = human;
        }

        public ushort Actor { get; }
        public byte Team { get; }
        public bool Human { get; }
    }

    /// <summary>The weapon families the achievements name.</summary>
    public static class CareerWeapons
    {
        /// <summary>Carried guns whose shots count toward accuracy.</summary>
        public static bool IsFirearm(byte weaponId)
            => weaponId == WeaponIds.RK44 || weaponId == WeaponIds.SIND7 || weaponId == WeaponIds.SIND7_SUPPRESSED
               || weaponId == WeaponIds.EAGLE_76 || weaponId == WeaponIds.SL_DEFENDER
               || weaponId == WeaponIds.SIGNAL_DMR || weaponId == WeaponIds.RECON_LRR;

        public static bool IsPrimary(byte weaponId)
            => weaponId == WeaponIds.RK44 || weaponId == WeaponIds.EAGLE_76 || weaponId == WeaponIds.SL_DEFENDER
               || weaponId == WeaponIds.SIGNAL_DMR || weaponId == WeaponIds.RECON_LRR;

        public static bool IsPistol(byte weaponId)
            => weaponId == WeaponIds.SIND7 || weaponId == WeaponIds.SIND7_SUPPRESSED;

        public static bool IsLauncher(byte weaponId)
            => weaponId == WeaponIds.BEU_AW1 || weaponId == WeaponIds.BIL_SCALPEL;

        /// <summary>
        /// The thrown grenades: FRAG and SPEARHEAD, the explosive one with the bigger pouch. CROWD
        /// CONTROL, FROM THE GRAVE and JACK OF ALL TRADES say "a grenade", and counted FRAG alone
        /// until the owner's run of 2026-10-10.
        /// </summary>
        public static bool IsGrenade(byte weaponId)
            => weaponId == WeaponIds.FRAG || weaponId == WeaponIds.SPEARHEAD;

        /// <summary>The rifles COUNTER-SNIPER's victim must be holding.</summary>
        public static bool IsSniperRifle(byte weaponId)
            => weaponId == WeaponIds.SL_DEFENDER || weaponId == WeaponIds.SIGNAL_DMR || weaponId == WeaponIds.RECON_LRR;

        /// <summary>ARMOURER's twelve: every carried weapon that can kill.</summary>
        public static bool CountsForArmourer(byte weaponId)
            => IsFirearm(weaponId) || IsLauncher(weaponId) || weaponId == WeaponIds.FRAG
               || weaponId == WeaponIds.SPEARHEAD || weaponId == WeaponIds.WRENCH;
    }
}
