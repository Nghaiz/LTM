using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>One squad in the simulation: where it is, how many are left, what it was told.</summary>
    public sealed class SimSquad
    {
        public int Id;
        public int Team;
        public float X;
        public float Z;
        public int Size;

        /// <summary>Losses owed but not yet a whole bot.</summary>
        public float Wounds;

        /// <summary>The flag it is heading for now: its order's, or one it broke off for.</summary>
        public int Flag = -1;

        /// <summary>Where exactly it is walking to.</summary>
        public float TargetX;
        public float TargetZ;

        // ---- the commander's order; Role None is the original's own behaviour ----
        public SquadRole Role;
        public int CommandFlag = -1;
        public bool HasWaypoint;
        public float WaypointX;
        public float WaypointZ;
        public bool WaypointReached;
        public bool Sneak;

        /// <summary>Arrived and holding its spot, covered against fire from <see cref="FaceX"/>/<see cref="FaceZ"/>.</summary>
        public bool DugIn;
        public float FaceX;
        public float FaceZ;

        /// <summary>In a fight this tick, and the index of the enemy squad it is shooting.</summary>
        public bool Engaged;
        public int Victim = -1;

        /// <summary>
        /// The id of the squad this one is walking over to join, -1 for none: a lone bot a spawn wave
        /// sent to reinforce a squad, or one the commander folded into another (phase P29). It
        /// merges on arrival and takes no orders on the way.
        /// </summary>
        public int JoinId = -1;
    }

    /// <summary>How a round ended.</summary>
    public readonly struct SimResult
    {
        public SimResult(int winner, int score0, int score1, float seconds, bool eliminated)
        {
            Winner = winner;
            Score0 = score0;
            Score1 = score1;
            Seconds = seconds;
            Eliminated = eliminated;
        }

        /// <summary>0 or 1; -1 when the clock ran out first.</summary>
        public int Winner { get; }
        public int Score0 { get; }
        public int Score1 { get; }
        public float Seconds { get; }

        /// <summary>Won by taking every flag rather than by the lead.</summary>
        public bool Eliminated { get; }

        /// <summary>
        /// <paramref name="team"/>'s result as one number in [-1, 1]: a win is 1, a loss -1, a
        /// round the clock ended is the lead as a share of the victory margin.
        /// </summary>
        public float MarginFor(int team)
        {
            if (Winner >= 0) return Winner == team ? 1f : -1f;
            float lead = (team == 0 ? Score0 - Score1 : Score1 - Score0) / (float)SimRules.VictoryPoints;
            return lead < -1f ? -1f : lead > 1f ? 1f : lead;
        }
    }

    /// <summary>How one side decides: the original squads' own choices, or a commander over them.</summary>
    public interface ISidePolicy
    {
        /// <summary>Called every tick for its side; a policy keeps its own cadence.</summary>
        void Tick(ConquestSim sim, int team);
    }
}
