namespace Ironfront.Net.Replication.Ai
{
    /// <summary>
    /// The numbers the team commander decides with: how many bots it takes to pursue an objective,
    /// how much it keeps at home, what makes a target worth taking, when it flanks. Phase P28.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Data, not logic.</b> <see cref="TeamPlanner"/> reads every weight from here, so a tuning
    /// run changes a profile and never the planner, and the same planner code is what the tuner
    /// trains and what the server runs.
    /// </para>
    /// <para>
    /// <see cref="Default"/> is the profile the game ships with; see its remark for where its
    /// numbers came from.
    /// </para>
    /// </remarks>
    public sealed class TacticsProfile
    {
        /// <summary>Bots per objective pursued at once: a side of 24 at 8 goes for three flags.</summary>
        public float BotsPerObjective { get; set; } = 8f;

        /// <summary>The most objectives pursued at once, however large the side.</summary>
        public int MaxObjectives { get; set; } = 3;

        /// <summary>What any target is worth before anything else is counted.</summary>
        public float TargetBase { get; set; } = 1f;

        /// <summary>Extra worth of a neutral flag: nobody to take it from.</summary>
        public float NeutralBonus { get; set; } = 0.5f;

        /// <summary>Worth lost per enemy in contact at the target.</summary>
        public float ThreatWeight { get; set; } = 0.15f;

        /// <summary>Extra worth per enemy-held flag the target opens the way to.</summary>
        public float LinkWeight { get; set; } = 0.3f;

        /// <summary>Worth lost per 100 m from the side's nearest flag (or squads, when it has none).</summary>
        public float DistanceWeight { get; set; } = 0.4f;

        /// <summary>A side smaller than this keeps nobody at home: every bot is needed at the front.</summary>
        public int MinBotsToDefend { get; set; } = 6;

        /// <summary>Bots kept on each frontline flag with nobody attacking it, by posture.</summary>
        public float GarrisonBalanced { get; set; } = 2f;

        public float GarrisonAggressive { get; set; } = 1f;

        public float GarrisonDefensive { get; set; } = 3f;

        /// <summary>Defenders added per enemy in contact at one of the side's flags.</summary>
        public float DefendPerThreat { get; set; } = 1f;

        /// <summary>The most of the side that ever defends, by posture.</summary>
        public float MaxDefendShareBalanced { get; set; } = 0.35f;

        public float MaxDefendShareAggressive { get; set; } = 0.2f;

        public float MaxDefendShareDefensive { get; set; } = 0.5f;

        /// <summary>Points ahead or behind that tip the posture, beside the flag count.</summary>
        public int PostureScoreMargin { get; set; } = 30;

        /// <summary>A side smaller than this never splits a squad off to flank.</summary>
        public int MinBotsToFlank { get; set; } = 8;

        /// <summary>How far to the side of the attack axis a flank's waypoint lies, in metres.</summary>
        public float FlankOffset { get; set; } = 45f;

        /// <summary>How far short of the target, along the axis, a flank's waypoint lies.</summary>
        public float FlankStandoff { get; set; } = 15f;

        /// <summary>How far from the flag toward the threat a defence digs in.</summary>
        public float DefendForward { get; set; } = 8f;

        /// <summary>
        /// How much better (in worth) a new target must be before an attacking squad is sent from
        /// its current one: what keeps a squad committed instead of turning round every tick.
        /// </summary>
        public float Stickiness { get; set; } = 0.35f;

        /// <summary>A new, independent copy of the shipped profile.</summary>
        public static TacticsProfile Default() => new TacticsProfile();

        /// <summary>A copy, for a tuner to perturb.</summary>
        public TacticsProfile Clone() => (TacticsProfile)MemberwiseClone();
    }
}
