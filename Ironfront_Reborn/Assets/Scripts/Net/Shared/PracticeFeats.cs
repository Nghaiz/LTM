#nullable enable

using System;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The practice achievements (owner's list of 2026-10-09, item 4): feats only the player's own
    /// game can see happen, because no server watches an offline match or reads the guide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Facts in, ids out.</b> The legacy game (Assembly-CSharp) reports what happened -- a match
    /// started with so many bots, the player killed an enemy, the match ended -- and the guide
    /// reports a tab read. This decides which practice achievement that earns and raises
    /// <see cref="Earned"/>; the client's achievement ledger keeps it, shows the toast and claims
    /// it from the master. It lives here, in Shared, because Assembly-CSharp can reach this
    /// assembly and not the client's.
    /// </para>
    /// <para>
    /// <b>Raised more than once is fine.</b> Reading the last guide tab again re-raises
    /// <see cref="StudentOfWar"/>; the ledger keeps a set and ignores what it already has.
    /// </para>
    /// <para>The ids are <c>AchievementCatalog</c>'s; a test pins that they exist there.</para>
    /// </remarks>
    public static class PracticeFeats
    {
        public const string BasicTraining = "basic_training";
        public const string BotBuster = "bot_buster";
        public const string OneManArmy = "one_man_army";
        public const string StudentOfWar = "student_of_war";

        /// <summary>Kills in one practice match that earn <see cref="BotBuster"/>.</summary>
        public const int BotBusterKills = 25;

        /// <summary>Bots in a won practice match that earn <see cref="OneManArmy"/>: the most a match can have.</summary>
        public const int OneManArmyBots = ProtocolConstants.MAX_BOTS;

        /// <summary>The guide tabs read so far, one bit per tab, kept between runs.</summary>
        public const string GuideTabsKey = "ironfront.achievements.guide-tabs";

        /// <summary>A practice achievement was earned (its id). Raised on the main thread.</summary>
        public static event Action<string>? Earned;

        private static int _kills;
        private static int _bots;
        private static bool _running;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Earned = null;
            _kills = 0;
            _bots = 0;
            _running = false;
        }

        /// <summary>An offline match began with <paramref name="bots"/> bots in it.</summary>
        public static void MatchStarted(int bots)
        {
            _kills = 0;
            _bots = bots;
            _running = true;
        }

        /// <summary>The player killed an enemy in the offline match.</summary>
        public static void PlayerKilledEnemy()
        {
            if (!_running) return;
            _kills++;
            if (_kills == BotBusterKills) Raise(BotBuster);
        }

        /// <summary>The offline match ended; <paramref name="playerWon"/> when the player's side took it.</summary>
        public static void MatchEnded(bool playerWon)
        {
            if (!_running) return;
            _running = false;
            Raise(BasicTraining);
            if (playerWon && _bots >= OneManArmyBots) Raise(OneManArmy);
        }

        /// <summary>The guide's tab <paramref name="tab"/> of <paramref name="tabs"/> was shown to the player.</summary>
        public static void GuideTabRead(int tab, int tabs)
        {
            if (tabs <= 0 || tabs > 30 || tab < 0 || tab >= tabs) return;
            int read = PlayerPrefs.GetInt(GuideTabsKey, 0) | (1 << tab);
            PlayerPrefs.SetInt(GuideTabsKey, read);
            int all = (1 << tabs) - 1;
            if ((read & all) == all) Raise(StudentOfWar);
        }

        private static void Raise(string id) => Earned?.Invoke(id);
    }
}
