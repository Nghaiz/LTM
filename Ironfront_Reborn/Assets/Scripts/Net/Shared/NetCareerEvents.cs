#nullable enable

using System;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The legacy game's way to tell the server's achievement counters what happened (achievements
    /// v2): a blow and the health it left, a bullet that hurt a soldier, a horn. Assembly-CSharp
    /// reaches this assembly and not the server's, so the server installs the handlers here and
    /// the game calls through (the E-11 seam, as <see cref="NetShotAnnouncements"/> is).
    /// </summary>
    /// <remarks>
    /// Off the server nothing is installed and every call is a field read; offline and on a client
    /// the game behaves exactly as it did.
    /// </remarks>
    public static class NetCareerEvents
    {
        /// <summary>(victim, attacker or null, health left, cause). Installed by the server.</summary>
        public static Action<Component, Component?, float, CauseOfDeath>? DamageSink { get; set; }

        /// <summary>(shooter, victim, weapon id, shot serial). Installed by the server.</summary>
        public static Action<Component, Component, byte, long>? HitSink { get; set; }

        /// <summary>(driver). Installed by the server.</summary>
        public static Action<Component>? HornSink { get; set; }

        public static void Damage(Component victim, Component? attacker, float healthAfter, CauseOfDeath cause)
            => DamageSink?.Invoke(victim, attacker, healthAfter, cause);

        public static void Hit(Component shooter, Component victim, byte weaponId, long shotSerial)
            => HitSink?.Invoke(shooter, victim, weaponId, shotSerial);

        public static void Horn(Component driver) => HornSink?.Invoke(driver);

        /// <summary>Uninstalls every handler. The server calls it on unbind.</summary>
        public static void Clear()
        {
            DamageSink = null;
            HitSink = null;
            HornSink = null;
        }

        // With domain reload disabled a static survives leaving play mode.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
