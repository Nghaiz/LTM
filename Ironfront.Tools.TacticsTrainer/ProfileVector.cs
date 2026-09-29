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
            P("BotsPerObjective", 2f, 16f, false, p => p.BotsPerObjective, (p, v) => p.BotsPerObjective = v),
            P("MaxObjectives", 1f, 8f, true, p => p.MaxObjectives, (p, v) => p.MaxObjectives = (int)v),
            P("NeutralBonus", 0f, 2f, false, p => p.NeutralBonus, (p, v) => p.NeutralBonus = v),
            P("ThreatWeight", 0f, 0.6f, false, p => p.ThreatWeight, (p, v) => p.ThreatWeight = v),
            P("LinkWeight", 0f, 1.2f, false, p => p.LinkWeight, (p, v) => p.LinkWeight = v),
            P("DistanceWeight", 0f, 1.5f, false, p => p.DistanceWeight, (p, v) => p.DistanceWeight = v),
            P("MinBotsToDefend", 0f, 20f, true, p => p.MinBotsToDefend, (p, v) => p.MinBotsToDefend = (int)v),
            P("GarrisonBalanced", 0f, 8f, false, p => p.GarrisonBalanced, (p, v) => p.GarrisonBalanced = v),
            P("GarrisonAggressive", 0f, 6f, false, p => p.GarrisonAggressive, (p, v) => p.GarrisonAggressive = v),
            P("GarrisonDefensive", 0f, 10f, false, p => p.GarrisonDefensive, (p, v) => p.GarrisonDefensive = v),
            P("DefendPerThreat", 0f, 3f, false, p => p.DefendPerThreat, (p, v) => p.DefendPerThreat = v),
            P("MaxDefendShareBalanced", 0f, 0.7f, false, p => p.MaxDefendShareBalanced, (p, v) => p.MaxDefendShareBalanced = v),
            P("MaxDefendShareAggressive", 0f, 0.6f, false, p => p.MaxDefendShareAggressive, (p, v) => p.MaxDefendShareAggressive = v),
            P("MaxDefendShareDefensive", 0f, 0.9f, false, p => p.MaxDefendShareDefensive, (p, v) => p.MaxDefendShareDefensive = v),
            P("PostureScoreMargin", 0f, 150f, true, p => p.PostureScoreMargin, (p, v) => p.PostureScoreMargin = (int)v),
            P("MinBotsToFlank", 4f, 40f, true, p => p.MinBotsToFlank, (p, v) => p.MinBotsToFlank = (int)v),
            P("FlankOffset", 15f, 90f, false, p => p.FlankOffset, (p, v) => p.FlankOffset = v),
            P("FlankStandoff", 0f, 40f, false, p => p.FlankStandoff, (p, v) => p.FlankStandoff = v),
            P("DefendForward", 0f, 20f, false, p => p.DefendForward, (p, v) => p.DefendForward = v),
            P("Stickiness", 0f, 1.5f, false, p => p.Stickiness, (p, v) => p.Stickiness = v),
            P("AttackDivertRange", 0f, 200f, false, p => p.AttackDivertRange, (p, v) => p.AttackDivertRange = v),
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
