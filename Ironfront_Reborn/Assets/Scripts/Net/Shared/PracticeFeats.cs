#nullable enable

using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>What an offline match was set up as, read when it starts.</summary>
    public readonly struct PracticeMatch
    {
        public PracticeMatch(ushort mapId, bool night, VictoryRule rule, int victoryPoints, bool vehicles,
            int team0Bots, int team1Bots, int playerTeam, int capturePoints)
        {
            MapId = mapId;
            Night = night;
            Rule = rule;
            VictoryPoints = victoryPoints;
            Vehicles = vehicles;
            Team0Bots = team0Bots;
            Team1Bots = team1Bots;
            PlayerTeam = playerTeam;
            CapturePoints = capturePoints;
        }

        public ushort MapId { get; }
        public bool Night { get; }
        public VictoryRule Rule { get; }
        public int VictoryPoints { get; }
        public bool Vehicles { get; }
        public int Team0Bots { get; }
        public int Team1Bots { get; }
        public int PlayerTeam { get; }

        /// <summary>Flags that can be captured on the map.</summary>
        public int CapturePoints { get; }

        public int Bots => Team0Bots + Team1Bots;
        public int AlliedBots => PlayerTeam == 1 ? Team1Bots : Team0Bots;
        public int EnemyBots => PlayerTeam == 1 ? Team0Bots : Team1Bots;

        /// <summary>The default rule or a harder one: LEAD BY 200 or more, FIRST TO 500 or more.</summary>
        public bool HardRule => Rule == VictoryRule.Target ? VictoryPoints >= 500 : VictoryPoints >= 200;
    }

    /// <summary>
    /// The fifteen practice achievements (achievements v2, <c>docs/achievements.md</c>): feats only
    /// the player's own game can see, because no server watches an offline match or reads the guide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Facts in, ids out.</b> The legacy game (Assembly-CSharp) reports what happened -- a match
    /// started, a kill, a death, a flag, night vision, the end -- and this decides what that earns,
    /// raises <see cref="Earned"/> at the moment it happens, and keeps the progress numbers (the
    /// <c>Pr</c> career stats) on this machine. The achievement ledger shows the toast and claims
    /// the ids and the numbers from the master. It lives in Shared because Assembly-CSharp can reach
    /// this assembly and not the client's.
    /// </para>
    /// <para>
    /// <b>The rules are the catalogue's descriptions.</b> A round counts for "finish" and "win" after
    /// <see cref="CareerRules.QualifyingSeconds"/> of play, as online. Raised more than once is
    /// fine: the ledger keeps a set.
    /// </para>
    /// <para>The ids are <c>AchievementCatalog</c>'s; a test pins that they are exactly its practice ones.</para>
    /// </remarks>
    public static class PracticeFeats
    {
        public const string ByTheBook = "by_the_book";
        public const string Cadet = "cadet";
        public const string Turncoat = "turncoat";
        public const string DustDevil = "dust_devil";
        public const string FirstPastThePost = "first_past_the_post";
        public const string BootsOnly = "boots_only";
        public const string HellWeek = "hell_week";
        public const string IslandHopper = "island_hopper";
        public const string LakeMonster = "lake_monster";
        public const string GraveyardShift = "graveyard_shift";
        public const string MotorPool = "motor_pool";
        public const string GoldStandard = "gold_standard";
        public const string DrillSergeant = "drill_sergeant";
        public const string GrandTour = "grand_tour";
        public const string Immaculate = "immaculate";

        private const ushort Dustbowl = 1, Island = 2, ForestLake = 3;
        private const byte KindLand = 1, KindTank = 2, KindHeli = 4, KindBoat = 8, KindAll = 15;

        /// <summary>A practice achievement was earned (its id). Raised on the main thread.</summary>
        public static event Action<string>? Earned;

        /// <summary>An offline match began (the round summary snapshots the practice numbers).</summary>
        public static event Action? RoundBegan;

        /// <summary>An offline match ended (the round summary compares the practice numbers).</summary>
        public static event Action? RoundOver;

        private static PracticeMatch _match;
        private static bool _running;
        private static float _startedAt;
        private static int _kills;
        private static int _lifeKills;
        private static int _boatKills;
        private static byte _vehicleKinds;
        private static bool _nightVisionUsed;
        private static readonly HashSet<int> FlagsHelped = new HashSet<int>();
        private static readonly Dictionary<int, int> KillsByActor = new Dictionary<int, int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Earned = null;
            RoundBegan = null;
            RoundOver = null;
            _running = false;
        }

        /// <summary>An offline match began.</summary>
        public static void MatchStarted(in PracticeMatch match, float now)
        {
            _match = match;
            _running = true;
            _startedAt = now;
            _kills = 0;
            _lifeKills = 0;
            _boatKills = 0;
            _vehicleKinds = 0;
            _nightVisionUsed = false;
            FlagsHelped.Clear();
            KillsByActor.Clear();
            RoundBegan?.Invoke();
        }

        /// <summary>
        /// Someone was killed in the offline match. <paramref name="killerKey"/> identifies the
        /// killer among everyone in it (0 for nobody), for "the most kills of anyone".
        /// </summary>
        public static void ActorKilled(int killerKey, bool killerIsPlayer, bool victimIsPlayer, bool enemyKill,
            byte weaponId, byte killerVehicleType)
        {
            if (!_running) return;
            if (victimIsPlayer) _lifeKills = 0;
            if (killerKey == 0 || !enemyKill) return;

            KillsByActor[killerKey] = KillsByActor.TryGetValue(killerKey, out int had) ? had + 1 : 1;
            if (!killerIsPlayer) return;

            _kills++;
            _lifeKills++;
            if (weaponId == WeaponIds.SUPER_WRENCH) Raise(GoldStandard);

            byte kind = killerVehicleType == VehicleIds.JEEP || killerVehicleType == VehicleIds.QUADBIKE ? KindLand
                : killerVehicleType == VehicleIds.TANK ? KindTank
                : killerVehicleType == VehicleIds.HELICOPTER ? KindHeli
                : killerVehicleType == VehicleIds.RHIB ? KindBoat
                : (byte)0;
            if (kind != 0)
            {
                _vehicleKinds |= kind;
                Best(CareerStat.PrMotorPoolBest, CareerStats.BitCount(_vehicleKinds));
                if (_vehicleKinds == KindAll) Raise(MotorPool);
            }

            if (_match.MapId == Dustbowl && _match.Bots >= 50)
            {
                Best(CareerStat.PrDustDevilBest, _kills);
                if (_kills >= 40) Raise(DustDevil);
            }
            if (_match.Bots >= ProtocolConstants.MAX_BOTS)
            {
                Best(CareerStat.PrHellWeekBest, _kills);
                if (_kills >= 75) Raise(HellWeek);
                Best(CareerStat.PrImmaculateBest, _lifeKills);
                if (_lifeKills >= 100) Raise(Immaculate);
            }
            if (_match.MapId == ForestLake && kind == KindBoat)
            {
                _boatKills++;
                Best(CareerStat.PrLakeMonsterBest, _boatKills);
                if (_boatKills >= 10) Raise(LakeMonster);
            }
        }

        /// <summary>A flag turned to the player's side while they stood in it.</summary>
        public static void PlayerHelpedCapture(int flagKey)
        {
            if (!_running) return;
            FlagsHelped.Add(flagKey);
            if (_match.MapId == Island && _match.CapturePoints > 0 && FlagsHelped.Count >= _match.CapturePoints)
                Raise(IslandHopper);
        }

        /// <summary>The player turned night vision on.</summary>
        public static void NightVisionTurnedOn()
        {
            if (_running) _nightVisionUsed = true;
        }

        /// <summary>The offline match ended at <paramref name="now"/>; <paramref name="playerWon"/> when the player's side took it.</summary>
        public static void MatchEnded(bool playerWon, int playerKey, float now)
        {
            if (!_running) return;
            _running = false;
            try
            {
                Judge(playerWon, playerKey, now);
            }
            finally
            {
                // A finished round's numbers are saved now, not a few seconds later.
                AchievementVault.Flush();
                RoundOver?.Invoke();
            }
        }

        private static void Judge(bool playerWon, int playerKey, float now)
        {
            if (now - _startedAt < CareerRules.QualifyingSeconds) return;

            if (_match.MapId >= 1 && _match.MapId <= 3) Or(CareerStat.PrMapsFinished, 1L << _match.MapId);
            if (Has(CareerStat.PrMapsFinished, (1L << Dustbowl) | (1L << Island) | (1L << ForestLake))) Raise(Cadet);
            if (!playerWon) return;

            Or(CareerStat.PrSidesWon, 1L << (_match.PlayerTeam == 1 ? 1 : 0));
            if (Has(CareerStat.PrSidesWon, 3)) Raise(Turncoat);

            if (_match.Rule == VictoryRule.Target && _kills >= 30) Raise(FirstPastThePost);
            if (!_match.Vehicles && _match.Bots >= 50 && MostKills(playerKey)) Raise(BootsOnly);
            if (_match.Night && _match.Bots >= 50 && !_nightVisionUsed) Raise(GraveyardShift);
            // Under any rule: a side scores a kill times the flags it holds, so a lone soldier who
            // holds one or two flags against twenty bots never reaches a 200-point lead (owner's
            // run of 2026-10-10: "is one against twenty even possible?").
            if (_match.AlliedBots == 0 && _match.EnemyBots >= 20) Raise(DrillSergeant);

            if (_match.HardRule && _match.MapId >= 1 && _match.MapId <= 3)
                Or(CareerStat.PrGrandTour, 1L << (2 * (_match.MapId - 1) + (_match.Rule == VictoryRule.Target ? 1 : 0)));
            // "...and win a Night Mode practice round": under any rule, unlike the six map wins.
            if (_match.Night) Or(CareerStat.PrGrandTour, 1L << 6);
            if (Has(CareerStat.PrGrandTour, 0x7F)) Raise(GrandTour);
        }

        /// <summary>The guide's tab <paramref name="tab"/> of <paramref name="tabs"/> was shown to the player.</summary>
        public static void GuideTabRead(int tab, int tabs)
        {
            if (tabs <= 0 || tabs > 30 || tab < 0 || tab >= tabs) return;
            AchievementVault.RaiseProgress(CareerStat.PrGuidePages, 1L << tab);
            long read = Read(CareerStat.PrGuidePages);
            int all = (1 << tabs) - 1;
            if ((read & all) == all) Raise(ByTheBook);
        }

        /// <summary>
        /// This machine's practice numbers, by career key: the guide pages read, the maps and sides,
        /// the personal bests. Sent with every claim and laid over the master's career on the page.
        /// </summary>
        public static Dictionary<string, long> Progress()
        {
            var progress = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> entry in AchievementVault.Data.Progress)
                if (entry.Value != 0 && CareerStats.TryParse(entry.Key, out CareerStat stat) && CareerStats.IsPractice(stat))
                    progress[entry.Key] = entry.Value;
            return progress;
        }

        private static bool MostKills(int playerKey)
        {
            int mine = KillsByActor.TryGetValue(playerKey, out int own) ? own : 0;
            foreach (KeyValuePair<int, int> other in KillsByActor)
                if (other.Key != playerKey && other.Value > mine) return false;
            return true;
        }

        private static long Read(CareerStat stat) => AchievementVault.GetProgress(stat);

        // Both keep the larger number or both halves of a mask: the vault's merge rule for the stat.
        private static void Best(CareerStat stat, long value) => AchievementVault.RaiseProgress(stat, value);

        private static void Or(CareerStat stat, long bits) => AchievementVault.RaiseProgress(stat, bits);

        private static bool Has(CareerStat stat, long bits) => (Read(stat) & bits) == bits;

        private static void Raise(string id) => Earned?.Invoke(id);
    }
}
