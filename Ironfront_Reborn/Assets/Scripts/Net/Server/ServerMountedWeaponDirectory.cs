using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The server's answer to <c>Weapon.CanFire()</c> for a mounted weapon: whatever
    /// <see cref="MountedWeaponAuthority"/> would say. V6 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It asks the predicate, it does not step the authority.</b>
    /// <see cref="MountedWeaponAuthority.CheckCanFire"/> is static and side-effect free precisely
    /// so this can be called from <c>CanFire()</c> — which runs from <c>Fire()</c> and again from
    /// AI targeting, several times per trigger pull. Spending ammo here would spend it several
    /// times for one shot; the spend happens once, in <c>Step</c>, from the tick loop.
    /// </para>
    /// <para>
    /// <b>An untracked weapon is not refused.</b> Returning false for a vehicle the registry has
    /// not reached yet would present as "the gun does not work" with nothing anywhere saying why.
    /// A miss reports no opinion and the weapon behaves as it does offline.
    /// </para>
    /// </remarks>
    internal sealed class ServerMountedWeaponDirectory : IWeaponFireDirectory
    {
        private readonly MountedWeaponRegistry _registry;
        private readonly System.Func<float> _nowSeconds;

        internal ServerMountedWeaponDirectory(
            MountedWeaponRegistry registry, System.Func<float> nowSeconds)
        {
            _registry   = registry;
            _nowSeconds = nowSeconds;
        }

        /// <inheritdoc />
        public bool TryMayFire(ushort vehicleId, byte seatIndex, bool gunnerIsAlive, out bool mayFire)
        {
            mayFire = true;

            if (_registry == null || _nowSeconds == null) return false;
            if (!_registry.TryGetState(vehicleId, seatIndex, out WeaponRuntimeState state)) return false;

            WeaponConfig config = _registry.ConfigOf(vehicleId, seatIndex);

            mayFire = MountedWeaponAuthority.CheckCanFire(
                in state, in config, gunnerIsAlive, _nowSeconds()) == FireRejection.None;

            return true;
        }

        /// <inheritdoc />
        public void Declare(
            ushort vehicleId, byte seatIndex, in MountedWeaponDeclaration declaration)
        {
            if (_registry == null) return;

            // Built from the prefab's own numbers rather than from WeaponCatalog: a turret bolted
            // to a vehicle usually carries WeaponIds.NONE and has no catalog row at all. Range,
            // damage and force stay zero because this authority never resolves a shot -- it
            // decides whether the trigger is honoured and what it costs. V7 owns the projectile.
            var config = new WeaponConfig(
                cooldown: declaration.Cooldown,
                spread: 0f,
                projectilesPerShot: 1,
                range: 0f,
                damage: 0f,
                force: 0f,
                clipSize: declaration.ClipSize,
                spareAmmo: declaration.SpareAmmo,
                spendsAmmo: declaration.SpendsAmmo);

            _registry.Register(vehicleId, seatIndex, declaration.WeaponId, in config);
        }

        /// <summary>
        /// The weapon id the registry holds for a seat, for the <c>S_WEAPON_FIRE</c> the bridge
        /// emits. <see cref="WeaponIds.NONE"/> when untracked.
        /// </summary>
        internal byte WeaponIdOf(ushort vehicleId, byte seatIndex)
            => _registry != null ? _registry.WeaponIdOf(vehicleId, seatIndex) : WeaponIds.NONE;
    }
}
