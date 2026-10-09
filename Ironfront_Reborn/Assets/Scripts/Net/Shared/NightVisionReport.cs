#nullable enable

using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Night vision went on or off on this machine (achievements v2). Offline the practice
    /// achievements hear it; online the client sends <c>C_NIGHT_VISION</c> through the sender the
    /// networked client installs, so the server knows this player turned it on.
    /// </summary>
    /// <remarks>
    /// In Shared because the goggles (Assembly-CSharp) can reach this assembly and not the client's.
    /// NAKED EYE, CREATURE OF THE NIGHT and GRAVEYARD SHIFT reward NOT turning it on, so a
    /// player's own game is the only witness that matters and a lie only costs that player.
    /// </remarks>
    public static class NightVisionReport
    {
        /// <summary>Whether night vision is on right now.</summary>
        public static bool IsOn { get; private set; }

        /// <summary>Sends the state to the game server; installed by the networked client, null otherwise.</summary>
        public static Action<bool>? Sender { get; set; }

        public static void TurnedOn()
        {
            IsOn = true;
            if (NetContext.IsOffline) PracticeFeats.NightVisionTurnedOn();
            Sender?.Invoke(true);
        }

        public static void TurnedOff()
        {
            IsOn = false;
            Sender?.Invoke(false);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            IsOn = false;
            Sender = null;
        }
    }
}
