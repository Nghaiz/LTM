using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Ai
{
    /// <summary>
    /// One side's commander: reads the flags and the squads and gives every squad a role -- take
    /// this flag, hold that one, come at this one from the side. Phase P28, from the owner's report
    /// of 2026-09-30 ("the bots should count how many of them there are and split up sensibly").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it decides, in order.</b> A posture from the flags each side holds and the score.
    /// Targets: the capturable flags the side does not hold that border one it does, valued by
    /// what they open up, how far away they are and how hard they are held, and as many of them at
    /// once as the side's size supports (<see cref="TacticsProfile.BotsPerObjective"/>). Defence:
    /// the side's frontline flags keep a garrison, more where the enemy is in contact, capped at a
    /// share of the side. Then every squad gets a job -- the ones already doing a job that still
    /// makes sense keep it -- and where two or more squads go for one flag, one of them goes round
    /// the side, quietly.
    /// </para>
    /// <para>
    /// <b>Sticky on purpose.</b> A squad keeps its target until the target falls or a clearly better
    /// one appears (<see cref="TacticsProfile.Stickiness"/>), and a defender keeps its flag while
    /// the flag still needs defending. Bots that re-decide every tick look indecisive, and the
    /// owner asked for decisive.
    /// </para>
    /// <para>
    /// <b>Engine-free and allocation-free</b>: fixed scratch arrays, so it is tested here and runs on
    /// the game server twice a side every couple of seconds without garbage. The same code is what
    /// the tuner trains.
    /// </para>
    /// </remarks>
    public sealed class TeamPlanner
    {
        public const int MaxFlags = 64;
        public const int MaxSquads = 128;

        private readonly TacticsProfile _profile;

        private readonly float[] _value = new float[MaxFlags];
        private readonly bool[] _candidate = new bool[MaxFlags];
        private readonly bool[] _chosen = new bool[MaxFlags];
        private readonly float[] _demand = new float[MaxFlags];
        private readonly int[] _attackBots = new int[MaxFlags];
        private readonly float[] _desired = new float[MaxFlags];
        private readonly bool[] _flanked = new bool[MaxFlags];
        private readonly bool[] _assigned = new bool[MaxSquads];

        public TeamPlanner(TacticsProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public TacticsProfile Profile => _profile;

        /// <summary>The posture of the last plan.</summary>
        public TeamPosture LastPosture { get; private set; }

        /// <summary>How many flags the last plan went for at once.</summary>
        public int LastObjectives { get; private set; }

        /// <summary>How many bots the last plan put on defence.</summary>
        public int LastDefenders { get; private set; }

        /// <summary>
        /// Plans for <paramref name="team"/>: one order per squad, written to
        /// <paramref name="orders"/> in the squads' order. Returns how many were written.
        /// </summary>
        /// <param name="adjacency">Every flag's neighbours, as indices, at its AdjacencyStart.</param>
        public int Plan(
            int team, ReadOnlySpan<FlagInfo> flags, ReadOnlySpan<int> adjacency,
            ReadOnlySpan<SquadInfo> squads, int score, int enemyScore, Span<SquadOrder> orders)
        {
            int flagCount = Math.Min(flags.Length, MaxFlags);
            int squadCount = Math.Min(Math.Min(squads.Length, MaxSquads), orders.Length);
            LastObjectives = 0;
            LastDefenders = 0;
            if (squadCount == 0) return 0;

            int bots = 0;
            for (int i = 0; i < squadCount; i++) bots += Math.Max(0, squads[i].Size);

            TeamPosture posture = PostureOf(team, flags, flagCount, score, enemyScore);
            LastPosture = posture;

            int objectives = ChooseTargets(team, flags, flagCount, adjacency, squads, squadCount, bots, posture);
            LastObjectives = objectives;

            float defenceCap = WeighDefence(team, flags, flagCount, adjacency, bots, posture);

            for (int i = 0; i < squadCount; i++)
            {
                orders[i] = new SquadOrder { SquadIndex = i, Role = SquadRole.None, Flag = -1 };
                _assigned[i] = false;
            }

            Array.Clear(_attackBots, 0, _attackBots.Length);
            Array.Clear(_flanked, 0, _flanked.Length);

            int defenders = KeepStickyOrders(team, flags, flagCount, squads, squadCount, orders);
            defenders = FillDefence(team, flags, flagCount, adjacency, squads, squadCount, orders, defenders, defenceCap);
            LastDefenders = defenders;

            AssignAttackers(flags, flagCount, squads, squadCount, orders, objectives, team, adjacency);
            PickFlankers(flags, flagCount, squads, squadCount, orders, bots);

            for (int i = 0; i < squadCount; i++)
            {
                if (orders[i].Role == SquadRole.Defend)
                    orders[i] = WithDefencePoint(orders[i], team, flags, adjacency);

                orders[i].Changed = orders[i].Role != squads[i].Role || orders[i].Flag != squads[i].Flag;
            }

            return squadCount;
        }

        // ------------------------------------------------------------------ posture

        private TeamPosture PostureOf(int team, ReadOnlySpan<FlagInfo> flags, int flagCount, int score, int enemyScore)
        {
            int ours = 0;
            int theirs = 0;
            for (int f = 0; f < flagCount; f++)
            {
                if (flags[f].Owner == team) ours++;
                else if (flags[f].Owner >= 0) theirs++;
            }

            int margin = _profile.PostureScoreMargin;
            if (ours < theirs || score + margin < enemyScore) return TeamPosture.Aggressive;
            if (ours > theirs && score > enemyScore + margin) return TeamPosture.Defensive;
            return TeamPosture.Balanced;
        }

        // ------------------------------------------------------------------ targets

        /// <summary>Values every capturable flag the side does not hold and marks the best ones chosen.</summary>
        private int ChooseTargets(
            int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<int> adjacency,
            ReadOnlySpan<SquadInfo> squads, int squadCount, int bots, TeamPosture posture)
        {
            bool holdsAny = false;
            bool hasAdjacency = false;
            for (int f = 0; f < flagCount; f++)
            {
                if (flags[f].Owner == team) holdsAny = true;
                if (flags[f].AdjacencyCount > 0) hasAdjacency = true;
            }

            Vec3 squadsCentre = Centre(squads, squadCount);
            int candidates = 0;

            for (int f = 0; f < flagCount; f++)
            {
                _candidate[f] = false;
                _chosen[f] = false;
                _value[f] = float.NegativeInfinity;

                FlagInfo flag = flags[f];
                if (!flag.Capturable || flag.Owner == team) continue;

                bool borders = !holdsAny || !hasAdjacency || BordersSide(f, team, flags, adjacency);
                if (!borders) continue;

                int enemyNeighbours = 0;
                for (int n = 0; n < flag.AdjacencyCount; n++)
                {
                    int other = Neighbour(flag, n, adjacency, flagCount);
                    if (other >= 0 && flags[other].Owner >= 0 && flags[other].Owner != team) enemyNeighbours++;
                }

                Vec3 from = holdsAny ? NearestHeld(flag.Position, team, flags, flagCount) : squadsCentre;
                float hundreds = Vec3.Distance(flag.Position, from) / 100f;

                _value[f] = _profile.TargetBase
                            + (flag.Owner < 0 ? _profile.NeutralBonus : 0f)
                            - _profile.ThreatWeight * flag.EnemiesInContact
                            + _profile.LinkWeight * enemyNeighbours
                            - _profile.DistanceWeight * hundreds;
                _candidate[f] = true;
                candidates++;
            }

            if (candidates == 0) return 0;

            int objectives = (int)Math.Round(bots / Math.Max(1f, _profile.BotsPerObjective));
            if (posture == TeamPosture.Defensive) objectives--;
            objectives = Math.Max(1, Math.Min(objectives, Math.Min(_profile.MaxObjectives, candidates)));

            for (int pick = 0; pick < objectives; pick++)
            {
                int best = -1;
                for (int f = 0; f < flagCount; f++)
                {
                    if (!_candidate[f] || _chosen[f]) continue;
                    if (best < 0 || _value[f] > _value[best]) best = f;
                }

                if (best < 0) break;
                _chosen[best] = true;
            }

            return objectives;
        }

        private static bool BordersSide(int flag, int team, ReadOnlySpan<FlagInfo> flags, ReadOnlySpan<int> adjacency)
        {
            FlagInfo info = flags[flag];
            for (int n = 0; n < info.AdjacencyCount; n++)
            {
                int other = Neighbour(info, n, adjacency, flags.Length);
                if (other >= 0 && flags[other].Owner == team) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ defence

        /// <summary>How many bots each of the side's flags wants, and the most the side will spare.</summary>
        private float WeighDefence(
            int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<int> adjacency,
            int bots, TeamPosture posture)
        {
            Array.Clear(_demand, 0, _demand.Length);

            // A small side needs every bot at the front; a lost flag is taken back by the attack.
            if (bots < _profile.MinBotsToDefend) return 0f;

            float garrison = posture == TeamPosture.Aggressive ? _profile.GarrisonAggressive
                : posture == TeamPosture.Defensive ? _profile.GarrisonDefensive
                : _profile.GarrisonBalanced;

            bool anyTarget = false;
            for (int f = 0; f < flagCount; f++) if (_chosen[f]) anyTarget = true;

            for (int f = 0; f < flagCount; f++)
            {
                FlagInfo flag = flags[f];
                if (flag.Owner != team || !flag.Capturable) continue;

                bool frontline = false;
                for (int n = 0; n < flag.AdjacencyCount; n++)
                {
                    int other = Neighbour(flag, n, adjacency, flagCount);
                    if (other >= 0 && flags[other].Owner != team) frontline = true;
                }

                // With nothing left to take, every bot holds what the side has.
                float demand = (frontline ? garrison : 0f) + flag.EnemiesInContact * _profile.DefendPerThreat;
                if (!anyTarget && frontline) demand = Math.Max(demand, bots);

                _demand[f] = demand;
            }

            float share = posture == TeamPosture.Aggressive ? _profile.MaxDefendShareAggressive
                : posture == TeamPosture.Defensive ? _profile.MaxDefendShareDefensive
                : _profile.MaxDefendShareBalanced;

            return anyTarget ? bots * share : bots;
        }

        // ------------------------------------------------------------------ assignment

        /// <summary>Squads already doing something that still makes sense keep doing it.</summary>
        private int KeepStickyOrders(
            int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<SquadInfo> squads,
            int squadCount, Span<SquadOrder> orders)
        {
            float bestValue = float.NegativeInfinity;
            for (int f = 0; f < flagCount; f++) if (_chosen[f] && _value[f] > bestValue) bestValue = _value[f];

            int defenders = 0;
            for (int i = 0; i < squadCount; i++)
            {
                SquadInfo squad = squads[i];
                int flag = squad.Flag;
                if (flag < 0 || flag >= flagCount) continue;

                if (squad.Role == SquadRole.Defend)
                {
                    if (squad.InVehicle || flags[flag].Owner != team || _demand[flag] <= 0f) continue;

                    Keep(i, squad, orders);
                    _demand[flag] -= squad.Size;
                    defenders += squad.Size;
                    continue;
                }

                if (squad.Role != SquadRole.Attack && squad.Role != SquadRole.Flank) continue;
                if (!flags[flag].Capturable || flags[flag].Owner == team || !_candidate[flag]) continue;

                bool stillWorthIt = _chosen[flag] || _value[flag] + _profile.Stickiness >= bestValue;
                if (!stillWorthIt) continue;

                Keep(i, squad, orders);
                _attackBots[flag] += squad.Size;
                if (squad.Role == SquadRole.Flank) _flanked[flag] = true;
            }

            return defenders;
        }

        private void Keep(int index, in SquadInfo squad, Span<SquadOrder> orders)
        {
            orders[index].Role = squad.Role;
            orders[index].Flag = squad.Flag;
            orders[index].Sneak = squad.Role == SquadRole.Flank;
            _assigned[index] = true;
        }

        /// <summary>Sends the nearest free squads to the flags that most need defending.</summary>
        private int FillDefence(
            int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<int> adjacency,
            ReadOnlySpan<SquadInfo> squads, int squadCount, Span<SquadOrder> orders, int defenders, float cap)
        {
            while (defenders < cap)
            {
                int flag = -1;
                for (int f = 0; f < flagCount; f++)
                {
                    if (_demand[f] < 0.5f) continue;
                    if (flag < 0 || _demand[f] > _demand[flag]) flag = f;
                }

                if (flag < 0) break;

                int squad = NearestFree(flags[flag].Position, squads, squadCount, allowVehicles: false);
                if (squad < 0) break;

                orders[squad].Role = SquadRole.Defend;
                orders[squad].Flag = flag;
                _assigned[squad] = true;
                _demand[flag] -= squads[squad].Size;
                defenders += squads[squad].Size;
            }

            return defenders;
        }

        /// <summary>
        /// Spreads the free squads over the chosen targets, more where the enemy is in contact,
        /// each to the target most short of its share, the nearer on a tie.
        /// </summary>
        private void AssignAttackers(
            ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<SquadInfo> squads, int squadCount,
            Span<SquadOrder> orders, int objectives, int team, ReadOnlySpan<int> adjacency)
        {
            if (objectives == 0)
            {
                // Nothing to take: whoever is free holds the nearest flag of the side's own.
                for (int i = 0; i < squadCount; i++)
                {
                    if (_assigned[i]) continue;
                    int held = NearestHeldIndex(squads[i].Position, team, flags, flagCount);
                    if (held < 0) continue;

                    orders[i].Role = SquadRole.Defend;
                    orders[i].Flag = held;
                    _assigned[i] = true;
                }

                return;
            }

            int free = 0;
            for (int i = 0; i < squadCount; i++) if (!_assigned[i]) free += squads[i].Size;

            float weights = 0f;
            int already = 0;
            for (int f = 0; f < flagCount; f++)
            {
                if (!_chosen[f]) continue;
                weights += 1f + flags[f].EnemiesInContact;
                already += _attackBots[f];
            }

            for (int f = 0; f < flagCount; f++)
                _desired[f] = _chosen[f] ? (free + already) * (1f + flags[f].EnemiesInContact) / weights : 0f;

            // Nearest pairs first: of every free squad and every objective still short of its share,
            // the closest pair is joined, then the next closest. The squads nearest an objective take
            // it, and nobody walks the length of the map past one it could have taken on the way.
            // (Assigning in list order, as this first did, sent whichever squad came first to the
            // flag most short -- often the far one; part 4's simulator measured the cost.)
            while (true)
            {
                int bestSquad = -1;
                int bestFlag = -1;
                float bestDistance = float.PositiveInfinity;
                for (int i = 0; i < squadCount; i++)
                {
                    if (_assigned[i]) continue;
                    for (int f = 0; f < flagCount; f++)
                    {
                        if (!_chosen[f] || _desired[f] - _attackBots[f] <= ShareTolerance) continue;
                        float distance = Vec3.Distance(squads[i].Position, flags[f].Position);
                        if (distance >= bestDistance) continue;
                        bestSquad = i;
                        bestFlag = f;
                        bestDistance = distance;
                    }
                }
                if (bestSquad < 0) break;
                Attack(bestSquad, bestFlag, squads, orders);
            }

            // Every share met and squads left over: each goes for the chosen flag nearest it.
            for (int i = 0; i < squadCount; i++)
            {
                if (_assigned[i]) continue;
                int nearest = -1;
                float nearestDistance = float.PositiveInfinity;
                for (int f = 0; f < flagCount; f++)
                {
                    if (!_chosen[f]) continue;
                    float distance = Vec3.Distance(squads[i].Position, flags[f].Position);
                    if (distance < nearestDistance)
                    {
                        nearest = f;
                        nearestDistance = distance;
                    }
                }
                if (nearest >= 0) Attack(i, nearest, squads, orders);
            }
        }

        /// <summary>An objective counts as short of its share while it lacks more than this many bots.</summary>
        private const float ShareTolerance = 0.25f;

        private void Attack(int squad, int flag, ReadOnlySpan<SquadInfo> squads, Span<SquadOrder> orders)
        {
            orders[squad].Role = SquadRole.Attack;
            orders[squad].Flag = flag;
            _assigned[squad] = true;
            _attackBots[flag] += squads[squad].Size;
        }

        /// <summary>
        /// Where two or more squads on foot go for one flag, the second-nearest of them goes round
        /// the side through a waypoint off the attack axis, quietly, while the nearest goes straight
        /// in. Six or more squads on one flag send a second flank round the other side: a pincer.
        /// </summary>
        private void PickFlankers(
            ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<SquadInfo> squads, int squadCount,
            Span<SquadOrder> orders, int bots)
        {
            if (bots < _profile.MinBotsToFlank) return;

            for (int f = 0; f < flagCount; f++)
            {
                if (!_chosen[f]) continue;

                int onFoot = 0;
                int flanking = 0;
                Vec3 centre = Vec3.Zero;

                for (int i = 0; i < squadCount; i++)
                {
                    if (orders[i].Flag != f || squads[i].InVehicle) continue;
                    if (orders[i].Role != SquadRole.Attack && orders[i].Role != SquadRole.Flank) continue;

                    onFoot++;
                    centre = centre + squads[i].Position;
                    if (orders[i].Role == SquadRole.Flank) flanking++;
                }

                if (onFoot < 2) continue;
                centre = centre * (1f / onFoot);

                int wanted = onFoot >= 6 ? 2 : 1;
                float firstSide = 0f;

                // Flanks already under way keep their squads, and their sides.
                for (int i = 0; i < squadCount; i++)
                {
                    if (orders[i].Flag != f || orders[i].Role != SquadRole.Flank || squads[i].InVehicle) continue;

                    orders[i].Sneak = true;
                    orders[i].Waypoint = FlankWaypoint(flags[f].Position, centre, squads[i], firstSide);
                    orders[i].HasWaypoint = true;
                    if (firstSide == 0f) firstSide = SideOf(flags[f].Position, centre, orders[i].Waypoint);
                }

                // New flanks: the free attackers nearest the flag after the one leading the assault,
                // a second one forced round the other side.
                while (flanking < wanted)
                {
                    int pick = NthNearestAttacker(f, flags[f].Position, squads, squadCount, orders, skip: 1);
                    if (pick < 0) break;

                    orders[pick].Role = SquadRole.Flank;
                    orders[pick].Sneak = true;
                    orders[pick].Waypoint = FlankWaypoint(flags[f].Position, centre, squads[pick], -firstSide);
                    orders[pick].HasWaypoint = true;
                    if (firstSide == 0f) firstSide = SideOf(flags[f].Position, centre, orders[pick].Waypoint);
                    flanking++;
                }
            }
        }

        /// <summary>
        /// The attacking squad on foot, not engaged, that is the (<paramref name="skip"/>+1)-th
        /// nearest to the flag among those still attacking head-on; -1 when there is none.
        /// </summary>
        private static int NthNearestAttacker(
            int flag, in Vec3 target, ReadOnlySpan<SquadInfo> squads, int squadCount, Span<SquadOrder> orders, int skip)
        {
            int seen = 0;
            int found = -1;
            float last = -1f;

            // Walk the attackers in order of distance without sorting: pick the next nearest each pass.
            for (int pass = 0; pass <= skip; pass++)
            {
                int best = -1;
                float bestDistance = float.PositiveInfinity;

                for (int i = 0; i < squadCount; i++)
                {
                    if (orders[i].Flag != flag || orders[i].Role != SquadRole.Attack || squads[i].InVehicle) continue;

                    float distance = Vec3.Distance(squads[i].Position, target);
                    if (distance < last || (distance == last && i <= found)) continue;
                    if (distance < bestDistance || (distance == bestDistance && i < best))
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }

                if (best < 0) return -1;
                found = best;
                last = bestDistance;
                seen++;
            }

            return found >= 0 && !squads[found].Engaged ? found : -1;
        }

        /// <summary>Which side of the attack axis a point lies on: +1, -1, or 0 on the line.</summary>
        private static float SideOf(in Vec3 target, in Vec3 attackersCentre, in Vec3 point)
        {
            Vec3 axis = Flat(target - attackersCentre);
            var side = new Vec3(-axis.Z, 0f, axis.X);
            Vec3 offset = Flat(point - target);
            float lean = offset.X * side.X + offset.Z * side.Z;
            return lean > 0f ? 1f : lean < 0f ? -1f : 0f;
        }

        /// <summary>
        /// A point off to the side of the line from the attackers to the flag, a little short of
        /// it, on the side the flanking squad already stands (or by its id, standing on the line).
        /// </summary>
        public Vec3 FlankWaypoint(in Vec3 target, in Vec3 attackersCentre, in SquadInfo squad, float forcedSide = 0f)
        {
            Vec3 axis = Flat(target - attackersCentre);
            float length = axis.Magnitude;
            axis = length > 0.01f ? axis * (1f / length) : new Vec3(0f, 0f, 1f);

            var side = new Vec3(-axis.Z, 0f, axis.X);
            Vec3 offset = Flat(squad.Position - target);
            float lean = offset.X * side.X + offset.Z * side.Z;
            float sign = forcedSide != 0f ? Math.Sign(forcedSide)
                : Math.Abs(lean) > 1f ? Math.Sign(lean)
                : (squad.Id % 2 == 0 ? 1f : -1f);

            return target + side * (sign * _profile.FlankOffset) - axis * _profile.FlankStandoff;
        }

        /// <summary>The spot a defence digs in round: a little in front of the flag, toward the nearest threat.</summary>
        private SquadOrder WithDefencePoint(SquadOrder order, int team, ReadOnlySpan<FlagInfo> flags, ReadOnlySpan<int> adjacency)
        {
            FlagInfo flag = flags[order.Flag];
            int threat = -1;
            float threatDistance = float.PositiveInfinity;

            for (int n = 0; n < flag.AdjacencyCount; n++)
            {
                int other = Neighbour(flag, n, adjacency, flags.Length);
                if (other < 0 || flags[other].Owner == team) continue;

                float distance = Vec3.Distance(flag.Position, flags[other].Position);
                if (distance < threatDistance)
                {
                    threat = other;
                    threatDistance = distance;
                }
            }

            Vec3 forward = threat >= 0 ? Flat(flags[threat].Position - flag.Position).Normalized : Vec3.Zero;
            order.Waypoint = flag.Position + forward * _profile.DefendForward;
            order.HasWaypoint = true;
            order.Sneak = false;
            return order;
        }

        // ------------------------------------------------------------------ helpers

        private int NearestFree(in Vec3 point, ReadOnlySpan<SquadInfo> squads, int squadCount, bool allowVehicles)
        {
            int best = -1;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < squadCount; i++)
            {
                if (_assigned[i]) continue;
                if (!allowVehicles && squads[i].InVehicle) continue;

                float distance = Vec3.Distance(point, squads[i].Position);
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static int NearestHeldIndex(in Vec3 point, int team, ReadOnlySpan<FlagInfo> flags, int flagCount)
        {
            int best = -1;
            float bestDistance = float.PositiveInfinity;

            for (int f = 0; f < flagCount; f++)
            {
                if (flags[f].Owner != team) continue;

                float distance = Vec3.Distance(point, flags[f].Position);
                if (distance < bestDistance)
                {
                    best = f;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static Vec3 NearestHeld(in Vec3 point, int team, ReadOnlySpan<FlagInfo> flags, int flagCount)
        {
            Vec3 best = point;
            float bestDistance = float.PositiveInfinity;

            for (int f = 0; f < flagCount; f++)
            {
                if (flags[f].Owner != team) continue;

                float distance = Vec3.Distance(point, flags[f].Position);
                if (distance < bestDistance)
                {
                    best = flags[f].Position;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static Vec3 Centre(ReadOnlySpan<SquadInfo> squads, int squadCount)
        {
            if (squadCount == 0) return Vec3.Zero;

            Vec3 sum = Vec3.Zero;
            for (int i = 0; i < squadCount; i++) sum = sum + squads[i].Position;
            return sum * (1f / squadCount);
        }

        private static int Neighbour(in FlagInfo flag, int n, ReadOnlySpan<int> adjacency, int flagCount)
        {
            int at = flag.AdjacencyStart + n;
            if (at < 0 || at >= adjacency.Length) return -1;

            int other = adjacency[at];
            return other >= 0 && other < flagCount ? other : -1;
        }

        private static Vec3 Flat(in Vec3 v) => new Vec3(v.X, 0f, v.Z);
    }
}
