using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// The per-round facts the achievements are judged from (achievements v2,
    /// <c>docs/achievements.md</c>): every actor's kills by kind, shots, damage, vehicles, flags.
    /// The game server feeds it; the master judges what it reports (<see cref="CareerRules"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine-free and fixed-size</b>: the callers are MonoBehaviours no test can run, and the id
    /// space is <c>MAX_ACTORS</c> slots, bots included, because the rank feats compare a player with
    /// every bot in the round.
    /// </para>
    /// <para>
    /// <b>Time is the server tick's</b> (seconds), so "the same blast" is "the same tick": every
    /// victim of one explosion dies inside one call of the damage loop.
    /// </para>
    /// </remarks>
    public sealed partial class MatchCareerTally
    {
        private const int Actors = ProtocolConstants.MAX_ACTORS;
        private const ushort Nobody = ushort.MaxValue;

        /// <summary>Two events closer than this are in the same server tick.</summary>
        private const float SameTick = 0.001f;

        private const byte LifePrimary = 1, LifePistol = 2, LifeGrenade = 4, LifeLauncher = 8, LifeAll = 15;

        private readonly long[,] _facts = new long[Actors, RoundFacts.Count];

        private readonly float[] _deathTime = new float[Actors];
        private readonly float[] _joinTime = new float[Actors];
        private readonly float[] _lastKillTime = new float[Actors];
        private readonly int[] _multi = new int[Actors];
        private readonly int[] _headshotRun = new int[Actors];
        private readonly float[] _blastTime = new float[Actors];
        private readonly int[] _blastKills = new int[Actors];
        private readonly int[] _graveKills = new int[Actors];
        private readonly byte[] _lifeWeapons = new byte[Actors];
        private readonly float[] _enemyBlastKillTime = new float[Actors];
        private readonly float[] _selfBlastDeathTime = new float[Actors];
        private readonly float[] _mutualCountedAt = new float[Actors];
        private readonly float[] _lastRoadkillTime = new float[Actors];
        private readonly bool[] _clutch = new bool[Actors];
        private readonly int[] _clutchKills = new int[Actors];
        private readonly ushort[] _stintVehicle = new ushort[Actors];
        private readonly int[] _stintSeat = new int[Actors];
        private readonly bool[] _stintIsHeli = new bool[Actors];
        private readonly int[] _stintKills = new int[Actors];
        private readonly float[] _watchHealth = new float[Actors];
        private readonly bool[] _watchAlive = new bool[Actors];
        private readonly long[] _lastHitShot = new long[Actors];
        private readonly ulong[] _flagMask = new ulong[Actors];
        private readonly short[] _pairKills = new short[Actors * Actors];
        private readonly bool[] _killedBy = new bool[Actors * Actors];

        private float _roundStart;
        private int _mapFlags;
        private readonly bool[] _nearLoss = new bool[2];

        public MatchCareerTally() => Clear();

        /// <summary>One fact of one actor this round.</summary>
        public long Get(ushort actor, RoundFact fact) => actor < Actors ? _facts[actor, (int)fact] : 0;

        /// <summary>The round's Playing phase began at <paramref name="now"/>, on a map with <paramref name="mapFlags"/> flags.</summary>
        public void BeginRound(float now, int mapFlags)
        {
            Clear();
            _roundStart = now;
            _mapFlags = Math.Max(0, mapFlags);
        }

        /// <summary>A player took <paramref name="actor"/>'s body at <paramref name="now"/>: their time starts here.</summary>
        public void NoteJoined(ushort actor, float now)
        {
            if (actor >= Actors) return;
            Forget(actor);
            _joinTime[actor] = now;
        }

        /// <summary>
        /// The score after a change, under the room's rule, so HAIL MARY can tell a side the enemy
        /// came within <see cref="CareerRules.HailMaryPoints"/> of beating.
        /// </summary>
        public void NoteScores(int score0, int score1, bool firstToRule, int victoryPoints)
        {
            int near = victoryPoints - CareerRules.HailMaryPoints;
            if (near <= 0) return;
            if (firstToRule)
            {
                if (score1 >= near) _nearLoss[0] = true;
                if (score0 >= near) _nearLoss[1] = true;
            }
            else
            {
                if (score1 - score0 >= near) _nearLoss[0] = true;
                if (score0 - score1 >= near) _nearLoss[1] = true;
            }
        }

        /// <summary>Counts one resolved death at <paramref name="now"/> seconds.</summary>
        public void RecordKill(in CareerKill kill, float now)
        {
            ushort killer = kill.Killer;
            ushort victim = kill.Victim;
            if (victim < Actors) RecordDeath(in kill, now);
            if (killer >= Actors || killer == victim || kill.SameTeam) return;

            Add(killer, kill.VictimIsBot ? RoundFact.BotKills : RoundFact.PlayerKills, 1);

            bool byVehicle = (kill.Detail & DeathDetail.KillerInVehicle) != 0;
            bool melee = (kill.Detail & DeathDetail.Melee) != 0;
            bool rammed = kill.Cause == CauseOfDeath.Vehicle && byVehicle;
            if (melee) Add(killer, RoundFact.MeleeKills, 1);
            if (kill.Cause == CauseOfDeath.Explosion) Add(killer, RoundFact.ExplosiveKills, 1);
            if (rammed)
            {
                Add(killer, RoundFact.Roadkills, 1);
                if (kill.VehicleType == VehicleIds.RHIB) Add(killer, RoundFact.BoatRoadkills, 1);
                if (kill.VehicleType == VehicleIds.HELICOPTER) Add(killer, RoundFact.HeliRoadkills, 1);
                _lastRoadkillTime[killer] = now;
            }
            if (byVehicle && kill.VehicleType == VehicleIds.TANK) Add(killer, RoundFact.TankKills, 1);
            if (byVehicle && kill.VehicleType == VehicleIds.HELICOPTER) Add(killer, RoundFact.HelicopterKills, 1);
            if (byVehicle && kill.VehicleType == VehicleIds.RHIB) Add(killer, RoundFact.BoatKills, 1);

            CountWeapon(killer, in kill, now);
            CountDistances(killer, in kill);
            CountRuns(killer, in kill, now);
            CountNemesis(killer, in kill);
            CountStint(killer, in kill);

            if (_clutch[killer])
            {
                _clutchKills[killer]++;
                Max(killer, RoundFact.ClutchBest, _clutchKills[killer]);
            }

            if (kill.Cause == CauseOfDeath.Explosion)
            {
                _enemyBlastKillTime[killer] = now;
                CheckMutual(killer, now);
            }
        }

        private void RecordDeath(in CareerKill kill, float now)
        {
            ushort victim = kill.Victim;
            _deathTime[victim] = now;
            _graveKills[victim] = 0;
            _lifeWeapons[victim] = 0;
            _multi[victim] = 0;
            _headshotRun[victim] = 0;
            _clutch[victim] = false;
            _clutchKills[victim] = 0;
            _watchAlive[victim] = false;
            EndStint(victim);

            if (kill.Killer < Actors && kill.Killer != victim && !kill.SameTeam)
                _killedBy[kill.Killer * Actors + victim] = true;

            if (kill.Cause == CauseOfDeath.Explosion && kill.Killer == victim)
            {
                _selfBlastDeathTime[victim] = now;
                CheckMutual(victim, now);
            }
        }

        private void CountWeapon(ushort killer, in CareerKill kill, float now)
        {
            byte weapon = kill.WeaponId;
            if (weapon == WeaponIds.NONE) return;

            if (CareerWeapons.CountsForArmourer(weapon) && weapon < 63)
                _facts[killer, (int)RoundFact.WeaponKillMask] |= 1L << weapon;

            byte family = CareerWeapons.IsPrimary(weapon) ? LifePrimary
                : CareerWeapons.IsPistol(weapon) ? LifePistol
                : CareerWeapons.IsGrenade(weapon) ? LifeGrenade
                : CareerWeapons.IsLauncher(weapon) ? LifeLauncher
                : (byte)0;
            if (family != 0 && _lifeWeapons[killer] != LifeAll)
            {
                _lifeWeapons[killer] |= family;
                if (_lifeWeapons[killer] == LifeAll) Add(killer, RoundFact.JackOfAllTrades, 1);
            }

            if (!CareerWeapons.IsGrenade(weapon)) return;
            Add(killer, RoundFact.GrenadeKills, 1);
            _blastKills[killer] = Math.Abs(now - _blastTime[killer]) <= SameTick ? _blastKills[killer] + 1 : 1;
            _blastTime[killer] = now;
            Max(killer, RoundFact.BestGrenadeBlast, _blastKills[killer]);

            // FROM THE GRAVE: the thrower died in an earlier tick and has not been seen alive since.
            if (!_watchAlive[killer] && !float.IsNegativeInfinity(_deathTime[killer]) && _deathTime[killer] < now - SameTick)
            {
                _graveKills[killer]++;
                Max(killer, RoundFact.FromTheGraveBest, _graveKills[killer]);
            }
        }

        private void CountDistances(ushort killer, in CareerKill kill)
        {
            int metres = kill.DistanceMetres;
            Max(killer, RoundFact.LongestKillMetres, metres);
            if (kill.Headshot) Max(killer, RoundFact.LongestHeadshotMetres, metres);
            if (kill.WeaponId == WeaponIds.EAGLE_76) Max(killer, RoundFact.LongestShotgunKillMetres, metres);
            if (kill.Headshot && kill.VictimPilotHeightMetres >= CareerRules.AirborneMetres)
                Max(killer, RoundFact.LongestPilotHeadshotMetres, metres);
            if (CareerWeapons.IsPistol(kill.WeaponId) && CareerWeapons.IsSniperRifle(kill.VictimWeaponId))
                Max(killer, RoundFact.LongestPistolOnSniperMetres, metres);
        }

        private void CountRuns(ushort killer, in CareerKill kill, float now)
        {
            _multi[killer] = now - _lastKillTime[killer] <= CareerRules.MultiKillWindowSeconds ? _multi[killer] + 1 : 1;
            _lastKillTime[killer] = now;
            Max(killer, RoundFact.BestMultiKill, _multi[killer]);

            _headshotRun[killer] = kill.Headshot ? _headshotRun[killer] + 1 : 0;
            Max(killer, RoundFact.HeadshotRun, _headshotRun[killer]);
        }

        private void CountNemesis(ushort killer, in CareerKill kill)
        {
            ushort victim = kill.Victim;
            if (kill.VictimIsBot || victim >= Actors) return;
            int pair = killer * Actors + victim;
            if (_killedBy[victim * Actors + killer]) return;
            if (_pairKills[pair] < short.MaxValue) _pairKills[pair]++;
            Max(killer, RoundFact.NemesisBest, _pairKills[pair]);
        }

        private void CountStint(ushort killer, in CareerKill kill)
        {
            if (kill.KillerVehicleId == CareerKill.NoVehicle) return;
            bool heli = kill.KillerVehicleType == VehicleIds.HELICOPTER;
            bool tank = kill.KillerVehicleType == VehicleIds.TANK;
            if (!heli && !tank) return;

            int seat = heli ? kill.KillerSeat : -1;
            if (_stintVehicle[killer] != kill.KillerVehicleId || _stintSeat[killer] != seat)
            {
                _stintVehicle[killer] = kill.KillerVehicleId;
                _stintSeat[killer] = seat;
                _stintIsHeli[killer] = heli;
                _stintKills[killer] = 0;
            }
            _stintKills[killer]++;
            Max(killer, heli ? RoundFact.HeliStintBest : RoundFact.TankStintBest, _stintKills[killer]);
        }

        private void CheckMutual(ushort actor, float now)
        {
            if (Math.Abs(_enemyBlastKillTime[actor] - _selfBlastDeathTime[actor]) > SameTick) return;
            if (Math.Abs(_mutualCountedAt[actor] - now) <= SameTick) return;
            _mutualCountedAt[actor] = now;
            Add(actor, RoundFact.MutualDestructions, 1);
        }

        private void Add(ushort actor, RoundFact fact, long amount)
        {
            if (actor < Actors) _facts[actor, (int)fact] += amount;
        }

        private void Max(ushort actor, RoundFact fact, long value)
        {
            if (actor < Actors && value > _facts[actor, (int)fact]) _facts[actor, (int)fact] = value;
        }
    }
}
