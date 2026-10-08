using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>What one resolved kill was, as the career and the achievements need it.</summary>
    public readonly struct CareerKill
    {
        public CareerKill(ushort killer, ushort victim, bool sameTeam, bool victimIsBot, bool headshot,
            int distanceMetres, CauseOfDeath cause, byte weaponId, byte vehicleType, DeathDetail detail)
        {
            Killer = killer;
            Victim = victim;
            SameTeam = sameTeam;
            VictimIsBot = victimIsBot;
            Headshot = headshot;
            DistanceMetres = distanceMetres;
            Cause = cause;
            WeaponId = weaponId;
            VehicleType = vehicleType;
            Detail = detail;
        }

        public ushort Killer { get; }
        public ushort Victim { get; }
        public bool SameTeam { get; }
        public bool VictimIsBot { get; }
        public bool Headshot { get; }
        public int DistanceMetres { get; }
        public CauseOfDeath Cause { get; }
        public byte WeaponId { get; }
        public byte VehicleType { get; }
        public DeathDetail Detail { get; }
    }

    /// <summary>
    /// The per-round numbers a player's career is built from (owner's list of 2026-10-09, item 4):
    /// the kinds of kill, the feats, the flags. Fed from the same places as
    /// <see cref="MatchScoreTally"/>; read once, at the round's end, into the match report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine-free and fixed-size</b>, for <see cref="MatchScoreTally"/>'s reasons: the caller
    /// is a <c>MonoBehaviour</c> no test can run, and the id space is <c>MAX_ACTORS</c> slots.
    /// </para>
    /// <para>
    /// <b>Kills, deaths, headshots, streaks and points are not here.</b> The score tally already
    /// counts them; a second count would be a second answer that could disagree with the board.
    /// The report reads them there (<see cref="Fill"/> takes the tally).
    /// </para>
    /// </remarks>
    public sealed class MatchCareerTally
    {
        /// <summary>Seconds between kills that still make one multi-kill.</summary>
        public const float MultiKillWindowSeconds = 3f;

        /// <summary>Seconds after deploying in which a kill is a quick kill.</summary>
        public const float QuickKillSeconds = 3f;

        /// <summary>Seconds within which two kills by one grenade count as the same blast.</summary>
        public const float SameBlastSeconds = 0.5f;

        /// <summary>A round with this many kills and no deaths is flawless.</summary>
        public const int FlawlessKills = 15;

        /// <summary>The deficit a winning side must have come back from for a comeback win.</summary>
        public const int ComebackDeficit = 100;

        private const int Actors = ProtocolConstants.MAX_ACTORS;
        private const ushort Nobody = ushort.MaxValue;

        private readonly long[,] _stats = new long[Actors, CareerStats.Count];
        private readonly float[] _lastKillTime = new float[Actors];
        private readonly int[] _multi = new int[Actors];
        private readonly int[] _headshotRun = new int[Actors];
        private readonly float[] _lastGrenadeKill = new float[Actors];
        private readonly float[] _spawnTime = new float[Actors];
        private readonly ushort[] _lastKiller = new ushort[Actors];
        private int _maxDeficit0;
        private int _maxDeficit1;

        public MatchCareerTally() => Clear();

        /// <summary>One stat of one actor this round.</summary>
        public long Get(ushort actor, CareerStat stat) => actor < Actors ? _stats[actor, (int)stat] : 0;

        /// <summary>The actor deployed at <paramref name="time"/> seconds (for quick kills).</summary>
        public void NoteSpawn(ushort actor, float time)
        {
            if (actor >= Actors) return;
            _spawnTime[actor] = time;
        }

        /// <summary>The round's score after a change, so a comeback can be recognised at the end.</summary>
        public void NoteScores(int score0, int score1)
        {
            _maxDeficit0 = Math.Max(_maxDeficit0, score1 - score0);
            _maxDeficit1 = Math.Max(_maxDeficit1, score0 - score1);
        }

        /// <summary>Counts one resolved kill at <paramref name="time"/> seconds into the round.</summary>
        public void RecordKill(in CareerKill kill, float time)
        {
            ushort killer = kill.Killer;
            ushort victim = kill.Victim;
            if (victim < Actors) RecordDeathOnly(kill);
            if (killer >= Actors || killer == victim) return;

            if (kill.SameTeam)
            {
                Add(killer, CareerStat.TeamKills, 1);
                return;
            }

            Add(killer, kill.VictimIsBot ? CareerStat.BotKills : CareerStat.PlayerKills, 1);

            bool byVehicle = (kill.Detail & DeathDetail.KillerInVehicle) != 0;
            bool melee = (kill.Detail & DeathDetail.Melee) != 0;
            if (melee) Add(killer, CareerStat.MeleeKills, 1);
            if (kill.Cause == CauseOfDeath.Explosion) Add(killer, CareerStat.ExplosiveKills, 1);
            if (kill.Cause == CauseOfDeath.Vehicle && byVehicle) Add(killer, CareerStat.Roadkills, 1);
            if (byVehicle && kill.VehicleType == VehicleIds.TANK) Add(killer, CareerStat.TankKills, 1);
            if (byVehicle && kill.VehicleType == VehicleIds.HELICOPTER) Add(killer, CareerStat.HelicopterKills, 1);
            if (byVehicle && kill.VehicleType == VehicleIds.RHIB) Add(killer, CareerStat.BoatKills, 1);
            if (!byVehicle && (kill.Detail & DeathDetail.WentDownWithVehicle) != 0 && kill.VehicleType == VehicleIds.TANK)
                Add(killer, CareerStat.TanksDestroyed, 1);

            if (kill.WeaponId == WeaponIds.FRAG)
            {
                Add(killer, CareerStat.GrenadeKills, 1);
                if (time - _lastGrenadeKill[killer] <= SameBlastSeconds) Add(killer, CareerStat.GrenadeDoubleKills, 1);
                _lastGrenadeKill[killer] = time;
            }

            Max(killer, CareerStat.LongestKillMetres, kill.DistanceMetres);
            if (kill.Headshot) Max(killer, CareerStat.LongestHeadshotMetres, kill.DistanceMetres);

            _multi[killer] = time - _lastKillTime[killer] <= MultiKillWindowSeconds ? _multi[killer] + 1 : 1;
            _lastKillTime[killer] = time;
            Max(killer, CareerStat.BestMultiKill, _multi[killer]);

            _headshotRun[killer] = kill.Headshot ? _headshotRun[killer] + 1 : 0;
            Max(killer, CareerStat.HeadshotRun, _headshotRun[killer]);

            if (_lastKiller[killer] == victim)
            {
                Add(killer, CareerStat.RevengeKills, 1);
                _lastKiller[killer] = Nobody;
            }

            if (time - _spawnTime[killer] <= QuickKillSeconds) Add(killer, CareerStat.QuickKills, 1);
        }

        /// <summary>A death with no killer to credit: the world, a fall, the water, one's own blast.</summary>
        public void RecordDeath(ushort victim, ushort killer, CauseOfDeath cause)
            => RecordDeathOnly(new CareerKill(killer, victim, false, false, false, 0, cause, WeaponIds.NONE, VehicleIds.NONE, DeathDetail.None));

        private void RecordDeathOnly(in CareerKill kill)
        {
            ushort victim = kill.Victim;
            if (victim >= Actors) return;

            _multi[victim] = 0;
            _headshotRun[victim] = 0;
            if (kill.Cause == CauseOfDeath.Fall) Add(victim, CareerStat.FallDeaths, 1);
            if (kill.Cause == CauseOfDeath.Drown) Add(victim, CareerStat.DrownDeaths, 1);
            if (kill.Cause == CauseOfDeath.Explosion && kill.Killer == victim) Add(victim, CareerStat.OwnExplosiveDeaths, 1);
            _lastKiller[victim] = kill.Killer < Actors && kill.Killer != victim ? kill.Killer : Nobody;
        }

        /// <summary>A flag turned to <paramref name="actor"/>'s side while they stood in it.</summary>
        public void CreditCapture(ushort actor) => Add(actor, CareerStat.FlagsCaptured, 1);

        /// <summary>
        /// Writes one actor's round into <paramref name="into"/> (indexed by <see cref="CareerStat"/>):
        /// this tally's counts, the score tally's, and the round's result.
        /// </summary>
        /// <param name="mostPoints">Whether no actor in the round scored more points.</param>
        public void Fill(ushort actor, MatchScoreTally scores, byte team, byte winningTeam, int secondsPlayed,
            bool mostPoints, long[] into)
        {
            if (into == null || into.Length < CareerStats.Count) throw new ArgumentException("needs one slot per CareerStat", nameof(into));
            Array.Clear(into, 0, into.Length);
            if (actor >= Actors) return;

            for (int i = 0; i < CareerStats.Count; i++) into[i] = _stats[actor, i];

            int kills = scores.KillsOf(actor);
            int deaths = scores.DeathsOf(actor);
            bool won = winningTeam != TeamId.None && team == winningTeam;

            into[(int)CareerStat.Matches] = 1;
            into[(int)CareerStat.Wins] = won ? 1 : 0;
            into[(int)CareerStat.Kills] = kills;
            into[(int)CareerStat.Deaths] = deaths;
            into[(int)CareerStat.Score] = scores.PointsOf(actor);
            into[(int)CareerStat.Headshots] = scores.HeadshotsOf(actor);
            into[(int)CareerStat.BestStreak] = scores.BestStreakOf(actor);
            into[(int)CareerStat.SecondsPlayed] = Math.Max(0, secondsPlayed);
            into[(int)CareerStat.FlawlessRounds] = kills >= FlawlessKills && deaths == 0 ? 1 : 0;
            into[(int)CareerStat.MvpRounds] = won && mostPoints && scores.PointsOf(actor) > 0 ? 1 : 0;
            int deficit = team == TeamId.Team0 ? _maxDeficit0 : team == TeamId.Team1 ? _maxDeficit1 : 0;
            into[(int)CareerStat.ComebackWins] = won && deficit >= ComebackDeficit ? 1 : 0;
        }

        /// <summary>Forgets one actor (a player left; the slot may be reused).</summary>
        public void Forget(ushort actor)
        {
            if (actor >= Actors) return;
            for (int i = 0; i < CareerStats.Count; i++) _stats[actor, i] = 0;
            _lastKillTime[actor] = float.NegativeInfinity;
            _lastGrenadeKill[actor] = float.NegativeInfinity;
            _spawnTime[actor] = float.NegativeInfinity;
            _multi[actor] = 0;
            _headshotRun[actor] = 0;
            _lastKiller[actor] = Nobody;
        }

        /// <summary>Starts a new round.</summary>
        public void Clear()
        {
            for (ushort a = 0; a < Actors; a++) Forget(a);
            _maxDeficit0 = 0;
            _maxDeficit1 = 0;
        }

        private void Add(ushort actor, CareerStat stat, long amount)
        {
            if (actor < Actors) _stats[actor, (int)stat] += amount;
        }

        private void Max(ushort actor, CareerStat stat, long value)
        {
            if (actor < Actors && value > _stats[actor, (int)stat]) _stats[actor, (int)stat] = value;
        }
    }
}
