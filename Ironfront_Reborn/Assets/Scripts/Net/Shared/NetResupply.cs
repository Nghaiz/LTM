using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How a fixed supply cache refills a networked player's spare ammunition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a static seam.</b> <c>SupplyCache</c> compiles into <c>Assembly-CSharp</c>, which may
    /// not reach into <c>Ironfront.Net.Unity.Server</c> (E-11), and on a server a player's spare
    /// rounds live in that assembly's pool (<c>ServerTickLoop.SpareAmmo</c>), not on the
    /// <c>Actor</c>. The same shape as <see cref="NetBotRelease.HostedRoom"/>.
    /// </para>
    /// <para>
    /// <b>Only a player's body is the pool's.</b> A bot, an unclaimed slot and every actor offline
    /// keep their spare rounds on the <c>Actor</c>, so <see cref="TryGiveAmmo"/> answers false for
    /// them and the cache refills the <c>Actor</c> itself, as an ammo bag always has.
    /// </para>
    /// </remarks>
    public static class NetResupply
    {
        /// <summary>
        /// Refills every spare slot of the networked player whose body this is, each by its own
        /// per-pulse amount, and answers the rounds given; -1 when the body is not a player's.
        /// Registered by <c>ServerTickLoop</c>; null offline and on a client.
        /// </summary>
        public static Func<GameObject, int> GiveAmmo { get; set; }

        /// <summary>
        /// True when the server's pool owns this body's rounds (and has refilled it by
        /// <paramref name="rounds"/>); false when the caller must refill the body itself.
        /// </summary>
        public static bool TryGiveAmmo(GameObject body, out int rounds)
        {
            rounds = 0;
            Func<GameObject, int> give = GiveAmmo;
            if (give == null || body == null) return false;
            int given = give(body);
            if (given < 0) return false;
            rounds = given;
            return true;
        }
    }
}
