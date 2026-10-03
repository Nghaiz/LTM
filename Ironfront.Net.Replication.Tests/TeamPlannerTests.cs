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

            public int Add(string name, float x, float z, int owner, bool capturable = true, bool isBase = false)
            {
                Flags.Add(new FlagInfo { Position = new Vec3(x, 0f, z), Owner = owner, Capturable = capturable, IsBase = isBase });
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
            var planner = new TeamPlanner(new TacticsProfile());

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
            var planner = new TeamPlanner(new TacticsProfile());

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
            var planner = new TeamPlanner(new TacticsProfile());

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
            var planner = new TeamPlanner(new TacticsProfile());

            var squads = new SquadInfo[10];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, 0f, 150f, 4);

            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            int bots = 40;
            int cap = (int)Math.Ceiling(bots * Math.Max(
                new TacticsProfile().MaxDefendShareBalanced, new TacticsProfile().MaxDefendShareAggressive));
            Assert.InRange(planner.LastDefenders, 1, cap + 4);   // the last squad sent may overshoot by its own size
            Assert.Contains(orders, o => o.Role == SquadRole.Attack || o.Role == SquadRole.Flank);
        }

        [Fact]
        public void AVehicleSquad_AttacksAndNeverDefends()
        {
            Map map = TwoLanes(out int a, out _, out _, out _, out _);
            map.Threat(a, 5);
            var planner = new TeamPlanner(new TacticsProfile());

            var squads = new[] { Squad(1, -150f, 150f, 4, vehicle: true), Squad(2, 0f, 0f, 4), Squad(3, 0f, 0f, 4) };
            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            Assert.NotEqual(SquadRole.Defend, orders[0].Role);
        }

        [Fact]
        public void EvenOnFlags_TheSideIsBalanced_BehindAggressive_AheadOnBothDefensive()
        {
            var planner = new TeamPlanner(new TacticsProfile());

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
            var planner = new TeamPlanner(new TacticsProfile());
            Plan(planner, Blue, map, new[] { Squad(1, 0f, 50f) }, score: 10, enemyScore: 100);
            Assert.Equal(TeamPosture.Aggressive, planner.LastPosture);
        }

        [Fact]
        public void ASquadKeepsItsTarget_UntilItFallsOrSomethingClearlyBetterAppears()
        {
            Map map = TwoLanes(out _, out _, out int c, out int d, out _);
            var planner = new TeamPlanner(new TacticsProfile());

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

            // Nobody kept at home and no cost for crowding, so both squads go for B and the flank is
            // the only split.
            var planner = new TeamPlanner(new TacticsProfile { GarrisonBalanced = 0f, MinBotsToDefend = 100, OverWeight = 0f });

            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 160f, 5), Squad(2, 10f, 150f, 5) });

            SquadOrder flank = Array.Find(orders, o => o.Role == SquadRole.Flank);
            Assert.Equal(b, flank.Flag);
            Assert.True(flank.Sneak);
            Assert.True(flank.HasWaypoint);

            // Off the line the attack comes along, by the profile's offset, and short of the flag.
            Assert.InRange(Math.Abs(flank.Waypoint.X), new TacticsProfile().FlankOffset - 1f,
                           new TacticsProfile().FlankOffset + 1f);
            Assert.True(flank.Waypoint.Z < 300f);

            Assert.Contains(orders, o => o.Role == SquadRole.Attack && o.Flag == b);
        }

        /// <summary>A big push on one flag sends a flank round each side: a pincer.</summary>
        [Fact]
        public void ABigPushOnOneFlag_FlanksRoundBothSides()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            var planner = new TeamPlanner(new TacticsProfile { GarrisonBalanced = 0f, MinBotsToDefend = 100, OverWeight = 0f });

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
        public void AFlankUnderWay_StaysAFlank_PlanAfterPlan()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            var planner = new TeamPlanner(new TacticsProfile { GarrisonBalanced = 0f, MinBotsToDefend = 100, OverWeight = 0f });
            var squads = new[] { Squad(1, 0f, 160f, 5), Squad(2, 10f, 150f, 5) };

            SquadOrder[] first = Plan(planner, Blue, map, squads);
            int flanker = Array.FindIndex(first, o => o.Role == SquadRole.Flank);
            Assert.True(flanker >= 0);

            // The squads report back what they were told, and the flank has got round nearer the flag
            // than the head-on squad: it must stay the flank, not swap jobs with it.
            for (int i = 0; i < squads.Length; i++)
            {
                bool flanking = i == flanker;
                squads[i] = Squad(squads[i].Id, flanking ? 45f : 0f, flanking ? 280f : 200f, 5, first[i].Role, first[i].Flag);
            }
            SquadOrder[] second = Plan(planner, Blue, map, squads);

            Assert.Equal((SquadRole.Flank, b), (second[flanker].Role, second[flanker].Flag));
            Assert.All(second, o => Assert.False(o.Changed));
        }

        [Fact]
        public void AFlankGoesRoundTheSideTheSquadAlreadyStandsOn()
        {
            var planner = new TeamPlanner(new TacticsProfile());
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
            var planner = new TeamPlanner(new TacticsProfile());

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

            var planner = new TeamPlanner(new TacticsProfile());
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

            var planner = new TeamPlanner(new TacticsProfile());
            SquadOrder[] orders = Plan(planner, Blue, map, new[] { Squad(1, 0f, 20f), Squad(2, 0f, 30f) });

            Assert.All(orders, o => Assert.Equal(SquadRole.Defend, o.Role));
            Assert.All(orders, o => Assert.Equal(a, o.Flag));
        }

        [Fact]
        public void ADefence_DigsInAheadOfTheFlag_TowardTheThreat()
        {
            Map map = Line(out _, out int a, out _, out _, out _);
            map.Threat(a, 4);
            var planner = new TeamPlanner(new TacticsProfile());

            var squads = new SquadInfo[4];
            for (int i = 0; i < squads.Length; i++) squads[i] = Squad(i + 1, 0f, 150f, 3);
            SquadOrder[] orders = Plan(planner, Blue, map, squads);

            SquadOrder defence = Array.Find(orders, o => o.Role == SquadRole.Defend);
            Assert.Equal(a, defence.Flag);
            Assert.True(defence.HasWaypoint);
            Assert.True(defence.Waypoint.Z > 150f, "the threat lies up the line, at B");
        }

        /// <summary>
        /// Part 4's simulator found the first assignment sending squads the length of the map: it
        /// handed out objectives in list order, so the first squad went to whichever flag was most
        /// short even with another objective at its feet.
        /// </summary>
        [Fact]
        public void EachSquad_TakesTheObjectiveAtItsFeet_NotTheOneAcrossTheMap()
        {
            Map map = TwoLanes(out _, out _, out int c, out int d, out _);
            map.Threat(c, 2);    // C wants more of the side than D does
            var planner = new TeamPlanner(new TacticsProfile { BotsPerObjective = 4f, MinBotsToDefend = 100, MinBotsToFlank = 100 });

            SquadOrder[] orders = Plan(planner, Blue, map, new[]
            {
                Squad(1, 150f, 300f),    // at D's feet, listed first
                Squad(2, -150f, 300f),   // at C's feet
            });

            Assert.Equal((SquadRole.Attack, d), (orders[0].Role, orders[0].Flag));
            Assert.Equal((SquadRole.Attack, c), (orders[1].Role, orders[1].Flag));
        }

        // ------------------------------------------------------------------ phase P29

        [Fact]
        public void TheForceAFlagCosts_FollowsLanchester()
        {
            // k = 2, n = 2: a defender is worth sqrt(2) attackers; a quarter more for the margin.
            var planner = new TeamPlanner(new TacticsProfile { DefenderAdvantage = 2f, AttritionOrder = 2f, ForceMargin = 0.25f, BotsPerObjective = 2f });

            Assert.Equal(2f, planner.ForceFor(0f));                                                 // an empty flag: the floor
            Assert.Equal((float)Math.Ceiling(4 * Math.Sqrt(2) * 1.25), planner.ForceFor(4f));      // 8
            Assert.True(planner.ForceFor(8f) > planner.ForceFor(4f));

            // The linear law (n = 1) makes the same defenders cost more: numbers count once, not twice.
            var linear = new TeamPlanner(new TacticsProfile { DefenderAdvantage = 2f, AttritionOrder = 1f, ForceMargin = 0.25f });
            Assert.True(linear.ForceFor(4f) > planner.ForceFor(4f));
        }

        [Fact]
        public void HoldingAFlag_TakesFewerThanTheAttackers()
        {
            var planner = new TeamPlanner(new TacticsProfile { DefenderAdvantage = 2f, AttritionOrder = 2f, ForceMargin = 0.25f });

            Assert.Equal(1f, planner.GuardFor(0f));                                                // a quiet flag: one sentry
            Assert.True(planner.GuardFor(4f) < planner.ForceFor(4f));                              // 4 against 8

            // With no margin, three dug in hold four attackers: 4 / sqrt(2) = 2.83.
            var exact = new TeamPlanner(new TacticsProfile { DefenderAdvantage = 2f, AttritionOrder = 2f, ForceMargin = 0f });
            Assert.Equal(3f, exact.GuardFor(4f));
        }

        [Fact]
        public void TheThreatIsRemembered_AndFades()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            var planner = new TeamPlanner(new TacticsProfile { ThreatMemorySeconds = 20f });
            var squads = new[] { Squad(1, 0f, 150f) };
            var orders = new SquadOrder[1];

            flags[b].EnemiesInContact = 4;
            planner.Plan(Blue, flags, adjacency, squads, 0, 0, orders, time: 0f);
            Assert.Equal(4f, planner.RememberedThreat(b));

            flags[b].EnemiesInContact = 0;
            planner.Plan(Blue, flags, adjacency, squads, 0, 0, orders, time: 20f);
            Assert.InRange(planner.RememberedThreat(b), 4f / MathF.E - 0.01f, 4f / MathF.E + 0.01f);

            planner.Plan(Blue, flags, adjacency, squads, 0, 0, orders, time: 200f);
            Assert.True(planner.RememberedThreat(b) < 0.01f);
        }

        [Fact]
        public void WithoutAClock_NothingIsRemembered()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            var planner = new TeamPlanner(new TacticsProfile { ThreatMemorySeconds = 60f });
            var squads = new[] { Squad(1, 0f, 150f) };
            var orders = new SquadOrder[1];

            flags[b].EnemiesInContact = 4;
            planner.Plan(Blue, flags, adjacency, squads, 0, 0, orders);
            flags[b].EnemiesInContact = 0;
            planner.Plan(Blue, flags, adjacency, squads, 0, 0, orders);

            Assert.Equal(0f, planner.RememberedThreat(b));
        }

        /// <summary>Blue holds A; B, up the line, is held by three enemies.</summary>
        private static (FlagInfo[] Flags, int[] Adjacency, int B) DefendedB()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            flags[b].EnemiesInContact = 3;
            return (flags, adjacency, b);
        }

        private static TacticsProfile Gathering() => new TacticsProfile
        {
            GatherShare = 0.8f, GatherDistance = 80f, GatherMaxWait = 30f, MinBotsToDefend = 100, MinBotsToFlank = 100,
        };

        [Fact]
        public void AnAssaultOnADefendedFlag_GathersFirst_ThenGoesInTogether()
        {
            (FlagInfo[] flags, int[] adjacency, int b) = DefendedB();
            var planner = new TeamPlanner(Gathering());
            var orders = new SquadOrder[3];

            // Three squads still at A, 150 m short of B: the assault gathers, 80 m out toward home.
            var atHome = new[] { Squad(1, 0f, 150f), Squad(2, 5f, 150f), Squad(3, -5f, 150f) };
            planner.Plan(Blue, flags, adjacency, atHome, 0, 0, orders, time: 0f);
            Assert.All(orders, o => Assert.Equal((SquadRole.Assemble, b), (o.Role, o.Flag)));
            Assert.All(orders, o => Assert.InRange(o.Waypoint.Z, 219f, 221f));
            Assert.Equal(3, planner.LastGathering);

            // All of them at the rally point: it goes in, every squad at once.
            var gathered = new[]
            {
                Squad(1, 0f, 220f, role: SquadRole.Assemble, flag: b),
                Squad(2, 5f, 222f, role: SquadRole.Assemble, flag: b),
                Squad(3, -5f, 218f, role: SquadRole.Assemble, flag: b),
            };
            planner.Plan(Blue, flags, adjacency, gathered, 0, 0, orders, time: 10f);
            Assert.All(orders, o => Assert.Equal((SquadRole.Attack, b), (o.Role, o.Flag)));
            Assert.All(orders, o => Assert.True(o.Changed));
        }

        [Fact]
        public void AGatheringAssault_GoesInAnyway_AfterTheLongestWait()
        {
            (FlagInfo[] flags, int[] adjacency, int b) = DefendedB();
            var planner = new TeamPlanner(Gathering());
            var orders = new SquadOrder[1];
            var one = new[] { Squad(1, 0f, 150f) };

            planner.Plan(Blue, flags, adjacency, one, 0, 0, orders, time: 0f);
            Assert.Equal(SquadRole.Assemble, orders[0].Role);

            one[0] = Squad(1, 0f, 150f, role: SquadRole.Assemble, flag: b);
            planner.Plan(Blue, flags, adjacency, one, 0, 0, orders, time: 29f);
            Assert.Equal(SquadRole.Assemble, orders[0].Role);

            planner.Plan(Blue, flags, adjacency, one, 0, 0, orders, time: 31f);
            Assert.Equal(SquadRole.Attack, orders[0].Role);
        }

        [Fact]
        public void AnUndefendedFlag_IsNeverWaitedFor()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            var planner = new TeamPlanner(Gathering());
            var orders = new SquadOrder[2];

            planner.Plan(Blue, flags, adjacency, new[] { Squad(1, 0f, 150f), Squad(2, 5f, 150f) }, 0, 0, orders, time: 0f);

            Assert.All(orders, o => Assert.Equal((SquadRole.Attack, b), (o.Role, o.Flag)));
            Assert.Equal(0, planner.LastGathering);
        }

        [Fact]
        public void AContestedFlagOfOurOwn_IsTakenBack_AtOnce()
        {
            Map map = Line(out _, out int a, out _, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            flags[a].Contested = true;
            flags[a].EnemiesInContact = 2;
            var planner = new TeamPlanner(Gathering());
            var orders = new SquadOrder[1];

            planner.Plan(Blue, flags, adjacency, new[] { Squad(1, 0f, 60f) }, 0, 0, orders, time: 0f);

            // An attack on its own flag, straight in: the enemy is taking it now.
            Assert.Equal((SquadRole.Attack, a), (orders[0].Role, orders[0].Flag));
        }

        [Fact]
        public void AContestedFlagOfOurOwn_IsTakenBack_EvenWithTheObjectivesUsedUp()
        {
            Map map = Line(out _, out int a, out int b, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            flags[a].Contested = true;
            flags[a].EnemiesInContact = 2;
            var planner = new TeamPlanner(new TacticsProfile { MaxObjectives = 1, MinBotsToDefend = 100, MinBotsToFlank = 100 });
            var orders = new SquadOrder[2];

            // The squad at B's feet is settled first and takes the side's one objective; the other,
            // 90 m short of A, still goes back for it.
            planner.Plan(Blue, flags, adjacency, new[] { Squad(1, 0f, 290f), Squad(2, 0f, 60f) }, 0, 0, orders);

            Assert.Equal((SquadRole.Attack, b), (orders[0].Role, orders[0].Flag));
            Assert.Equal((SquadRole.Attack, a), (orders[1].Role, orders[1].Flag));
        }

        [Fact]
        public void AFlagWithTheForceItNeeds_SendsTheNextSquadElsewhere()
        {
            Map map = TwoLanes(out _, out _, out int c, out int d, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            // Nobody on posts; C is a little nearer than D for both squads.
            var planner = new TeamPlanner(new TacticsProfile { MinBotsToDefend = 100, MinBotsToFlank = 100, BotsPerObjective = 4f });
            var squads = new[] { Squad(1, -60f, 200f, 4), Squad(2, -50f, 200f, 4) };
            var orders = new SquadOrder[2];

            planner.Plan(Blue, flags, adjacency, squads, 0, 0, orders);

            Assert.Contains(orders, o => o.Flag == c);
            Assert.Contains(orders, o => o.Flag == d);
        }

        [Fact]
        public void BotsPastTheForceAFlagNeeds_CostTheSquadThatWouldAddThem()
        {
            Map map = TwoLanes(out _, out _, out int c, out int d, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            // Nothing earned for filling a need, so only the cost of crowding can move the second squad.
            var crowding = new TacticsProfile { MinBotsToDefend = 100, MinBotsToFlank = 100, BotsPerObjective = 4f, ShortWeight = 0f, OverWeight = 1f };
            var squads = new[] { Squad(1, -60f, 200f, 4), Squad(2, -50f, 200f, 4) };
            var orders = new SquadOrder[2];

            new TeamPlanner(crowding).Plan(Blue, flags, adjacency, squads, 0, 0, orders);
            Assert.Contains(orders, o => o.Flag == d);

            // Without that cost both take the nearer flag.
            crowding.OverWeight = 0f;
            new TeamPlanner(crowding).Plan(Blue, flags, adjacency, squads, 0, 0, orders);
            Assert.All(orders, o => Assert.Equal(c, o.Flag));
        }

        [Fact]
        public void OutnumberedEverywhere_TheSideStillAttacks()
        {
            Map map = Line(out _, out _, out int b, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            flags[b].EnemiesInContact = 30;
            var planner = new TeamPlanner(new TacticsProfile { MinBotsToDefend = 100 });
            var orders = new SquadOrder[1];

            planner.Plan(Blue, flags, adjacency, new[] { Squad(1, 0f, 150f, 2) }, 0, 0, orders);

            Assert.Equal(SquadRole.Attack, orders[0].Role);
        }

        [Fact]
        public void AnEmptySide_GetsNoOrders()
        {
            Map map = Line(out _, out _, out _, out _, out _);
            (FlagInfo[] flags, int[] adjacency) = map.Build();
            var planner = new TeamPlanner(new TacticsProfile());
            Assert.Equal(0, planner.Plan(Blue, flags, adjacency, ReadOnlySpan<SquadInfo>.Empty, 0, 0, Span<SquadOrder>.Empty));
        }
    
        // ------------------------------------------------------------------ bases (phase P32)

        /// <summary>
        /// Weights that leave only distance, the target base and the base bonus, so a test reads
        /// one rule at a time.
        /// </summary>
        private static TacticsProfile BaseRuleOnly() => new TacticsProfile
        {
            DistanceWeight = 1f, NeutralBonus = 0f, RetakeBonus = 0f, LinkWeight = 0f, ThreatWeight = 0f,
            DeepPenalty = 0f, ShortWeight = 0f, OverWeight = 0f, DeficitWeight = 0f, Stickiness = 0f,
            EnemyBaseBonus = 1.5f, VehicleDistanceShare = 0.35f, MinBotsToFlank = 1000, GatherShare = 0f,
        };

        /// <summary>Forest Lake's shape: both HQs can be taken. Blue holds HQ0 and A; B and HQ1 are red.</summary>
        private static Map CapturableHqs(out int b, out int hq1, float hq1Z, float bX = 0f, float hq1X = 0f)
        {
            var map = new Map();
            int hq0 = map.Add("HQ0", 0f, 0f, Blue, isBase: true);
            int a = map.Add("A", 0f, 200f, Blue);
            b = map.Add("B", bX, 400f, Red);
            hq1 = map.Add("HQ1", hq1X, hq1Z, Red, isBase: true);
            map.Link(hq0, a);
            map.Link(a, b);
            map.Link(a, hq1);
            map.Link(b, hq1);
            return map;
        }

        [Fact]
        public void AnEnemyBase_IsWorthMoreThanAnyOtherFlagAsFarAway()
        {
            Map map = CapturableHqs(out int b, out int hq1, hq1Z: 400f, bX: -200f, hq1X: 200f);
            SquadOrder[] orders = Plan(new TeamPlanner(BaseRuleOnly()), Blue, map, new[] { Squad(1, 0f, 200f) });
            Assert.Equal(hq1, orders[0].Flag);

            TacticsProfile noBonus = BaseRuleOnly();
            noBonus.EnemyBaseBonus = 0f;
            orders = Plan(new TeamPlanner(noBonus), Blue, map, new[] { Squad(1, 0f, 200f) });
            Assert.Equal(b, orders[0].Flag);
        }

        [Fact]
        public void ASquadInAVehicle_RaidsTheEnemyBase_WhileOneOnFootTakesTheFlagInFront()
        {
            // From A: B is 200 m away, HQ1 500 m. On foot B is worth 1 - 2 = -1 and HQ1 1 + 1.5 - 5
            // = -2.5; in a vehicle B is 1 - 0.7 = 0.3 and HQ1 2.5 - 1.75 = 0.75.
            Map map = CapturableHqs(out int b, out int hq1, hq1Z: 700f);

            SquadOrder[] onFoot = Plan(new TeamPlanner(BaseRuleOnly()), Blue, map, new[] { Squad(1, 0f, 200f) });
            Assert.Equal(b, onFoot[0].Flag);

            SquadOrder[] riding = Plan(new TeamPlanner(BaseRuleOnly()), Blue, map, new[] { Squad(1, 0f, 200f, vehicle: true) });
            Assert.Equal(hq1, riding[0].Flag);
            Assert.Equal(SquadRole.Attack, riding[0].Role);
        }

        [Fact]
        public void TheSidesOwnBase_IsNotATargetWhileItHoldsIt()
        {
            Map map = CapturableHqs(out _, out _, hq1Z: 700f);
            SquadOrder[] orders = Plan(new TeamPlanner(BaseRuleOnly()), Blue, map, new[] { Squad(1, 0f, 10f) });
            Assert.NotEqual(0, orders[0].Flag);
        }
    }
}
