using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// An abstract round of the game's conquest mode: squads walk between flags, fight when they
    /// meet, respawn at flags their side holds, and score the way the server does -- every death
    /// is worth the killers' flag count, a 200-point lead or the other side holding nothing wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it keeps from the game.</b> The maps' flags, links and capture ranges; capture
    /// arithmetic (CapturePoint's 0.06 per bot of majority per second, a neutral flag falling to
    /// the first side on it); the scoring and both endings (MatchRules); the 70/30 frontline spawn
    /// choice; the original squads' "take the nearest flag that is not safely ours" and its
    /// NewAttackOrder; the commander's cadence, contact rule and the exact TeamPlanner the server
    /// runs, with its orders carried out as Squad.FollowCommand does.
    /// </para>
    /// <para>
    /// <b>What it stands in for.</b> A fight is attrition between squads within 70 m (45 m for a
    /// sneaking one), one kill per bot per 40 s of fighting, halved against a squad dug in facing
    /// the fire. No terrain, no vehicles, no aiming. Deterministic for a seed.
    /// </para>
    /// </remarks>
    public sealed partial class ConquestSim
    {
        private readonly ISidePolicy[] _policies;
        private readonly SimRandom _rng;
        /// <summary>Each side's dead, by time of death.</summary>
        private readonly List<float>[] _dead = { new List<float>(), new List<float>() };
        private readonly int[] _score = new int[2];
        private readonly float[] _flaglessSince = { -1f, -1f };
        private int _nextSquadId = 1;
        private float _nextCapture = 1f;
        private float _nextWave = 1f;

        public ConquestSim(SimMap map, int botsPerTeam, ISidePolicy blue, ISidePolicy red, SimRandom rng)
        {
            Map = map;
            BotsPerTeam = botsPerTeam;
            _policies = new[] { blue, red };
            _rng = rng;

            int flags = map.Flags.Count;
            Owner = new int[flags];
            PendingOwner = new int[flags];
            Control = new float[flags];
            UnsafeUntil = new float[flags];
            for (int f = 0; f < flags; f++)
            {
                Owner[f] = map.Flags[f].StartOwner;
                PendingOwner[f] = Owner[f];
                Control[f] = Owner[f] >= 0 ? 1f : 0f;
            }
            // Everyone starts dead long enough to come in on the first wave, at a second in.
            for (int team = 0; team < 2; team++)
                for (int i = 0; i < botsPerTeam; i++)
                    _dead[team].Add(-SimRules.DeadGraceSeconds);
        }

        public SimMap Map { get; }
        public int BotsPerTeam { get; }
        public float Time { get; private set; }
        public int[] Owner { get; }
        public int[] PendingOwner { get; }
        public float[] Control { get; }
        public float[] UnsafeUntil { get; }
        public List<SimSquad> Squads { get; } = new List<SimSquad>();

        public int Score(int team) => _score[team];

        /// <summary>
        /// Whether a side keeps its squads whole the way the game has since phase P29: a lone bot a
        /// wave brings back reinforces a squad near its flag, and the commander folds lone bots into
        /// squads near them (<see cref="Regroup"/>). The original game did neither; a commander
        /// policy switches it on for its side.
        /// </summary>
        public bool[] SquadUpkeep { get; } = { false, false };

        /// <summary>
        /// How far from its flag a lone arrival looks for a squad to reinforce, per side: the
        /// commander's <see cref="TacticsProfile.ReinforceRadius"/>, as ActorManager.SpawnWave reads it.
        /// </summary>
        public float[] ReinforceRadius { get; } = { 150f, 150f };

        /// <summary>Each side's deaths so far, and its flag-seconds held: for diagnosis.</summary>
        public int[] Deaths { get; } = new int[2];

        public double[] FlagSeconds { get; } = new double[2];

        public int OwnedCount(int team)
        {
            int count = 0;
            for (int f = 0; f < Owner.Length; f++) if (Owner[f] == team) count++;
            return count;
        }

        public bool Safe(int flag) => Time >= UnsafeUntil[flag];

        /// <summary>A flag the side must still take: not its own, or its own with an enemy on it.</summary>
        public bool NeedsTaking(int flag, int team) => Owner[flag] != team || !Safe(flag);

        /// <summary>Plays the round out.</summary>
        public SimResult Run()
        {
            while (true)
            {
                if (Time >= _nextWave)
                {
                    _nextWave += SimRules.SpawnWaveSeconds;
                    Spawn(0);
                    Spawn(1);
                }
                _policies[0].Tick(this, 0);
                _policies[1].Tick(this, 1);
                Behave();
                MergeJoiners();
                Fight();
                if (Time >= _nextCapture)
                {
                    _nextCapture += 1f;
                    Capture();
                }

                FlagSeconds[0] += OwnedCount(0) * SimRules.TickSeconds;
                FlagSeconds[1] += OwnedCount(1) * SimRules.TickSeconds;
                Time += SimRules.TickSeconds;

                int lead = _score[0] - _score[1];
                if (lead >= SimRules.VictoryPoints) return new SimResult(0, _score[0], _score[1], Time, false);
                if (-lead >= SimRules.VictoryPoints) return new SimResult(1, _score[0], _score[1], Time, false);
                for (int team = 0; team < 2; team++)
                {
                    if (OwnedCount(team) > 0) { _flaglessSince[team] = -1f; continue; }
                    if (_flaglessSince[team] < 0f) _flaglessSince[team] = Time;
                    else if (Time - _flaglessSince[team] >= SimRules.EliminationDwellSeconds)
                        return new SimResult(1 - team, _score[0], _score[1], Time, true);
                }
                if (Time >= SimRules.MaxSeconds) return new SimResult(-1, _score[0], _score[1], Time, false);
            }
        }

        // ------------------------------------------------------------------ spawning

        /// <summary>
        /// ActorManager.SpawnWave: every bot dead long enough picks its own flag, and each flag's
        /// arrivals are split into squads of two to four.
        /// </summary>
        private void Spawn(int team)
        {
            List<float> dead = _dead[team];
            if (dead.Count == 0 || OwnedCount(team) == 0) return;

            var arrivals = new int[Owner.Length];
            int ready = 0;
            for (int i = dead.Count - 1; i >= 0; i--)
            {
                if (dead[i] + SimRules.DeadGraceSeconds > Time) continue;
                dead.RemoveAt(i);
                arrivals[SpawnFlag(team)]++;
                ready++;
            }
            if (ready == 0) return;

            for (int f = 0; f < arrivals.Length; f++)
            {
                SimFlag at = Map.Flags[f];
                int left = arrivals[f];
                while (left > 0)
                {
                    int size = Math.Min(left, SimRules.MinSquadSize + _rng.Next(SimRules.MaxSquadSize - SimRules.MinSquadSize + 1));
                    left -= size;
                    var squad = new SimSquad
                    {
                        Id = _nextSquadId++,
                        Team = team,
                        X = at.X + _rng.Range(-8f, 8f),
                        Z = at.Z + _rng.Range(-8f, 8f),
                        Size = size,
                    };
                    squad.TargetX = squad.X;
                    squad.TargetZ = squad.Z;
                    if (size == 1 && SquadUpkeep[team]) squad.JoinId = SquadWithRoomNear(team, squad.X, squad.Z, ReinforceRadius[team], squad.Id);
                    Squads.Add(squad);
                }
            }
        }

        /// <summary>
        /// The id of the squad of <paramref name="team"/> nearest (<paramref name="x"/>, <paramref name="z"/>)
        /// within <paramref name="radius"/> with room for one more, not itself joining one; -1 for none.
        /// </summary>
        private int SquadWithRoomNear(int team, float x, float z, float radius, int except)
        {
            int best = -1;
            float bestD = radius * radius;
            foreach (SimSquad s in Squads)
            {
                if (s.Team != team || s.Id == except || s.JoinId >= 0 || s.Size + 1 > SquadRegroup.MaxSize) continue;
                float dx = s.X - x, dz = s.Z - z;
                float d = dx * dx + dz * dz;
                if (d <= bestD)
                {
                    bestD = d;
                    best = s.Id;
                }
            }
            return best;
        }

        /// <summary>
        /// The commander's squad upkeep for <paramref name="team"/>, as BotCommander.Regroup does it
        /// before every plan: lone squads are told to walk over and join the squad
        /// <paramref name="regroup"/> picks for them.
        /// </summary>
        public void Regroup(int team, SquadRegroup regroup, float joinRadius = SquadRegroup.DefaultJoinRadius)
        {
            _regroupSquads.Clear();
            foreach (SimSquad s in Squads)
                if (s.Team == team && s.JoinId < 0 && _regroupSquads.Count < TeamPlanner.MaxSquads) _regroupSquads.Add(s);

            int count = _regroupSquads.Count;
            if (count == 0) return;
            for (int i = 0; i < count; i++)
            {
                SimSquad s = _regroupSquads[i];
                _regroupInfo[i] = new SquadInfo
                {
                    Id = s.Id,
                    Position = new Ironfront.Net.Replication.Movement.Vec3(s.X, 0f, s.Z),
                    Size = s.Size,
                    Engaged = s.Engaged,
                    Role = s.Role,
                    Flag = s.CommandFlag,
                };
            }
            int merges = regroup.Plan(new ReadOnlySpan<SquadInfo>(_regroupInfo, 0, count), _regroupMerges, joinRadius);
            for (int m = 0; m < merges; m++)
                _regroupSquads[_regroupMerges[m].From].JoinId = _regroupSquads[_regroupMerges[m].Into].Id;
        }

        private readonly List<SimSquad> _regroupSquads = new List<SimSquad>();
        private readonly SquadInfo[] _regroupInfo = new SquadInfo[TeamPlanner.MaxSquads];
        private readonly SquadMerge[] _regroupMerges = new SquadMerge[TeamPlanner.MaxSquads];

        /// <summary>AiActorController.SelectedSpawnPoint: 70% a frontline flag, 30% any flag held.</summary>
        private int SpawnFlag(int team)
        {
            var owned = new List<int>();
            var frontline = new List<int>();
            for (int f = 0; f < Owner.Length; f++)
            {
                if (Owner[f] != team) continue;
                owned.Add(f);
                foreach (int n in Map.Neighbours[f])
                {
                    if (Owner[n] != team) { frontline.Add(f); break; }
                }
            }
            if (frontline.Count > 0 && _rng.NextDouble() < SimRules.FrontlineSpawnShare)
                return frontline[_rng.Next(frontline.Count)];
            return owned[_rng.Next(owned.Count)];
        }

        // ------------------------------------------------------------------ capture

        private void Capture()
        {
            for (int f = 0; f < Owner.Length; f++)
            {
                SimFlag flag = Map.Flags[f];
                float range = flag.CaptureRange * flag.CaptureRange;
                int blue = 0, red = 0;
                foreach (SimSquad s in Squads)
                {
                    float dx = s.X - flag.X, dz = s.Z - flag.Z;
                    if (dx * dx + dz * dz > range) continue;
                    if (s.Team == 0) blue += s.Size; else red += s.Size;
                }
                if (blue == 0 && red == 0) continue;
                if ((Owner[f] != 0 && blue > 0) || (Owner[f] != 1 && red > 0)) UnsafeUntil[f] = Time + SimRules.UnsafeSeconds;
                if (blue == red) continue;

                int majority = blue > red ? 0 : 1;
                int lead = Math.Abs(blue - red);
                if (majority != PendingOwner[f])
                {
                    Control[f] -= lead * SimRules.CaptureSpeed;
                    if (Control[f] <= 0f)
                    {
                        Owner[f] = majority;
                        PendingOwner[f] = majority;
                        Control[f] = 0.01f;
                    }
                }
                else
                {
                    Control[f] = Math.Min(1f, Control[f] + lead * SimRules.CaptureSpeed);
                }
            }
        }

        // ------------------------------------------------------------------ deaths

        private void Bury(SimSquad squad, int deaths)
        {
            int killers = 1 - squad.Team;
            Deaths[squad.Team] += deaths;
            _score[killers] += deaths * OwnedCount(killers);
            for (int i = 0; i < deaths; i++) _dead[squad.Team].Add(Time);
        }

        internal float Jitter(float radius) => _rng.Range(-radius, radius);

        internal SimRandom Random => _rng;
    }
}
