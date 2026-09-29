using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// The original game: no commander. Every squad keeps role None and does what the original
    /// squads do (<see cref="ConquestSim"/>'s Original: the nearest flag not safely its own, else
    /// NewAttackOrder). The baseline the commander is trained against.
    /// </summary>
    public sealed class OriginalPolicy : ISidePolicy
    {
        public void Tick(ConquestSim sim, int team)
        {
        }
    }

    /// <summary>
    /// A side under the team commander: the server's own <see cref="TeamPlanner"/>, fed the way
    /// <c>BotCommander</c> feeds it and on its cadence, its orders applied as they come.
    /// </summary>
    public sealed class CommanderPolicy : ISidePolicy
    {
        private readonly TeamPlanner _planner;
        private readonly float _divertRange;
        private readonly List<SimSquad> _mine = new List<SimSquad>();
        private FlagInfo[] _flags = Array.Empty<FlagInfo>();
        private int[] _adjacency = Array.Empty<int>();
        private SquadInfo[] _info = new SquadInfo[TeamPlanner.MaxSquads];
        private SquadOrder[] _orders = new SquadOrder[TeamPlanner.MaxSquads];
        private float _nextPlan = -1f;

        public CommanderPolicy(TacticsProfile profile)
        {
            _planner = new TeamPlanner(profile);
            _divertRange = profile.AttackDivertRange;
        }

        /// <summary>Where each plan is written while <see cref="LogUntil"/>, for reading one round.</summary>
        public Action<string>? Log { get; set; }

        public float LogUntil { get; set; }

        public void Tick(ConquestSim sim, int team)
        {
            if (_nextPlan < 0f)
            {
                sim.DivertRange[team] = _divertRange;
                BuildFlags(sim.Map);
                // BotCommander: the first plan 3 s in, then the sides alternate once a second.
                _nextPlan = 3f + team;
            }
            if (sim.Time < _nextPlan)
            {
                return;
            }
            _nextPlan += SimRules.PlanPeriod;

            _mine.Clear();
            foreach (SimSquad s in sim.Squads)
            {
                if (s.Team == team && _mine.Count < TeamPlanner.MaxSquads) _mine.Add(s);
            }
            if (_mine.Count == 0)
            {
                return;
            }

            ReadFlags(sim, team);
            for (int i = 0; i < _mine.Count; i++)
            {
                SimSquad s = _mine[i];
                _info[i] = new SquadInfo
                {
                    Id = s.Id,
                    Position = new Vec3(s.X, 0f, s.Z),
                    Size = s.Size,
                    InVehicle = false,
                    Engaged = s.Engaged,
                    Role = s.Role,
                    Flag = s.CommandFlag,
                };
            }

            int written = _planner.Plan(
                team, _flags, _adjacency,
                new ReadOnlySpan<SquadInfo>(_info, 0, _mine.Count),
                sim.Score(team), sim.Score(1 - team),
                new Span<SquadOrder>(_orders, 0, _mine.Count));

            if (Log != null && sim.Time < LogUntil)
            {
                var line = new System.Text.StringBuilder();
                line.Append(System.FormattableString.Invariant($"t={sim.Time:0} team {team} {_planner.LastPosture} obj {_planner.LastObjectives} def {_planner.LastDefenders}:"));
                for (int i = 0; i < written; i++)
                {
                    SimSquad s = _mine[_orders[i].SquadIndex];
                    line.Append(System.FormattableString.Invariant($" [{s.Id}x{s.Size} {s.Role}/{s.CommandFlag}->{_orders[i].Role}/{_orders[i].Flag}{(_orders[i].Changed ? "*" : "")} @{s.X:0},{s.Z:0}]"));
                }
                Log(line.ToString());
            }
            for (int i = 0; i < written; i++)
            {
                SquadOrder order = _orders[i];
                if (!order.Changed) continue;
                SimSquad s = _mine[order.SquadIndex];
                s.Role = order.Flag >= 0 ? order.Role : SquadRole.None;
                s.CommandFlag = order.Flag;
                s.HasWaypoint = order.HasWaypoint;
                s.WaypointX = order.Waypoint.X;
                s.WaypointZ = order.Waypoint.Z;
                s.WaypointReached = !order.HasWaypoint;
                s.Sneak = order.Sneak && order.Role == SquadRole.Flank;
                s.DugIn = false;
            }
        }

        private void BuildFlags(SimMap map)
        {
            int count = map.Flags.Count;
            _flags = new FlagInfo[count];
            var adjacency = new List<int>();
            for (int f = 0; f < count; f++)
            {
                SimFlag flag = map.Flags[f];
                _flags[f].Position = new Vec3(flag.X, 0f, flag.Z);
                _flags[f].Capturable = true;
                _flags[f].AdjacencyStart = adjacency.Count;
                adjacency.AddRange(map.Neighbours[f]);
                _flags[f].AdjacencyCount = map.Neighbours[f].Count;
            }
            _adjacency = adjacency.ToArray();
        }

        /// <summary>Owners, and the enemies the side is in contact with round each flag (BotCommander.ReadFlags).</summary>
        private void ReadFlags(ConquestSim sim, int team)
        {
            for (int f = 0; f < _flags.Length; f++)
            {
                _flags[f].Owner = sim.Owner[f];
                _flags[f].EnemiesInContact = 0;
            }
            float threat = SimRules.ThreatRadius * SimRules.ThreatRadius;
            float contact = SimRules.ContactRadius * SimRules.ContactRadius;
            foreach (SimSquad enemy in sim.Squads)
            {
                if (enemy.Team == team) continue;
                bool known = false;
                foreach (SimSquad own in _mine)
                {
                    float dx = own.X - enemy.X, dz = own.Z - enemy.Z;
                    if (dx * dx + dz * dz < contact) { known = true; break; }
                }
                for (int f = 0; f < _flags.Length; f++)
                {
                    float dx = _flags[f].Position.X - enemy.X, dz = _flags[f].Position.Z - enemy.Z;
                    float d2 = dx * dx + dz * dz;
                    // On the flag itself it is known regardless: a contested flag shows on every
                    // player's map (BotCommander.ReadFlags).
                    float range = sim.Map.Flags[f].CaptureRange;
                    if ((known && d2 < threat) || d2 < range * range) _flags[f].EnemiesInContact += enemy.Size;
                }
            }
        }
    }
}
