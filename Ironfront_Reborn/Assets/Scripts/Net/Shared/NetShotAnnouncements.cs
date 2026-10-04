using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Puts one hand-held shot on the wire. Implemented by the server's tick loop.
    /// </summary>
    public interface IShotAnnouncer
    {
        /// <summary>
        /// Announces a shot from <paramref name="shooter"/>'s body along
        /// <paramref name="direction"/>, to the clients close enough to hear it — unless the body is
        /// a player's, whose shots are announced from their input frame instead.
        /// </summary>
        void AnnounceShot(GameObject shooter, Vector3 direction);

        /// <summary>
        /// Announces a bot's honk from <paramref name="shooter"/>'s seat, tagged as the horn, to
        /// the clients close enough to hear it. A player's honk is announced from their input frame.
        /// </summary>
        void AnnounceHorn(GameObject shooter);
    }

    /// <summary>
    /// The seam <c>Weapon.Shoot</c> announces a server-driven shot through, once per shot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a seam and not a call.</b> A client hears a remote shot only from
    /// <c>S_WEAPON_FIRE</c>, and until the 2026-09-27 fix nothing produced one for a bot: the
    /// producers took a client session, and a bot's hitscan bullet leaves the projectile bridge
    /// unannounced — so the only combat a player could hear was explosions. <c>Weapon</c> is the
    /// one place every bot shot passes through, but it lives in Assembly-CSharp, which may not name
    /// the server assembly (ledger E-11, <c>tools/check-net-layering.ps1</c> RULE 1). So the server
    /// installs an announcer here when it binds, and <c>Weapon</c> names only this.
    /// </para>
    /// <para>
    /// Offline and on a client nothing is installed, and a shot announces nothing — which is the
    /// whole of the single-player guarantee (V6-D9) for this path.
    /// </para>
    /// </remarks>
    public static class NetShotAnnouncements
    {
        /// <summary>The server's announcer while it is bound. Null everywhere else.</summary>
        public static IShotAnnouncer Announcer { get; set; }

        /// <inheritdoc cref="IShotAnnouncer.AnnounceShot"/>
        public static void Announce(GameObject shooter, Vector3 direction)
        {
            if (!NetContext.IsServer || shooter == null) return;
            Announcer?.AnnounceShot(shooter, direction);
        }

        /// <summary>Uninstalls the announcer. The server calls it on unbind.</summary>
        /// <inheritdoc cref="IShotAnnouncer.AnnounceHorn"/>
        public static void AnnounceHorn(GameObject shooter)
        {
            if (!NetContext.IsServer || shooter == null) return;
            Announcer?.AnnounceHorn(shooter);
        }

        public static void Clear() => Announcer = null;

        // With domain reload disabled a static survives leaving play mode, and the next run would
        // announce into the previous run's loop.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
