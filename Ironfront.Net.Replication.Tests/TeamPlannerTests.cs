using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The team commander (phase P28, owner's report of 2026-09-30): a side that counts its bots and
    /// splits them into attack, defence and flank, commits to its choices, and plays to the score.
    /// </summary>
    public sealed class TeamPlannerTests
    {
        private const int Blue = 0;
        private const int Red = 1;
        private const int Neutral = -1;

        /// <summary>
        /// A small map built from names: "HQ0" belongs to blue and cannot be taken, and so on.
        /// </summary>
        private sealed class Map
        {
            public readonly List<FlagInfo> Flags = new List<FlagInfo>();
            public readonly List<string> Names = new List<string>();
            private readonly List<List<int>> _links = new List<List<int>>();

            public int Add(string name, float x, float z, int owner, bool capturable = true)
            {
                Flags.Add(new FlagInfo { Position = new Vec3(x, 0f, z), Owner = owner, Capturable = capturable });
                Names.Add(name);
                _links.Add(new List<int>());
                return Flags.Count - 1;
            }

            public void Link(int a, int b)
            {
                _links[a].Add(b);
                _links[b].Add(a);
            }

            public void Threat(int flag, int enemies)
            {
                FlagInfo info = Flags[flag];
                info.EnemiesInContact = enemies;
                Flags[flag] = info;
            }

            public (FlagInfo[] Flags, int[] Adjacency) Build()
            {
                var adjacency = new List<int>();
                FlagInfo[] flags = Flags.ToArray();
                for (int f = 0; f < flags.Length; f++)
                {
                    flags[f].AdjacencyStart = adjacency.Count;
                    flags[f].AdjacencyCount = _links[f].Count;
                    adjacency.AddRange(_links[f]);
                }

                return (flags, adjacency.ToArray());
            }
        }

        private static SquadInfo Squad(int id, float x, float z, int size = 4, SquadRole role = SquadRole.None,
                                       int flag = -1, bool vehicle = false, bool engaged = false)
            => new SquadInfo
            {
                Id = id, Position = new Vec3(x, 0f, z), Size = size, Role = role, Flag = flag,
                InVehicle = vehicle, Engaged = engaged,
            };

        private static SquadOrder[] Plan(TeamPlanner planner, int team, Map map, SquadInfo[] squads,
                                         int score = 0, int enemyScore = 0)
        {
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            var orders = new SquadOrder[squads.Length];
            int written = planner.Plan(team, flags, adjacency, squads, score, enemyScore, orders);
            Assert.Equal(squads.Length, written);
            return orders;
        }

        /// <summary>HQ0 - A - B - C - HQ1 in a line, A blue, B neutral, C red.</summary>
        private static Map Line(out int hq0, out int a, out int b, out int c, out int hq1)
        {
            var map = new Map();
            hq0 = map.Add("HQ0", 0f, 0f, Blue, capturable: false);
            a = map.Add("A", 0f, 150f, Blue);
            b = map.Add("B", 0f, 300f, Neutral);
            c = map.Add("C", 0f, 450f, Red);
            hq1 = map.Add("HQ1", 0f, 600f, Red, capturable: false);
            map.Link(hq0, a);
            map.Link(a, b);
            map.Link(b, c);
            map.Link(c, hq1);
            return map;
        }

        /// <summary>
        /// Two lanes: blue holds HQ0, A and B; C and D are neutral; E and F are red. Blue borders C
        /// (by A) and D (by B), and C and D border each other.
        /// </summary>
        private static Map TwoLanes(out int a, out int b, out int c, out int d, out int e)
        {
            var map = new Map();
            int hq0 = map.Add("HQ0", 0f, 0f, Blue, capturable: false);
            a = map.Add("A", -150f, 150f, Blue);
            b = map.Add("B", 150f, 150f, Blue);
            c = map.Add("C", -150f, 350f, Neutral);
            d = map.Add("D", 150f, 350f, Neutral);
            e = map.Add("E", -150f, 550f, Red);
            int f = map.Add("F", 150f, 550f, Red);
            int hq1 = map.Add("HQ1", 0f, 700f, Red, capturable: false);
            map.Link(hq0, a);
            map.Link(hq0, b);
            map.Link(a, c);
            map.Link(b, d);
            map.Link(c, d);
            map.Link(c, e);
            map.Link(d, f);
            map.Link(e, hq1);
            map.Link(f, hq1);
            return map;
        }

        [Fact]
        public void AHandfulOfBots_GoesForOneFlagTogether_AndKeepsNobodyAtHome()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            var planner = new TeamPlanner(TacticsProfile.Default());

            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 20f, 2), Squad(2, 5f, 20f, 2) });

            Assert.Equal(1, planner.LastObjectives);
            Assert.Equal(0, planner.LastDefenders);
            Assert.All(orders, o => Assert.Equal(b, o.Flag));
            Assert.All(orders, o => Assert.True(o.Role == SquadRole.Attack || o.Role == SquadRole.Flank));
        }

        [Fact]
        public void ALargerSide_PursuesMoreFlagsAtOnce()
        {
            Map map = TwoLanes(out _, out _, out int c, out int d, out _);
            var planner = new TeamPlanner(TacticsProfile.Default());

            var squads = new SquadInfo[6];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, 0f, 30f, 4);

            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            Assert.Equal(2, planner.LastObjectives);
            Assert.Contains(orders, o => o.Flag == c && o.Role != SquadRole.Defend);
            Assert.Contains(orders, o => o.Flag == d && o.Role != SquadRole.Defend);
        }

        [Fact]
        public void AThreatenedFrontlineFlag_IsDefended_AndTheBaseNever()
        {
            Map map = TwoLanes(out int a, out _, out _, out _, out _);
            map.Threat(a, 3);
            var planner = new TeamPlanner(TacticsProfile.Default());

            var squads = new SquadInfo[5];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, -150f, 140f + i, 4);

            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            Assert.Contains(orders, o => o.Role == SquadRole.Defend && o.Flag == a);
            Assert.DoesNotContain(orders, o => o.Role == SquadRole.Defend && o.Flag == 0);   // HQ0
            Assert.True(planner.LastDefenders > 0);
        }

        [Fact]
        public void DefenceNeverTakesMoreThanTheCapOfTheSide()
        {
            Map map = TwoLanes(out int a, out int b, out _, out _, out _);
            map.Threat(a, 20);
            map.Threat(b, 20);
            var planner = new TeamPlanner(TacticsProfile.Default());

            var squads = new SquadInfo[10];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, 0f, 150f, 4);

            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            int bots = 40;
            int cap = (int)Math.Ceiling(bots * Math.Max(
                TacticsProfile.Default().MaxDefendShareBalanced, TacticsProfile.Default().MaxDefendShareAggressive));
            Assert.InRange(planner.LastDefenders, 1, cap + 4);   // the last squad sent may overshoot by its own size
            Assert.Contains(orders, o => o.Role == SquadRole.Attack || o.Role == SquadRole.Flank);
        }

        [Fact]
        public void AVehicleSquad_AttacksAndNeverDefends()
        {
            Map map = TwoLanes(out int a, out _, out _, out _, out _);
            map.Threat(a, 5);
            var planner = new TeamPlanner(TacticsProfile.Default());

            var squads = new[] { Squad(1, -150f, 150f, 4, vehicle: true), Squad(2, 0f, 0f, 4), Squad(3, 0f, 0f, 4) };
            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            Assert.NotEqual(SquadRole.Defend, orders[0].Role);
        }

        [Fact]
        public void EvenOnFlags_TheSideIsBalanced_BehindAggressive_AheadOnBothDefensive()
        {
            var planner = new TeamPlanner(TacticsProfile.Default());

            Map even = TwoLanes(out _, out _, out int c, out _, out _);
            Plan(planner, Red, even, new[] { Squad(1, 0f, 650f) });
            Assert.Equal(TeamPosture.Balanced, planner.LastPosture);   // three flags each

            FlagInfo taken = even.Flags[c];
            taken.Owner = Blue;
            even.Flags[c] = taken;

            Plan(planner, Red, even, new[] { Squad(1, 0f, 650f) });
            Assert.Equal(TeamPosture.Aggressive, planner.LastPosture);   // blue four, red three

            Plan(planner, Blue, even, new[] { Squad(1, 0f, 50f) }, score: 120, enemyScore: 20);
            Assert.Equal(TeamPosture.Defensive, planner.LastPosture);
        }

        [Fact]
        public void BehindOnPoints_TheSideIsAggressive()
        {
            Map map = Line(out _, out _, out _, out _, out _);
            var planner = new TeamPlanner(TacticsProfile.Default());
            Plan(planner, Blue, map, new[] { Squad(1, 0f, 50f) }, score: 10, enemyScore: 100);
            Assert.Equal(TeamPosture.Aggressive, planner.LastPosture);
        }

        [Fact]
        public void ASquadKeepsItsTarget_UntilItFallsOrSomethingClearlyBetterAppears()
        {
            Map map = TwoLanes(out _, out _, out int c, out int d, out _);
            var planner = new TeamPlanner(TacticsProfile.Default());

            // One small squad: one objective. It is already on its way to C; D is about as good.
            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 200f, 4, SquadRole.Attack, c) });
            Assert.Equal(c, orders[0].Flag);
            Assert.False(orders[0].Changed);

            // Once C is blue's, the squad moves on.
            FlagInfo taken = map.Flags[c];
            taken.Owner = Blue;
            map.Flags[c] = taken;

            orders = Plan(planner, Blue, map, new[] { Squad(1, -150f, 350f, 4, SquadRole.Attack, c) });
            Assert.NotEqual(c, orders[0].Flag);
            Assert.True(orders[0].Changed);
        }

        [Fact]
        public void TwoSquadsOnOneFlag_OneOfThemFlanks_QuietlyAndOffTheAxis()
        {
            Map map = Line(out _, out _, out int b, out _, out _);

            // Nobody kept at home, so both squads go for B and the flank is the only split.
            var planner = new TeamPlanner(new TacticsProfile { GarrisonBalanced = 0f });

            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 160f, 5), Squad(2, 10f, 150f, 5) });

            SquadOrder flank = Array.Find(orders, o => o.Role == SquadRole.Flank);
            Assert.Equal(b, flank.Flag);
            Assert.True(flank.Sneak);
            Assert.True(flank.HasWaypoint);

            // Off the line the attack comes along, by the profile's offset, and short of the flag.
            Assert.InRange(Math.Abs(flank.Waypoint.X), TacticsProfile.Default().FlankOffset - 1f,
                           TacticsProfile.Default().FlankOffset + 1f);
            Assert.True(flank.Waypoint.Z < 300f);

            Assert.Contains(orders, o => o.Role == SquadRole.Attack && o.Flag == b);
        }

        /// <summary>A big push on one flag sends a flank round each side: a pincer.</summary>
        [Fact]
        public void ABigPushOnOneFlag_FlanksRoundBothSides()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            var planner = new TeamPlanner(new TacticsProfile { GarrisonBalanced = 0f });

            var squads = new SquadInfo[7];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, i * 2f, 150f + i, 4);

            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            SquadOrder[] flanks = Array.FindAll(orders, o => o.Role == SquadRole.Flank);
            Assert.Equal(2, flanks.Length);
            Assert.All(flanks, o => Assert.Equal(b, o.Flag));
            Assert.True(Math.Sign(flanks[0].Waypoint.X) == -Math.Sign(flanks[1].Waypoint.X),
                        "the two flanks go round opposite sides");
            Assert.Equal(5, Array.FindAll(orders, o => o.Role == SquadRole.Attack).Length);
        }

        [Fact]
        public void AFlankGoesRoundTheSideTheSquadAlreadyStandsOn()
        {
            var planner = new TeamPlanner(TacticsProfile.Default());
            var target = new Vec3(0f, 0f, 300f);
            var centre = new Vec3(0f, 0f, 100f);

            Vec3 right = planner.FlankWaypoint(target, centre, Squad(1, 40f, 120f));
            Vec3 left = planner.FlankWaypoint(target, centre, Squad(2, -40f, 120f));

            Assert.True(right.X > 0f);
            Assert.True(left.X < 0f);
        }

        [Fact]
        public void TooSmallASide_NeverSplitsToFlank()
        {
            Map map = Line(out _, out _, out _, out _, out _);
            var planner = new TeamPlanner(TacticsProfile.Default());

            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 160f, 2), Squad(2, 0f, 150f, 2) });
            Assert.DoesNotContain(orders, o => o.Role == SquadRole.Flank);
        }

        [Fact]
        public void ASideWithNoFlags_StillGoesForOne()
        {
            var map = new Map();
            int hq0 = map.Add("HQ0", 0f, 0f, Red, capturable: false);
            int a = map.Add("A", 0f, 100f, Red);
            int b = map.Add("B", 0f, 400f, Red);
            map.Link(hq0, a);
            map.Link(a, b);

            var planner = new TeamPlanner(TacticsProfile.Default());
            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 450f) });

            Assert.Equal(SquadRole.Attack, orders[0].Role);
            Assert.True(orders[0].Flag == a || orders[0].Flag == b);
        }

        [Fact]
        public void WithNothingLeftToTake_EverySquadHoldsTheLine()
        {
            var map = new Map();
            int hq0 = map.Add("HQ0", 0f, 0f, Blue, capturable: false);
            int a = map.Add("A", 0f, 150f, Blue);
            int hq1 = map.Add("HQ1", 0f, 300f, Red, capturable: false);
            map.Link(hq0, a);
            map.Link(a, hq1);

            var planner = new TeamPlanner(TacticsProfile.Default());
            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 20f), Squad(2, 0f, 30f) });

            Assert.All(orders, o => Assert.Equal(SquadRole.Defend, o.Role));
            Assert.All(orders, o => Assert.Equal(a, o.Flag));
        }

        [Fact]
        public void ADefence_DigsInAheadOfTheFlag_TowardTheThreat()
        {
            Map map = Line(out _, out int a, out _, out _, out _);
            map.Threat(a, 4);
            var planner = new TeamPlanner(TacticsProfile.Default());

            var squads = new SquadInfo[4];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, 0f, 150f, 3);
            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            SquadOrder defence = Array.Find(orders, o => o.Role == SquadRole.Defend);
            Assert.Equal(a, defence.Flag);
            Assert.True(defence.HasWaypoint);
            Assert.True(defence.Waypoint.Z > 150f, "the threat lies up the line, at B");
        }

        [Fact]
        public void AnEmptySide_GetsNoOrders()
        {
            Map map = Line(out _, out _, out _, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            var planner = new TeamPlanner(TacticsProfile.Default());
            Assert.Equal(0, planner.Plan(Blue, flags, adjacency, ReadOnlySpan<SquadInfo>.Empty, 0, 0, Span<SquadOrder>.Empty));
        }
    }
}
