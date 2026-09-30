using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Ai
{
    /// <summary>One squad folding into another: <see cref="From"/> joins <see cref="Into"/>.</summary>
    public struct SquadMerge
    {
        /// <summary>The squad that ceases to exist, as its index in the list the plan was made from.</summary>
        public int From;

        /// <summary>The squad its members join.</summary>
        public int Into;
    }

    /// <summary>
    /// Folds a side's lone bots back into squads: the commander's "assign each free bot to the
    /// closest squad in need of more bots" (Straatman et al., "Hierarchical AI for Multiplayer Bots
    /// in Killzone 3", Game AI Pro, ch. 29). Phase P29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> Squads in the original only ever split or shrank, never joined: a live
    /// match drifted to one bot per squad within minutes (sixteen bots in fifteen squads on
    /// Dustbowl), so the commander planned for bots, not squads, and nobody moved or took cover
    /// together. Measured in an offline match on 2026-09-30: 48 of 53 squads a respawn wave formed
    /// had a single bot, and 162 members were split off on their own.
    /// </para>
    /// <para>
    /// <b>What it will not do.</b> Pull a bot out of a fight (an engaged squad is never the one that
    /// moves), touch a squad in or boarding a vehicle, or grow a squad past <see cref="MaxSize"/>,
    /// the largest a spawn wave forms. A lone bot with nobody within <see cref="JoinRadius"/> stays
    /// alone until it comes near somebody.
    /// </para>
    /// <para>Engine-free and allocation-free, like <see cref="TeamPlanner"/>.</para>
    /// </remarks>
    public sealed class SquadRegroup
    {
        /// <summary>A squad smaller than this looks for one to join.</summary>
        public const int MinSize = 2;

        /// <summary>
        /// No squad grows past this: the largest a spawn wave forms, from a spawn point's
        /// <c>maxSquadSize</c> of 4.
        /// </summary>
        public const int MaxSize = 4;

        /// <summary>
        /// How far a lone bot walks to join a squad, in metres: under half the distance between two
        /// of Dustbowl's flags, so it never crosses the map to do it.
        /// </summary>
        public const float JoinRadius = 80f;

        /// <summary>
        /// What joining a squad with another job costs, in metres of walk: a bot prefers a squad on
        /// its own flag to a slightly nearer one bound elsewhere.
        /// </summary>
        public const float OtherJobPenalty = 25f;

        private readonly int[] _size = new int[TeamPlanner.MaxSquads];
        private readonly bool[] _gone = new bool[TeamPlanner.MaxSquads];
        private readonly int[] _order = new int[TeamPlanner.MaxSquads];

        /// <summary>
        /// Writes the merges for one side's squads into <paramref name="merges"/> and returns how
        /// many there are. Smallest squads first, ties by id, so a plan is the same every time.
        /// </summary>
        public int Plan(ReadOnlySpan<SquadInfo> squads, Span<SquadMerge> merges)
        {
            int count = Math.Min(squads.Length, TeamPlanner.MaxSquads);
            for (int i = 0; i < count; i++)
            {
                _size[i] = Math.Max(0, squads[i].Size);
                _gone[i] = false;
                _order[i] = i;
            }

            SortBySizeThenId(squads, count);

            int written = 0;
            for (int n = 0; n < count && written < merges.Length; n++)
            {
                int from = _order[n];
                if (_gone[from] || _size[from] == 0 || _size[from] >= MinSize) continue;

                SquadInfo lone = squads[from];
                if (lone.InVehicle || lone.Engaged) continue;

                int into = BestSquadFor(from, squads, count);
                if (into < 0) continue;

                _size[into] += _size[from];
                _size[from] = 0;
                _gone[from] = true;
                merges[written++] = new SquadMerge { From = from, Into = into };
            }

            return written;
        }

        /// <summary>The nearest squad with room, on foot, preferring one on the same job.</summary>
        private int BestSquadFor(int from, ReadOnlySpan<SquadInfo> squads, int count)
        {
            SquadInfo lone = squads[from];
            int best = -1;
            float bestCost = float.PositiveInfinity;

            for (int i = 0; i < count; i++)
            {
                if (i == from || _gone[i] || _size[i] == 0) continue;

                SquadInfo other = squads[i];
                if (other.InVehicle || _size[i] + _size[from] > MaxSize) continue;

                float distance = Flat(other.Position - lone.Position).Magnitude;
                if (distance > JoinRadius) continue;

                bool sameJob = lone.Role == other.Role && lone.Flag == other.Flag;
                float cost = distance + (sameJob ? 0f : OtherJobPenalty);
                if (cost < bestCost || (cost == bestCost && best >= 0 && other.Id < squads[best].Id))
                {
                    best = i;
                    bestCost = cost;
                }
            }

            return best;
        }

        private void SortBySizeThenId(ReadOnlySpan<SquadInfo> squads, int count)
        {
            // Insertion sort: a side has tens of squads, and this allocates nothing.
            for (int i = 1; i < count; i++)
            {
                int key = _order[i];
                int j = i - 1;
                while (j >= 0 && Before(key, _order[j], squads))
                {
                    _order[j + 1] = _order[j];
                    j--;
                }

                _order[j + 1] = key;
            }
        }

        private bool Before(int a, int b, ReadOnlySpan<SquadInfo> squads)
        {
            if (_size[a] != _size[b]) return _size[a] < _size[b];
            return squads[a].Id < squads[b].Id;
        }

        private static Vec3 Flat(in Vec3 v) => new Vec3(v.X, 0f, v.Z);
    }
}
