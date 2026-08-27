namespace Ironfront.Net.Unity
{
    /// <summary>
    /// A held weapon, reduced to the one thing the client netcode does with somebody else's:
    /// play the flash and the report for a shot the server said happened. Phase C4a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately one method wide.</b> The presenter's entire use of <c>Weapon</c> was
    /// <c>weapon.PlayFireCosmetics()</c> on a remote shooter, plus the bounded scan that found
    /// which weapon the replicated id names — and that scan now lives on the far side of the
    /// seam, in <c>IGameplayActorPresence.TryGetWeaponByNetworkId</c>, because it reads the
    /// game's own loadout array. Widening this interface to mirror <c>Weapon</c> would export a
    /// firing model the netcode never asked for and must never drive: V10 D9 forbids the client
    /// touching <c>currentMuzzle</c> or the <c>Fire()</c> loop from a received event.
    /// </para>
    /// <para>
    /// <b>Implemented directly by <c>Weapon</c>, for the reason its actor counterpart gives.</b>
    /// It is also what keeps <c>ActiveWeapon</c> allocation-free: returning the component as the
    /// interface is a cast, and this runs per remote shot.
    /// </para>
    /// </remarks>
    public interface IGameplayWeapon
    {
        /// <summary>
        /// False once the underlying component has been destroyed.
        /// </summary>
        /// <remarks>
        /// The same hazard <c>IGameplayActorPresence.Exists</c> documents: an interface reference
        /// to a destroyed <c>UnityEngine.Object</c> stays non-null, so a weapon held across a
        /// despawn would pass a plain <c>!= null</c> and throw on use.
        /// </remarks>
        bool Exists { get; }

        /// <summary>
        /// Plays one muzzle flash and one report.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Weapon.PlayFireCosmetics()</c>. One call per <c>S_WEAPON_FIRE</c>, never
        /// the <c>Fire()</c> loop: each message is one shot, and <c>Shoot</c> alone is silent on
        /// an automatic weapon (V10 D8).
        /// </remarks>
        void PlayFireCosmetics();
    }
}
