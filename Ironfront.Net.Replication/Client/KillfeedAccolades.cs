using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>What one kill earned its killer, as the killfeed announces it.</summary>
    /// <remarks>Bits, because one kill can be a double kill, a revenge and a long shot at once.</remarks>
    [Flags]
    public enum KillfeedAccolade : ushort
    {
        None = 0,

        /// <summary>The second or later kill inside <see cref="KillfeedAccoladeTracker.MultiKillWindowSeconds"/> of the last.</summary>
        MultiKill = 1 << 0,

        /// <summary>A round number of kills without dying: 5, 10, 15...</summary>
        Streak = 1 << 1,

        /// <summary>The round's first kill of an enemy.</summary>
        FirstBlood = 1 << 2,

        /// <summary>The killer took down whoever last killed them.</summary>
        Revenge = 1 << 3,

        /// <summary>The kill ended a streak of <see cref="KillfeedAccoladeTracker.ShutdownMinStreak"/> or more.</summary>
        Shutdown = 1 << 4,

        /// <summary>Killer and victim were on the same side. Friendly fire is part of the game (owner ruling).</summary>
        TeamKill = 1 << 5,

        /// <summary>The victim was at least <see cref="KillfeedAccoladeTracker.LongShotMetres"/> away.</summary>
        LongShot = 1 << 6,
    }

    /// <summary>The accolades of one kill, with the counts they are announced with.</summary>
    public readonly struct KillfeedAccolades
    {
        public KillfeedAccolades(KillfeedAccolade flags, int multiKill, int streak, int endedStreak)
        {
            Flags = flags;
            MultiKill = multiKill;
            Streak = streak;
            EndedStreak = endedStreak;
        }

        public KillfeedAccolade Flags { get; }

        /// <summary>Kills in the killer's current chain, this one included: 2 for a double kill.</summary>
        public int MultiKill { get; }

        /// <summary>The killer's kills since they last died, this one included.</summary>
        public int Streak { get; }

        /// <summary>The streak this death ended for the victim.</summary>
        public int EndedStreak { get; }

        public bool Has(KillfeedAccolade accolade) => (Flags & accolade) != 0;
    }

    /// <summary>
    /// Works out what each kill earned from the stream of deaths every client receives. Owner's
    /// report of 2026-09-30: the killfeed should say more about the match than who shot whom.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Client-side, and that is enough.</b> <c>S_DEATH</c> is broadcast to every client, so
    /// every client sees the same deaths in the same order and counts the same chains and
    /// streaks. A player who joins mid-round starts counting from the deaths they saw, which for
    /// a feed about the last few seconds is the right horizon.
    /// </para>
    /// <para>
    /// <b>Only a scored kill of an enemy earns anything.</b> A team kill is announced as one and
    /// counts toward nothing; the world's deaths and a victim's own end the victim's streak and
    /// earn nobody a thing.
    /// </para>
    /// <para>Engine-free and allocation-free: arrays over the actor id space.</para>
    /// </remarks>
    public sealed class KillfeedAccoladeTracker
    {
        /// <summary>A kill this soon after the killer's last one continues the chain.</summary>
        public const float MultiKillWindowSeconds = 4f;

        /// <summary>A streak is announced every this many kills.</summary>
        public const int StreakStep = 5;

        /// <summary>Ending a streak at least this long is a shutdown.</summary>
        public const int ShutdownMinStreak = 5;

        /// <summary>A kill from at least this far is a long shot.</summary>
        public const ushort LongShotMetres = 100;

        private const ushort Nobody = DeathMessage.EnvironmentKiller;

        private readonly int[] _streak = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _chain = new int[ProtocolConstants.MAX_ACTORS];
        private readonly float[] _lastKillAt = new float[ProtocolConstants.MAX_ACTORS];
        private readonly ushort[] _lastKilledBy = new ushort[ProtocolConstants.MAX_ACTORS];
        private bool _firstBloodTaken;

        public KillfeedAccoladeTracker() => Reset();

        /// <summary>
        /// Records one death and returns what it earned. The entry's sides must already be
        /// resolved (<see cref="KillfeedEntry.WithTeams"/>); an unknown side is never a team kill.
        /// </summary>
        public KillfeedAccolades Observe(in KillfeedEntry death, float nowSeconds)
        {
            if (death.Kind != KillfeedKind.Death) return default;

            ushort killer = death.KillerActorId;
            ushort victim = death.VictimActorId;

            int ended = 0;
            if (victim < _streak.Length)
            {
                ended = _streak[victim];
                _streak[victim] = 0;
                _chain[victim] = 0;
            }

            if (!death.IsScoredKill || killer >= _streak.Length)
            {
                if (victim < _lastKilledBy.Length) _lastKilledBy[victim] = Nobody;
                return default;
            }

            bool teamKill = death.KillerTeam != TeamId.None && death.KillerTeam == death.VictimTeam;
            if (teamKill)
            {
                if (victim < _lastKilledBy.Length) _lastKilledBy[victim] = Nobody;
                return new KillfeedAccolades(KillfeedAccolade.TeamKill, 0, _streak[killer], ended);
            }

            KillfeedAccolade flags = KillfeedAccolade.None;

            bool chained = _chain[killer] > 0 && nowSeconds - _lastKillAt[killer] <= MultiKillWindowSeconds;
            _chain[killer] = chained ? _chain[killer] + 1 : 1;
            _lastKillAt[killer] = nowSeconds;
            if (_chain[killer] >= 2) flags |= KillfeedAccolade.MultiKill;

            _streak[killer]++;
            if (_streak[killer] % StreakStep == 0) flags |= KillfeedAccolade.Streak;

            if (!_firstBloodTaken)
            {
                _firstBloodTaken = true;
                flags |= KillfeedAccolade.FirstBlood;
            }

            if (_lastKilledBy[killer] == victim)
            {
                flags |= KillfeedAccolade.Revenge;
                _lastKilledBy[killer] = Nobody;
            }

            if (ended >= ShutdownMinStreak) flags |= KillfeedAccolade.Shutdown;
            if (death.DistanceMetres >= LongShotMetres) flags |= KillfeedAccolade.LongShot;

            if (victim < _lastKilledBy.Length) _lastKilledBy[victim] = killer;

            return new KillfeedAccolades(flags, _chain[killer], _streak[killer], ended);
        }

        /// <summary>
        /// Forgets one actor, for a player slot handed to a new occupant: the newcomer inherits
        /// no streak and no score to settle.
        /// </summary>
        public void Forget(ushort actorId)
        {
            if (actorId >= _streak.Length) return;

            _streak[actorId] = 0;
            _chain[actorId] = 0;
            _lastKilledBy[actorId] = Nobody;

            for (int i = 0; i < _lastKilledBy.Length; i++)
                if (_lastKilledBy[i] == actorId) _lastKilledBy[i] = Nobody;
        }

        /// <summary>Starts a new round: every streak and chain ends, and first blood is up for grabs.</summary>
        public void Reset()
        {
            Array.Clear(_streak, 0, _streak.Length);
            Array.Clear(_chain, 0, _chain.Length);
            Array.Clear(_lastKillAt, 0, _lastKillAt.Length);
            for (int i = 0; i < _lastKilledBy.Length; i++) _lastKilledBy[i] = Nobody;
            _firstBloodTaken = false;
        }
    }
}
