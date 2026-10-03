using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Ai
{
    /// <summary>
    /// One side's commander: reads the flags and the squads and gives every squad a job -- take
    /// this flag, hold that one, come at this one from the side, gather here first. Phase P28, from
    /// the owner's report of 2026-09-30 ("the bots should count how many of them there are and split
    /// up sensibly"); phase P29 rebuilt how it decides on the literature (below).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it decides.</b> Every squad scores every job it could do -- each flag the side does not
    /// hold, and each of its own flags on the front line or under threat -- with one utility: what
    /// the flag is worth, less how far away it is, plus what the squad adds toward the force the
    /// flag needs, less what it would add past that, less the deficit if even with it the side would
    /// be outnumbered there. The best squad-and-job pair of all is settled first, the force the flag
    /// has is counted, and the rest are scored again: an auction, one lot at a time. Then an assault
    /// on a defended flag gathers short of it and goes in together, and where two or more squads go
    /// for one flag, one of them goes round the side, quietly.
    /// </para>
    /// <para>
    /// <b>Where it comes from.</b> One utility per option over several considerations is the utility
    /// AI of Dave Mark's Infinite Axis Utility System; settling the best pair first and re-pricing
    /// the rest is market-based task allocation from multi-robot coordination. The force a flag
    /// needs is Lanchester's attrition law in the form Stanescu, Barriga and Buro fitted to battles
    /// ("Using Lanchester Attrition Laws for Combat Prediction in StarCraft", AIIDE 2015): E defenders
    /// worth k attackers each take more than E * k^(1/n) to beat, with the order n and the worth k
    /// trained rather than assumed. Gathering before an assault is the regroup point Killzone 3's
    /// commander sends squads to "to form up before attacks" (Straatman et al., Game AI Pro, ch. 29);
    /// the remembered threat is a one-number influence map per flag (Tozour, Game Programming Gems 2).
    /// </para>
    /// <para>
    /// <b>Why an auction and not a plan.</b> P28 chose N targets for the side and then shared squads
    /// out over them. Trained, it still lost narrowly to the original squads, which each simply go
    /// for the nearest flag that needs taking -- and a planner that fixes the targets first cannot
    /// express that. This one can: with every weight but distance at zero it IS the original, so
    /// training starts from the baseline and can only be rewarded for doing better than it.
    /// Reading list: <c>plans/reports/2026-09-30-p29-bot-ai-research.md</c>.
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

        /// <summary>A gathering squad counts as there within this many metres of its rally point.</summary>
        public const float RallyRadius = 30f;

        /// <summary>
        /// A fight this close past the rally distance means the assault has already met the
        /// defenders, so it goes in rather than wait.
        /// </summary>
        public const float ContactSlack = 20f;

        private readonly TacticsProfile _profile;

        // Facts about each flag, for one plan.
        private readonly bool[] _target = new bool[MaxFlags];
        private readonly bool[] _retake = new bool[MaxFlags];
        private readonly bool[] _post = new bool[MaxFlags];
        private readonly bool[] _borders = new bool[MaxFlags];
        private readonly int[] _enemyNeighbours = new int[MaxFlags];
        private readonly float[] _need = new float[MaxFlags];
        private readonly float[] _friends = new float[MaxFlags];
        private readonly bool[] _chosen = new bool[MaxFlags];
        private readonly bool[] _assigned = new bool[MaxSquads];
        private readonly float[,] _utility = new float[MaxSquads, MaxFlags];

        // What the side remembers from one plan to the next (phase P29).
        private readonly float[] _threat = new float[MaxFlags];
        private readonly bool[] _go = new bool[MaxFlags];
        private readonly float[] _gatheringSince = new float[MaxFlags];
        private float _lastPlanTime = float.NaN;

        public TeamPlanner(TacticsProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            for (int f = 0; f < MaxFlags; f++) _gatheringSince[f] = float.NaN;
        }

        public TacticsProfile Profile => _profile;

        /// <summary>The posture of the last plan.</summary>
        public TeamPosture LastPosture { get; private set; }

        /// <summary>How many flags the last plan went for at once.</summary>
        public int LastObjectives { get; private set; }

        /// <summary>How many bots the last plan put on defence.</summary>
        public int LastDefenders { get; private set; }

        /// <summary>How many squads the last plan told to gather short of a defended flag.</summary>
        public int LastGathering { get; private set; }

        /// <summary>The enemies the side remembers round <paramref name="flag"/>, as of the last plan.</summary>
        public float RememberedThreat(int flag) => flag >= 0 && flag < MaxFlags ? _threat[flag] : 0f;

        /// <summary>The bots the last plan reckoned <paramref name="flag"/> needs: to take it, or to hold it.</summary>
        public float Need(int flag) => flag >= 0 && flag < MaxFlags ? _need[flag] : 0f;

        /// <summary>
        /// Plans for <paramref name="team"/> with no clock: nothing is remembered from one plan to the
        /// next and no assault waits to gather. What a test that asks about one plan wants.
        /// </summary>
        public int Plan(
            int team, ReadOnlySpan<FlagInfo> flags, ReadOnlySpan<int> adjacency,
            ReadOnlySpan<SquadInfo> squads, int score, int enemyScore, Span<SquadOrder> orders)
            => Plan(team, flags, adjacency, squads, score, enemyScore, orders, float.NaN);

        /// <summary>
        /// Plans for <paramref name="team"/> at <paramref name="time"/> seconds: one order per squad,
        /// written to <paramref name="orders"/> in the squads' order. Returns how many were written.
        /// </summary>
        /// <param name="adjacency">Every flag's neighbours, as indices, at its AdjacencyStart.</param>
        public int Plan(
            int team, ReadOnlySpan<FlagInfo> flags, ReadOnlySpan<int> adjacency,
            ReadOnlySpan<SquadInfo> squads, int score, int enemyScore, Span<SquadOrder> orders, float time)
        {
            int flagCount = Math.Min(flags.Length, MaxFlags);
            int squadCount = Math.Min(Math.Min(squads.Length, MaxSquads), orders.Length);
            LastObjectives = 0;
            LastDefenders = 0;
            LastGathering = 0;

            RememberThreats(flags, flagCount, time);
            if (squadCount == 0) return 0;

            int bots = 0;
            for (int i = 0; i < squadCount; i++) bots += Math.Max(0, squads[i].Size);

            TeamPosture posture = PostureOf(team, flags, flagCount, score, enemyScore);
            LastPosture = posture;

            for (int i = 0; i < squadCount; i++)
            {
                orders[i] = new SquadOrder { SquadIndex = i, Role = SquadRole.None, Flag = -1 };
                _assigned[i] = false;
            }

            ReadFlags(team, flags, flagCount, adjacency);
            Auction(team, flags, flagCount, squads, squadCount, orders, bots, posture);
            Gather(team, flags, flagCount, squads, squadCount, orders, time);
            PickFlankers(flags, flagCount, squads, squadCount, orders, bots);

            for (int i = 0; i < squadCount; i++)
            {
                if (orders[i].Role == SquadRole.Defend)
                    orders[i] = WithDefencePoint(orders[i], team, flags, adjacency);

                orders[i].Changed = orders[i].Role != squads[i].Role || orders[i].Flag != squads[i].Flag;
            }

            return squadCount;
        }

        // ------------------------------------------------------------------ what the side knows

        /// <summary>
        /// The enemies round each flag: those in contact now, or fewer and fewer of those seen before
        /// as <see cref="TacticsProfile.ThreatMemorySeconds"/> passes. With no clock, only the present.
        /// </summary>
        private void RememberThreats(ReadOnlySpan<FlagInfo> flags, int flagCount, float time)
        {
            float keep = 0f;
            if (!float.IsNaN(time) && !float.IsNaN(_lastPlanTime) && time >= _lastPlanTime
                && _profile.ThreatMemorySeconds > 0f)
            {
                keep = (float)Math.Exp(-(time - _lastPlanTime) / _profile.ThreatMemorySeconds);
            }

            for (int f = 0; f < flagCount; f++)
                _threat[f] = Math.Max(flags[f].EnemiesInContact, _threat[f] * keep);
            for (int f = flagCount; f < MaxFlags; f++) _threat[f] = 0f;

            _lastPlanTime = time;
        }

        /// <summary>What one defender in cover is worth in attackers, through the attrition order: k^(1/n).</summary>
        private double DefenderWorth()
            => Math.Pow(Math.Max(1f, _profile.DefenderAdvantage), 1.0 / Math.Max(0.5f, _profile.AttritionOrder));

        /// <summary>
        /// The bots it takes to win a flag held by <paramref name="threat"/> enemies: Lanchester's law
        /// of order n with defenders worth k attackers each, plus a margin; never fewer than the
        /// smallest force an objective is given.
        /// </summary>
        public float ForceFor(float threat)
        {
            float floor = Math.Max(1f, _profile.BotsPerObjective);
            if (threat <= 0.01f) return floor;

            float needed = (float)Math.Ceiling(threat * DefenderWorth() * (1f + Math.Max(0f, _profile.ForceMargin)));
            return Math.Max(floor, needed);
        }

        /// <summary>
        /// The bots it takes to hold one of the side's flags against <paramref name="threat"/>
        /// attackers, dug in: the same law from the other side, so fewer than they are; one bot
        /// watches a quiet front-line flag.
        /// </summary>
        public float GuardFor(float threat)
        {
            if (threat < 0.5f) return 1f;
            return Math.Max(1f, (float)Math.Ceiling(threat / DefenderWorth() * (1f + Math.Max(0f, _profile.ForceMargin))));
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

        // ------------------------------------------------------------------ the jobs on offer

        /// <summary>
        /// Which flags are jobs this plan: every capturable flag the side does not hold, or holds with
        /// an enemy on it, is a target; every other capturable flag it holds on the front line, or
        /// with an enemy remembered round it, is a post. Each gets the force it needs.
        /// </summary>
        private void ReadFlags(int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<int> adjacency)
        {
            bool holdsAny = false;
            bool hasAdjacency = false;
            for (int f = 0; f < flagCount; f++)
            {
                if (flags[f].Owner == team) holdsAny = true;
                if (flags[f].AdjacencyCount > 0) hasAdjacency = true;
            }

            for (int f = 0; f < MaxFlags; f++)
            {
                _target[f] = false;
                _retake[f] = false;
                _post[f] = false;
                _chosen[f] = false;
                _friends[f] = 0f;
                _need[f] = 0f;
                _enemyNeighbours[f] = 0;
                _borders[f] = false;
            }

            for (int f = 0; f < flagCount; f++)
            {
                FlagInfo flag = flags[f];
                if (!flag.Capturable) continue;

                bool own = flag.Owner == team;
                bool frontline = false;
                for (int n = 0; n < flag.AdjacencyCount; n++)
                {
                    int other = Neighbour(flag, n, adjacency, flagCount);
                    if (other < 0) continue;
                    if (flags[other].Owner != team) frontline = true;
                    if (flags[other].Owner >= 0 && flags[other].Owner != team) _enemyNeighbours[f]++;
                }

                if (own && flag.Contested)
                {
                    // Being taken right now: a target like any other, and one that never waits to gather.
                    _target[f] = true;
                    _retake[f] = true;
                    _borders[f] = true;
                    _need[f] = ForceFor(_threat[f]);
                }
                else if (own)
                {
                    _post[f] = frontline || _threat[f] >= 0.5f;
                    _need[f] = GuardFor(_threat[f]);
                }
                else
                {
                    _target[f] = true;
                    _borders[f] = !holdsAny || !hasAdjacency || BordersSide(f, team, flags, adjacency);
                    _need[f] = ForceFor(_threat[f]);
                }
            }
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

        // ------------------------------------------------------------------ the auction

        /// <summary>
        /// Settles the best squad-and-job pair of all, counts the squad toward its flag, re-prices
        /// that flag for everyone still free, and repeats until every squad has a job. A side goes
        /// for at most <see cref="TacticsProfile.MaxObjectives"/> flags at once and puts at most a
        /// share of itself on posts; a squad with no job left holds the nearest flag of its own.
        /// </summary>
        private void Auction(
            int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<SquadInfo> squads,
            int squadCount, Span<SquadOrder> orders, int bots, TeamPosture posture)
        {
            float share = posture == TeamPosture.Aggressive ? _profile.MaxDefendShareAggressive
                : posture == TeamPosture.Defensive ? _profile.MaxDefendShareDefensive
                : _profile.MaxDefendShareBalanced;
            bool anyTarget = false;
            for (int f = 0; f < flagCount; f++) anyTarget |= _target[f];

            // With nothing left to take, every bot holds what the side has; otherwise a share of it.
            float postCap = !anyTarget ? bots : bots < _profile.MinBotsToDefend ? 0f : bots * share;
            float garrison = posture == TeamPosture.Aggressive ? _profile.GarrisonAggressive
                : posture == TeamPosture.Defensive ? _profile.GarrisonDefensive
                : _profile.GarrisonBalanced;

            for (int i = 0; i < squadCount; i++)
                for (int f = 0; f < flagCount; f++)
                    _utility[i, f] = Utility(squads[i], f, flags, garrison);

            int targets = 0;
            int onPosts = 0;
            for (int lot = 0; lot < squadCount; lot++)
            {
                int bestSquad = -1;
                int bestFlag = -1;
                float best = float.NegativeInfinity;
                for (int i = 0; i < squadCount; i++)
                {
                    if (_assigned[i]) continue;
                    for (int f = 0; f < flagCount; f++)
                    {
                        if (_target[f])
                        {
                            // Taking back a flag being lost is never held back by the limit on objectives.
                            if (!_chosen[f] && !_retake[f] && targets >= _profile.MaxObjectives) continue;
                        }
                        else if (_post[f])
                        {
                            if (squads[i].InVehicle || onPosts + squads[i].Size > postCap) continue;
                        }
                        else
                        {
                            continue;
                        }

                        // Strictly greater: on a tie the lower squad and the lower flag win, so a plan
                        // is the same for the same input.
                        if (_utility[i, f] > best)
                        {
                            best = _utility[i, f];
                            bestSquad = i;
                            bestFlag = f;
                        }
                    }
                }

                if (bestSquad < 0) break;

                _assigned[bestSquad] = true;
                orders[bestSquad].Flag = bestFlag;
                orders[bestSquad].Role = _target[bestFlag] ? SquadRole.Attack : SquadRole.Defend;
                orders[bestSquad].Sneak = false;

                // A flank already on its way to this flag stays a flank: turning it into a head-on
                // attack every plan would re-path the squad every two seconds (PickFlankers then
                // keeps its side).
                if (_target[bestFlag] && squads[bestSquad].Flag == bestFlag && squads[bestSquad].Role == SquadRole.Flank)
                    orders[bestSquad].Role = SquadRole.Flank;
                _friends[bestFlag] += squads[bestSquad].Size;
                if (_target[bestFlag])
                {
                    if (!_chosen[bestFlag] && !_retake[bestFlag]) targets++;
                    _chosen[bestFlag] = true;
                }
                else
                {
                    onPosts += squads[bestSquad].Size;
                }

                for (int i = 0; i < squadCount; i++)
                    if (!_assigned[i]) _utility[i, bestFlag] = Utility(squads[i], bestFlag, flags, garrison);
            }

            // No job left for it: hold the nearest flag of the side's own. Never in a vehicle, which
            // does not dig in; it keeps the original squads' own behaviour instead.
            for (int i = 0; i < squadCount; i++)
            {
                if (_assigned[i] || squads[i].InVehicle) continue;
                int held = NearestHeldIndex(squads[i].Position, team, flags, flagCount);
                if (held < 0) continue;

                orders[i].Role = SquadRole.Defend;
                orders[i].Flag = held;
                _assigned[i] = true;
                onPosts += squads[i].Size;
            }

            LastObjectives = targets;
            LastDefenders = onPosts;
        }

        /// <summary>
        /// What <paramref name="squad"/> doing flag <paramref name="f"/>'s job is worth, given the
        /// force already settled on it. Distance is always a cost; the rest depends on the job.
        /// </summary>
        private float Utility(in SquadInfo squad, int f, ReadOnlySpan<FlagInfo> flags, float garrison)
        {
            float need = Math.Max(1f, _need[f]);
            float have = _friends[f];
            float fills = Math.Min(squad.Size, Math.Max(0f, need - have));
            float over = Math.Max(0f, have + squad.Size - need);

            float value;
            if (_target[f])
            {
                value = _profile.TargetBase
                        + (flags[f].Owner < 0 ? _profile.NeutralBonus : 0f)
                        + (_retake[f] ? _profile.RetakeBonus : 0f)
                        + _profile.LinkWeight * _enemyNeighbours[f]
                        - _profile.ThreatWeight * _threat[f]
                        - (_borders[f] ? 0f : _profile.DeepPenalty);

                // Still outnumbered there, even with this squad: Lanchester's deficit, as a share of the need.
                float deficit = Math.Max(0f, need - have - squad.Size);
                value -= _profile.DeficitWeight * deficit / need;

                // A base the side does not hold (phase P32): its vehicles and its safe respawn go with it.
                if (flags[f].IsBase) value += _profile.EnemyBaseBonus;
            }
            else
            {
                value = garrison + _profile.DefendPerThreat * _threat[f];
            }

            value += _profile.ShortWeight * fills / need - _profile.OverWeight * over / need;
            float distanceCost = _profile.DistanceWeight * Vec3.Distance(squad.Position, flags[f].Position) / 100f;
            value -= squad.InVehicle ? distanceCost * _profile.VehicleDistanceShare : distanceCost;

            // Sticky on purpose: a squad keeps its job until another is clearly better, so it commits.
            if (squad.Role != SquadRole.None && squad.Flag == f) value += _profile.Stickiness;

            return value;
        }

        // ------------------------------------------------------------------ gathering

        /// <summary>
        /// An assault on a flag the enemy holds waits short of it until enough of it has arrived --
        /// <see cref="TacticsProfile.GatherShare"/> of the force the flag costs -- or
        /// <see cref="TacticsProfile.GatherMaxWait"/> has passed, or it is already in the fight; then
        /// every squad on it goes in together, and keeps going until the flag falls or is dropped.
        /// An undefended flag, a vehicle and a squad already fighting never wait.
        /// </summary>
        private void Gather(
            int team, ReadOnlySpan<FlagInfo> flags, int flagCount, ReadOnlySpan<SquadInfo> squads,
            int squadCount, Span<SquadOrder> orders, float time)
        {
            for (int f = 0; f < MaxFlags; f++)
            {
                if (f < flagCount && _chosen[f]) continue;
                _go[f] = false;
                _gatheringSince[f] = float.NaN;
            }

            for (int f = 0; f < flagCount; f++)
            {
                if (!_chosen[f]) continue;

                bool waits = _profile.GatherShare > 0f && !float.IsNaN(time) && _threat[f] >= 0.5f && !_retake[f];
                if (!waits) _go[f] = true;

                Vec3 target = flags[f].Position;
                Vec3 rally = RallyPoint(target, team, flags, flagCount, out float reach);

                if (!_go[f])
                {
                    int there = 0;
                    bool fighting = false;
                    for (int i = 0; i < squadCount; i++)
                    {
                        if (!IsAssault(orders[i].Role) || orders[i].Flag != f || squads[i].InVehicle) continue;

                        float toTarget = Vec3.Distance(squads[i].Position, target);
                        if (Vec3.Distance(squads[i].Position, rally) <= RallyRadius || toTarget <= reach) there += squads[i].Size;
                        if (squads[i].Engaged && toTarget <= reach + ContactSlack) fighting = true;
                    }

                    if (float.IsNaN(_gatheringSince[f])) _gatheringSince[f] = time;
                    if (there >= _profile.GatherShare * _need[f] || fighting || time - _gatheringSince[f] >= _profile.GatherMaxWait)
                        _go[f] = true;
                }

                for (int i = 0; i < squadCount; i++)
                {
                    if (!IsAssault(orders[i].Role) || orders[i].Flag != f) continue;

                    if (_go[f] || squads[i].InVehicle || squads[i].Engaged)
                    {
                        if (orders[i].Role == SquadRole.Assemble)
                        {
                            orders[i].Role = SquadRole.Attack;
                            orders[i].HasWaypoint = false;
                        }
                        continue;
                    }

                    orders[i].Role = SquadRole.Assemble;
                    orders[i].Waypoint = rally;
                    orders[i].HasWaypoint = true;
                    orders[i].Sneak = false;
                    LastGathering++;
                }
            }
        }

        private static bool IsAssault(SquadRole role)
            => role == SquadRole.Attack || role == SquadRole.Flank || role == SquadRole.Assemble;

        /// <summary>
        /// Where an assault on <paramref name="target"/> gathers: toward the side's nearest flag from
        /// it, <see cref="TacticsProfile.GatherDistance"/> out, but never past seven tenths of the way
        /// home. <paramref name="reach"/> is how far out that is.
        /// </summary>
        private Vec3 RallyPoint(in Vec3 target, int team, ReadOnlySpan<FlagInfo> flags, int flagCount, out float reach)
        {
            Vec3 home = NearestHeld(target, team, flags, flagCount);
            Vec3 toHome = Flat(home - target);
            float length = toHome.Magnitude;
            if (length < 1f)
            {
                reach = 0f;
                return target;
            }

            reach = Math.Min(_profile.GatherDistance, 0.7f * length);
            return target + toHome * (reach / length);
        }

        // ------------------------------------------------------------------ flanks

        /// <summary>
        /// Where two or more squads on foot go for one flag, the second-nearest of them goes round
        /// the side through a waypoint off the attack axis, quietly, while the nearest goes straight
        /// in. Six or more squads on one flag send a second flank round the other side: a pincer.
        /// Squads still gathering are left out: a flank leaves with the assault, not before it.
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
