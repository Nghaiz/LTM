using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>One tuned weight: its range, whether it is a count, and how to read and write it.</summary>
    public sealed class ProfileParameter
    {
        public ProfileParameter(string name, float min, float max, bool integer,
            Func<TacticsProfile, float> get, Action<TacticsProfile, float> set)
        {
            Name = name;
            Min = min;
            Max = max;
            Integer = integer;
            Get = get;
            Set = set;
        }

        public string Name { get; }
        public float Min { get; }
        public float Max { get; }
        public bool Integer { get; }
        public Func<TacticsProfile, float> Get { get; }
        public Action<TacticsProfile, float> Set { get; }
    }

    /// <summary>
    /// <see cref="TacticsProfile"/> as a point in [0, 1]^n, which is what the evolution strategy
    /// moves. Every weight but <see cref="TacticsProfile.TargetBase"/> -- the scale the others are
    /// measured against -- is here; a test holds the list to the profile's properties both ways.
    /// </summary>
    public static class ProfileVector
    {
        public static readonly IReadOnlyList<ProfileParameter> Parameters = new[]
        {
            P("NeutralBonus", -0.5f, 1.5f, false, p => p.NeutralBonus, (p, v) => p.NeutralBonus = v),
            // Inert in the simulator, which has no capturable HQ and no vehicle (phase P32): a trained
            // profile's values for these two are noise. Keep the hand-set ones when copying a run.
            P("EnemyBaseBonus", 0f, 3f, false, p => p.EnemyBaseBonus, (p, v) => p.EnemyBaseBonus = v),
            P("VehicleDistanceShare", 0.1f, 1f, false, p => p.VehicleDistanceShare, (p, v) => p.VehicleDistanceShare = v),
            P("RetakeBonus", -0.5f, 2f, false, p => p.RetakeBonus, (p, v) => p.RetakeBonus = v),
            P("ThreatWeight", 0f, 0.5f, false, p => p.ThreatWeight, (p, v) => p.ThreatWeight = v),
            P("LinkWeight", 0f, 1f, false, p => p.LinkWeight, (p, v) => p.LinkWeight = v),
            P("DeepPenalty", 0f, 3f, false, p => p.DeepPenalty, (p, v) => p.DeepPenalty = v),
            P("DistanceWeight", 0.05f, 3f, false, p => p.DistanceWeight, (p, v) => p.DistanceWeight = v),
            P("MaxObjectives", 1f, 8f, true, p => p.MaxObjectives, (p, v) => p.MaxObjectives = (int)v),
            P("BotsPerObjective", 1f, 8f, false, p => p.BotsPerObjective, (p, v) => p.BotsPerObjective = v),
            P("AttritionOrder", 1f, 2.5f, false, p => p.AttritionOrder, (p, v) => p.AttritionOrder = v),
            P("DefenderAdvantage", 1f, 4f, false, p => p.DefenderAdvantage, (p, v) => p.DefenderAdvantage = v),
            P("ForceMargin", 0f, 1f, false, p => p.ForceMargin, (p, v) => p.ForceMargin = v),
            P("ShortWeight", 0f, 3f, false, p => p.ShortWeight, (p, v) => p.ShortWeight = v),
            P("OverWeight", 0f, 3f, false, p => p.OverWeight, (p, v) => p.OverWeight = v),
            P("DeficitWeight", 0f, 3f, false, p => p.DeficitWeight, (p, v) => p.DeficitWeight = v),
            P("ThreatMemorySeconds", 0f, 60f, false, p => p.ThreatMemorySeconds, (p, v) => p.ThreatMemorySeconds = v),
            P("MinBotsToDefend", 0f, 20f, true, p => p.MinBotsToDefend, (p, v) => p.MinBotsToDefend = (int)v),
            P("GarrisonBalanced", -1f, 2f, false, p => p.GarrisonBalanced, (p, v) => p.GarrisonBalanced = v),
            P("GarrisonAggressive", -1f, 2f, false, p => p.GarrisonAggressive, (p, v) => p.GarrisonAggressive = v),
            P("GarrisonDefensive", -1f, 2f, false, p => p.GarrisonDefensive, (p, v) => p.GarrisonDefensive = v),
            P("DefendPerThreat", 0f, 1.5f, false, p => p.DefendPerThreat, (p, v) => p.DefendPerThreat = v),
            P("MaxDefendShareBalanced", 0f, 0.8f, false, p => p.MaxDefendShareBalanced, (p, v) => p.MaxDefendShareBalanced = v),
            P("MaxDefendShareAggressive", 0f, 0.8f, false, p => p.MaxDefendShareAggressive, (p, v) => p.MaxDefendShareAggressive = v),
            P("MaxDefendShareDefensive", 0f, 0.8f, false, p => p.MaxDefendShareDefensive, (p, v) => p.MaxDefendShareDefensive = v),
            P("PostureScoreMargin", 0f, 150f, true, p => p.PostureScoreMargin, (p, v) => p.PostureScoreMargin = (int)v),
            P("DefendForward", 0f, 20f, false, p => p.DefendForward, (p, v) => p.DefendForward = v),
            P("Stickiness", 0f, 1.5f, false, p => p.Stickiness, (p, v) => p.Stickiness = v),
            P("AttackDivertRange", 0f, 200f, false, p => p.AttackDivertRange, (p, v) => p.AttackDivertRange = v),
            P("MinBotsToFlank", 4f, 60f, true, p => p.MinBotsToFlank, (p, v) => p.MinBotsToFlank = (int)v),
            P("FlankOffset", 15f, 90f, false, p => p.FlankOffset, (p, v) => p.FlankOffset = v),
            P("FlankStandoff", 0f, 40f, false, p => p.FlankStandoff, (p, v) => p.FlankStandoff = v),
            P("GatherDistance", 40f, 150f, false, p => p.GatherDistance, (p, v) => p.GatherDistance = v),
            P("GatherShare", 0f, 1.2f, false, p => p.GatherShare, (p, v) => p.GatherShare = v),
            P("GatherMaxWait", 5f, 90f, false, p => p.GatherMaxWait, (p, v) => p.GatherMaxWait = v),
            P("RegroupRadius", 10f, 120f, false, p => p.RegroupRadius, (p, v) => p.RegroupRadius = v),
            P("ReinforceRadius", 20f, 200f, false, p => p.ReinforceRadius, (p, v) => p.ReinforceRadius = v),
        };

        public static int Length => Parameters.Count;

        /// <summary>The profile's weights, each mapped into [0, 1] of its range.</summary>
        public static float[] From(TacticsProfile profile)
        {
            var u = new float[Parameters.Count];
            for (int i = 0; i < u.Length; i++)
            {
                ProfileParameter p = Parameters[i];
                u[i] = Math.Clamp((p.Get(profile) - p.Min) / (p.Max - p.Min), 0f, 1f);
            }
            return u;
        }

        /// <summary>A new profile at <paramref name="u"/>; counts are rounded, everything is clamped to range.</summary>
        public static TacticsProfile To(float[] u)
        {
            var profile = new TacticsProfile();
            for (int i = 0; i < u.Length; i++)
            {
                ProfileParameter p = Parameters[i];
                float value = p.Min + Math.Clamp(u[i], 0f, 1f) * (p.Max - p.Min);
                if (p.Integer) value = MathF.Round(value);
                p.Set(profile, value);
            }
            return profile;
        }

        /// <summary>The profile as C# initialisers, ready to become the shipped defaults.</summary>
        public static string Describe(TacticsProfile profile)
        {
            var text = new StringBuilder();
            foreach (ProfileParameter p in Parameters)
            {
                float value = p.Get(profile);
                text.Append(p.Name).Append(" = ")
                    .Append(p.Integer ? ((int)value).ToString(CultureInfo.InvariantCulture)
                                      : value.ToString("0.###", CultureInfo.InvariantCulture) + "f")
                    .Append('\n');
            }
            return text.ToString();
        }

        private static ProfileParameter P(string name, float min, float max, bool integer,
            Func<TacticsProfile, float> get, Action<TacticsProfile, float> set)
            => new ProfileParameter(name, min, max, integer, get, set);
    }
}
