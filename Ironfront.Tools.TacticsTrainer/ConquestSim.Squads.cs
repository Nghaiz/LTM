using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    public sealed partial class ConquestSim
    {
        private const float ArriveDistance = 2f;

        /// <summary>Each side's TacticsProfile.AttackDivertRange, set by its commander (Squad.MayDivertTo).</summary>
        public float[] DivertRange { get; } = { SimRules.DivertSlack, SimRules.DivertSlack };

        // ------------------------------------------------------------------ what each squad does

        /// <summary>
        /// Each squad's order tick, then its step. A squad with no order does what the original
        /// squads do; one with an order does what <c>Squad.FollowCommand</c> does with it.
        /// </summary>
        private void Behave()
        {
            foreach (SimSquad s in Squads)
            {
                if (s.JoinId >= 0 && Joining(s))
                {
                    Step(s);
                    continue;
                }

                int closest = ClosestFlag(s.X, s.Z);
                switch (s.Role)
                {
                case SquadRole.Attack:
                    Attack(s, closest);
                    break;
                case SquadRole.Defend:
                    Defend(s);
                    break;
                case SquadRole.Flank:
                    Flank(s, closest);
                    break;
                case SquadRole.Assemble:
                    HoldAt(s);
                    break;
                default:
                    Original(s, closest);
                    break;
                }
                Step(s);
            }
        }

        /// <summary>The original's AiOrders: the nearest flag not safely ours, else NewAttackOrder.</summary>
        private void Original(SimSquad s, int closest)
        {
            if (NeedsTaking(closest, s.Team) && closest != s.Flag)
            {
                Go(s, closest);
            }
            else if (s.Flag < 0 || !NeedsTaking(s.Flag, s.Team))
            {
                NewAttackOrder(s, closest);
            }
        }

        /// <summary>Squad.NewAttackOrder, as written.</summary>
        private void NewAttackOrder(SimSquad s, int closest)
        {
            if (Owner[closest] != s.Team)
            {
                Go(s, closest);
                return;
            }
            var choices = new List<int>();
            foreach (int n in Map.Neighbours[closest])
            {
                if (Owner[n] == s.Team) continue;
                choices.Add(n);
                if (Owner[n] >= 0) choices.Add(n);
            }
            if (choices.Count > 0)
            {
                Go(s, choices[_rng.Next(choices.Count)]);
                return;
            }
            var enemy = new List<int>();
            for (int f = 0; f < Owner.Length; f++) if (Owner[f] == 1 - s.Team) enemy.Add(f);
            if (enemy.Count > 0) Go(s, enemy[_rng.Next(enemy.Count)]);
        }

        /// <summary>An attack: its flag, breaking off only for a flag it is standing on; holds a flag it took.</summary>
        private void Attack(SimSquad s, int closest)
        {
            int goal = s.CommandFlag;
            if (closest != goal && NeedsTaking(closest, s.Team)
                && Distance(s, Map.Flags[closest].X, Map.Flags[closest].Z) < Map.Flags[closest].CaptureRange + DivertRange[s.Team])
            {
                goal = closest;
            }
            if (goal != s.Flag) Go(s, goal);
        }

        /// <summary>A defence: to its spot by the flag, dug in facing the way the enemy comes.</summary>
        private void Defend(SimSquad s) => HoldAt(s);

        /// <summary>
        /// To the order's waypoint -- a defence's spot, an assault's rally point -- and dug in there
        /// (Squad.MoveToAndDigIn).
        /// </summary>
        private void HoldAt(SimSquad s)
        {
            if (s.Flag != s.CommandFlag || !s.HasWaypoint || s.TargetX != s.WaypointX || s.TargetZ != s.WaypointZ)
            {
                s.Flag = s.CommandFlag;
                s.TargetX = s.HasWaypoint ? s.WaypointX : Map.Flags[s.CommandFlag].X;
                s.TargetZ = s.HasWaypoint ? s.WaypointZ : Map.Flags[s.CommandFlag].Z;
                s.DugIn = false;
            }
        }

        /// <summary>A flank: quietly to the side approach, then in at the flag.</summary>
        private void Flank(SimSquad s, int closest)
        {
            if (!s.WaypointReached && s.HasWaypoint)
            {
                if (Distance(s, s.WaypointX, s.WaypointZ) < SimRules.FlankReached)
                {
                    s.WaypointReached = true;
                    s.Sneak = false;
                }
                else
                {
                    s.Flag = s.CommandFlag;
                    s.TargetX = s.WaypointX;
                    s.TargetZ = s.WaypointZ;
                    return;
                }
            }
            Attack(s, closest);
        }

        private void Go(SimSquad s, int flag)
        {
            SimFlag at = Map.Flags[flag];
            float spread = at.CaptureRange * 0.5f;
            s.Flag = flag;
            s.TargetX = at.X + Jitter(spread);
            s.TargetZ = at.Z + Jitter(spread);
            s.DugIn = false;
        }

        /// <summary>Walks toward the target; on arrival digs in facing the nearest enemy-held flag.</summary>
        private void Step(SimSquad s)
        {
            float dx = s.TargetX - s.X, dz = s.TargetZ - s.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance <= ArriveDistance)
            {
                if (!s.DugIn) DigIn(s);
                return;
            }
            s.DugIn = false;
            float speed = s.Engaged ? SimRules.EngagedSpeed : s.Sneak ? SimRules.SneakSpeed : SimRules.WalkSpeed;
            float step = Math.Min(distance, speed * SimRules.TickSeconds);
            s.X += dx / distance * step;
            s.Z += dz / distance * step;
        }

        private void DigIn(SimSquad s)
        {
            s.DugIn = true;
            float fx, fz;
            if (s.Role == SquadRole.Defend && s.Flag >= 0)
            {
                // The defence spot lies toward the threat from the flag: face that way.
                fx = s.X - Map.Flags[s.Flag].X;
                fz = s.Z - Map.Flags[s.Flag].Z;
            }
            else if (s.Role == SquadRole.Assemble && s.Flag >= 0)
            {
                // A rally point faces the flag the assault is about to go for.
                fx = Map.Flags[s.Flag].X - s.X;
                fz = Map.Flags[s.Flag].Z - s.Z;
            }
            else
            {
                int enemy = NearestFlagOwnedBy(1 - s.Team, s.X, s.Z);
                fx = enemy >= 0 ? Map.Flags[enemy].X - s.X : 0f;
                fz = enemy >= 0 ? Map.Flags[enemy].Z - s.Z : 0f;
            }
            float length = MathF.Sqrt(fx * fx + fz * fz);
            s.FaceX = length > 0.01f ? fx / length : 0f;
            s.FaceZ = length > 0.01f ? fz / length : 0f;
        }

        // ------------------------------------------------------------------ joining

        /// <summary>
        /// Walks to where the squad it is joining is going -- AiActorController.JoinedSquad sends a
        /// joiner to its new leader's destination, not after the leader -- and merges once it is
        /// beside the squad; false once that squad is gone.
        /// </summary>
        private bool Joining(SimSquad s)
        {
            SimSquad? target = SquadById(s.JoinId);
            if (target == null)
            {
                s.JoinId = -1;
                return false;
            }
            s.Flag = target.Flag;
            s.TargetX = target.TargetX;
            s.TargetZ = target.TargetZ;
            return true;
        }

        /// <summary>Folds every joiner that has reached its squad into it.</summary>
        private void MergeJoiners()
        {
            bool merged = false;
            foreach (SimSquad s in Squads)
            {
                if (s.JoinId < 0 || s.Size <= 0) continue;
                SimSquad? target = SquadById(s.JoinId);
                if (target == null || target.Size <= 0) continue;
                if (Distance(s, target.X, target.Z) > JoinDistance) continue;
                target.Size += s.Size;
                target.Wounds += s.Wounds;
                s.Size = 0;
                merged = true;
            }
            if (merged) Squads.RemoveAll(q => q.Size <= 0);
        }

        /// <summary>A joiner is in its new squad once this close to it.</summary>
        private const float JoinDistance = 6f;

        private SimSquad? SquadById(int id)
        {
            foreach (SimSquad s in Squads) if (s.Id == id) return s;
            return null;
        }

        // ------------------------------------------------------------------ fighting

        /// <summary>
        /// Every squad fires on the nearest enemy squad it can see; losses land after everyone has
        /// fired, so the order squads are listed in gives nobody the first shot.
        /// </summary>
        private void Fight()
        {
            int count = Squads.Count;
            for (int i = 0; i < count; i++)
            {
                Squads[i].Engaged = false;
                Squads[i].Victim = -1;
            }

            for (int i = 0; i < count; i++)
            {
                SimSquad a = Squads[i];
                float best = float.MaxValue;
                for (int j = 0; j < count; j++)
                {
                    SimSquad b = Squads[j];
                    if (b.Team == a.Team) continue;
                    float range = a.Sneak || b.Sneak ? SimRules.SneakDetectRange : SimRules.EngageRange;
                    float dx = b.X - a.X, dz = b.Z - a.Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 <= range * range && d2 < best)
                    {
                        best = d2;
                        a.Victim = j;
                    }
                }
                a.Engaged = a.Victim >= 0;
            }

            for (int i = 0; i < count; i++)
            {
                SimSquad a = Squads[i];
                if (a.Victim < 0) continue;
                SimSquad b = Squads[a.Victim];
                b.Wounds += a.Size * SimRules.KillsPerBotSecond * SimRules.TickSeconds * CoverAgainst(b, a);
            }

            for (int i = count - 1; i >= 0; i--)
            {
                SimSquad s = Squads[i];
                int deaths = Math.Min(s.Size, (int)s.Wounds);
                if (deaths <= 0) continue;
                s.Wounds -= deaths;
                s.Size -= deaths;
                Bury(s, deaths);
                if (s.Size <= 0) Squads.RemoveAt(i);
            }
        }

        /// <summary>The share of fire from <paramref name="shooter"/> that reaches <paramref name="target"/>.</summary>
        private static float CoverAgainst(SimSquad target, SimSquad shooter)
        {
            if (!target.DugIn) return 1f;
            float dx = shooter.X - target.X, dz = shooter.Z - target.Z;
            float length = MathF.Sqrt(dx * dx + dz * dz);
            if (length < 0.01f) return 1f;
            float facing = (dx * target.FaceX + dz * target.FaceZ) / length;
            return facing >= SimRules.CoverConeCos ? SimRules.CoverFactor : 1f;
        }

        // ------------------------------------------------------------------ geometry

        public int ClosestFlag(float x, float z)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int f = 0; f < Map.Flags.Count; f++)
            {
                float dx = Map.Flags[f].X - x, dz = Map.Flags[f].Z - z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }

        private int NearestFlagOwnedBy(int team, float x, float z)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int f = 0; f < Map.Flags.Count; f++)
            {
                if (Owner[f] != team) continue;
                float dx = Map.Flags[f].X - x, dz = Map.Flags[f].Z - z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }

        private static float Distance(SimSquad s, float x, float z)
        {
            float dx = x - s.X, dz = z - s.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }
    }
}
