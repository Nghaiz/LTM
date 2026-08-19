namespace Ironfront.Net.Unity
{
    /// <summary>
    /// What a mounted weapon's own prefab says about it, handed to the server once. V6 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These numbers do NOT come from <c>WeaponCatalog</c>, and that is not an oversight.</b>
    /// The catalog maps the eighteen ids in the loadout registry to the server's numbers, and a
    /// turret bolted to a vehicle is not in that registry — most carry <c>WeaponIds.NONE</c> and
    /// have no row to read. Their numbers live on the component, on a prefab the SERVER itself
    /// instantiated, so reading them there is reading the server's own scene rather than trusting
    /// a client.
    /// </para>
    /// <para>
    /// A struct rather than six parameters, so adding a seventh number later does not touch every
    /// call site — and so the two sides of the seam cannot silently disagree about argument order,
    /// which for a pair of numbers as similar as clip size and spare ammo is a real hazard.
    /// </para>
    /// </remarks>
    public readonly struct MountedWeaponDeclaration
    {
        /// <summary>Its wire id, or <c>WeaponIds.NONE</c> for a turret with no registry row.</summary>
        public readonly byte WeaponId;

        /// <summary>Rounds in a full clip. <c>Weapon.Configuration.ammo</c>.</summary>
        public readonly byte ClipSize;

        /// <summary>
        /// Rounds outside the clip, or a sentinel. <c>Weapon.Configuration.spareAmmo</c>, whose
        /// <c>-1</c> (no resupply) and <c>-2</c> (infinite) are carried through unchanged.
        /// </summary>
        public readonly short SpareAmmo;

        /// <summary>Seconds between shots. <c>Weapon.Configuration.cooldown</c>.</summary>
        public readonly float Cooldown;

        /// <summary>False for a weapon whose trigger costs nothing — the horn (V6 task 5).</summary>
        public readonly bool SpendsAmmo;

        public MountedWeaponDeclaration(
            byte weaponId, byte clipSize, short spareAmmo, float cooldown, bool spendsAmmo)
        {
            WeaponId   = weaponId;
            ClipSize   = clipSize;
            SpareAmmo  = spareAmmo;
            Cooldown   = cooldown;
            SpendsAmmo = spendsAmmo;
        }
    }

    /// <summary>
    /// Answers whether a mounted weapon may fire right now, for the active role. V6 task 3.
    /// </summary>
    /// <remarks>
    /// <b>Keyed by ids for the reason <see cref="ITurretAimDirectory"/> is</b> — this assembly
    /// cannot name <c>Weapon</c>, <c>Seat</c> or <c>Actor</c>. The weapon resolves its own
    /// <c>(vehicleId, seatIndex)</c> in <c>Assembly-CSharp</c>, where those types are in scope.
    /// </remarks>
    public interface IWeaponFireDirectory
    {
        /// <summary>
        /// The server's answer for one mounted weapon.
        /// </summary>
        /// <returns>
        /// False when this side has nothing to say — an unregistered weapon behaves as it does
        /// offline rather than being silently jammed, which is the difference between a vehicle
        /// the netcode has not reached yet and a bug report saying the gun does not work.
        /// </returns>
        bool TryMayFire(ushort vehicleId, byte seatIndex, bool gunnerIsAlive, out bool mayFire);

        /// <summary>
        /// Announces that a mounted weapon exists on this seat, with these numbers.
        /// </summary>
        /// <remarks>
        /// Idempotent: a weapon declares itself whenever it is asked whether it may fire, and a
        /// re-declaration must not re-arm a half-empty gun. A gunner getting out and back in runs
        /// through <c>Seat.SetOccupant</c> every time.
        /// </remarks>
        void Declare(ushort vehicleId, byte seatIndex, in MountedWeaponDeclaration declaration);
    }

    /// <summary>
    /// The single gate <c>Weapon.CanFire()</c> consults before every mounted shot. V6 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three roles, and why each is what it is:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b><see cref="NetRole.Offline"/> → always true.</b> Single-player is untouched, byte for
    /// byte (V6-D9). This is the same guard shape phase-05 used at <c>Actor.Damage</c>, and it is
    /// checked before anything else so that a misconfigured directory cannot reach it.
    /// </item>
    /// <item>
    /// <b><see cref="NetRole.Server"/> → the authority decides.</b>
    /// <c>MountedWeaponAuthority</c> owns the clip, the cooldown and the reload, so a client
    /// sending ten frames inside one tick gets one shot and nine refusals.
    /// </item>
    /// <item>
    /// <b><see cref="NetRole.Client"/> → true only for the local player's own weapon.</b> A
    /// remote actor's mounted weapon is driven by <c>S_WEAPON_FIRE</c>, which plays the cosmetics
    /// directly; letting <c>CanFire</c> pass there would have every client independently deciding
    /// that every other player's turret had fired.
    /// </item>
    /// </list>
    /// <para>
    /// <b>Side-effect free by contract.</b> <c>CanFire()</c> is called from <c>Fire()</c> and from
    /// AI targeting, more than once per shot, so anything that spent ammo here would spend it
    /// several times for one trigger pull.
    /// </para>
    /// </remarks>
    public static class NetWeaponAuthority
    {
        /// <summary>The active side's implementation. Null reads as "no opinion".</summary>
        public static IWeaponFireDirectory Directory { get; set; }

        /// <inheritdoc cref="IWeaponFireDirectory.Declare"/>
        public static void Declare(
            ushort vehicleId, byte seatIndex, in MountedWeaponDeclaration declaration)
        {
            if (NetContext.IsOffline) return;
            if (vehicleId == 0) return;

            Directory?.Declare(vehicleId, seatIndex, in declaration);
        }

        /// <summary>
        /// Whether the mounted weapon at <paramref name="seatIndex"/> on
        /// <paramref name="vehicleId"/> may fire.
        /// </summary>
        /// <param name="locallyOccupied">
        /// True when the seat holds the local player's actor. Decides the client branch, and is
        /// false for every seat on a dedicated server.
        /// </param>
        public static bool MayFire(
            ushort vehicleId, byte seatIndex, bool locallyOccupied, bool gunnerIsAlive)
        {
            // D9, first and unconditionally.
            if (NetContext.IsOffline) return true;

            if (NetContext.IsClient) return locallyOccupied;

            IWeaponFireDirectory directory = Directory;
            if (directory == null) return true;
            if (vehicleId == 0) return true;

            return directory.TryMayFire(vehicleId, seatIndex, gunnerIsAlive, out bool mayFire)
                ? mayFire
                : true;
        }

        /// <summary>
        /// Whether a shot's GAMEPLAY half should run here: the projectile, the ammo, the
        /// <c>Highlight()</c>, the recoil impulse on a rigidbody.
        /// </summary>
        /// <remarks>
        /// True offline and on the server; false on a client, whose mounted weapons are driven by
        /// the wire. The cosmetic half — muzzle flash, casing, audio, animator trigger — is the
        /// complement, and is skipped on a dedicated server where the components it touches are
        /// stripped or null.
        /// </remarks>
        public static bool GameplayHalfRunsHere => !NetContext.IsClient;

        /// <summary>
        /// Whether a shot's COSMETIC half should run here.
        /// </summary>
        /// <remarks>
        /// False on a dedicated server, which is where <c>AudioSource</c>, <c>ParticleSystem</c>
        /// and <c>Animator</c> are missing on a stripped prefab — two of the § 3.6 headless NREs
        /// are exactly this.
        /// </remarks>
        public static bool CosmeticHalfRunsHere => !NetContext.IsServer;

        /// <summary>Drops the directory. Role teardown.</summary>
        public static void Clear() => Directory = null;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
