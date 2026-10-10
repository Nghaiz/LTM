#nullable enable

using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The original game's secret: the SUPER WRENCH, a golden wrench hidden from the loadout screen
    /// until <c>ISEEGOLD</c> is typed on the menu (owner's list of 2026-10-09, achievements item:
    /// "discover and use the gold wrench", practice only).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The code is the original's.</b> <c>WeaponManager.Update</c> still carries Ravenfield's
    /// obfuscated key sequence, which spells I-S-E-E-G-O-L-D, and still listens only outside a
    /// map. What changed is what a completed sequence does: the original set every hidden entry's
    /// <c>hidden</c> flag to false for the rest of the run, which would also have offered the
    /// wrench online, where the server treats it as inert. Now it calls <see cref="Unlock"/>.
    /// </para>
    /// <para>
    /// <b>Practice only, and remembered.</b> <see cref="IsOffered"/> lets a hidden entry onto the
    /// loadout screen only once unlocked and only in an offline match; the unlock is kept between
    /// runs, so a player who found it keeps it.
    /// </para>
    /// <para>
    /// It lives in Shared because <c>WeaponManager</c> and <c>LoadoutUi</c> (Assembly-CSharp)
    /// write and read it, and the client's toast announces it, and Shared is the one assembly all
    /// three can see.
    /// </para>
    /// </remarks>
    public static class GoldenWrench
    {
        /// <summary>
        /// The preference that remembered the unlock up to v4.6.0; <see cref="AchievementVault"/>
        /// keeps it now and still writes this key for an older build.
        /// </summary>
        public const string UnlockedKey = AchievementVault.LegacyGoldenWrenchKey;

        /// <summary>Raised on the main thread when the wrench is unlocked; carries its loadout picture.</summary>
        public static event Action<Sprite?>? Revealed;

        /// <summary>Whether this machine has unlocked the golden wrench.</summary>
        public static bool IsUnlocked => AchievementVault.Data.GoldenWrench;

        /// <summary>
        /// Unlocks it and says so, once: a second <c>ISEEGOLD</c> on a machine that has it already is
        /// not news.
        /// </summary>
        public static void Unlock(Sprite? picture)
        {
            if (IsUnlocked) return;
            AchievementVault.SetGoldenWrench();
            Revealed?.Invoke(picture);
        }

        /// <summary>
        /// Whether a loadout entry may be offered: a normal one always; one the game hides (the
        /// golden wrench) only once unlocked, and only in an offline match.
        /// </summary>
        public static bool IsOffered(bool hiddenEntry, bool offline)
            => !hiddenEntry || (offline && IsUnlocked);

        /// <summary>Clears the subscription for <see cref="NetContext.ResetOnLoad"/>'s reason.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Revealed = null;
    }
}
