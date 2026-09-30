// FROZEN BASELINE -- do not edit, do not use in the game.
//
// The team commander exactly as it shipped in v2.1.0 (develop 3b51041, phases P28 parts 1-4),
// copied here when phase P29 rewrote TeamPlanner. It exists only so the simulator can play the new
// commander against the old one: without it "v2 beats v1" would be a claim nobody could re-check.
// The game never loads it -- it lives in the trainer, which does not ship.

namespace Ironfront.Tools.TacticsTrainer.Baselines
{
    /// <summary>
    /// The numbers the team commander decides with: how many bots it takes to pursue an objective,
    /// how much it keeps at home, what makes a target worth taking, when it flanks. Phase P28.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Data, not logic.</b> <see cref="TeamPlannerV1"/> reads every weight from here, so a tuning
    /// run changes a profile and never the planner, and the same planner code is what the tuner
    /// trains and what the server runs.
    /// </para>
    /// <para>
    /// <b>Two sets of numbers.</b> The property initialisers are the hand-set weights, each
    /// argued in its own summary: what <c>new TacticsProfileV1()</c> gives, what the planner's own
    /// tests run on, and where part 4's trainer starts. <see cref="Default"/> is the profile the
    /// game ships: those weights after training; see its remark for the run that produced them.
    /// </para>
    /// </remarks>
    public sealed class TacticsProfileV1
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

        /// <summary>
        /// How far past a flag's capture range, in metres, an attacking squad breaks off from its
        /// objective to take a flag that needs taking -- one not its side's, or its side's with an
        /// enemy on it. The original squads always went for the nearest such flag; an attack keeps
        /// that reflex within this reach (read by <c>Squad.MayDivertTo</c>).
        /// </summary>
        public float AttackDivertRange { get; set; } = 10f;

        /// <summary>A new, independent copy of the shipped profile: the hand-set weights, trained.</summary>
        /// <remarks>
        /// <para>
        /// From <c>Ironfront.Tools.TacticsTrainer train --seed 2026093001 --generations 100
        /// --population 32</c>; the run, its numbers and how to repeat it are in
        /// <c>plans/reports/2026-09-30-p28-tactics-training.md</c>. On 320 held-out simulated rounds this
        /// profile beat the hand-set one 167 to 30 (mean margin +0.54) and kept roughly level with
        /// the original game's squads (69 to 88, -0.08), where the hand-set profile lost 21 to 171.
        /// </para>
        /// <para>
        /// <b>What it learned.</b> Go for many flags at once (one objective per two bots, up to
        /// six), keep almost nobody at home until a flag is actually threatened and then send two
        /// defenders per attacker, break off for any flag within ~185 m that needs taking, and
        /// flank only in a big battle (38 bots or more a side).
        /// </para>
        /// </remarks>
        public static TacticsProfileV1 Default() => new TacticsProfileV1
        {
            BotsPerObjective = 2.162f,
            MaxObjectives = 6,
            NeutralBonus = 1.32f,
            ThreatWeight = 0.269f,
            LinkWeight = 0.284f,
            DistanceWeight = 0.6f,
            MinBotsToDefend = 10,
            GarrisonBalanced = 0.275f,
            GarrisonAggressive = 0.239f,
            GarrisonDefensive = 0.363f,
            DefendPerThreat = 1.973f,
            MaxDefendShareBalanced = 0.425f,
            MaxDefendShareAggressive = 0.507f,
            MaxDefendShareDefensive = 0.434f,
            PostureScoreMargin = 68,
            MinBotsToFlank = 38,
            FlankOffset = 16.657f,
            FlankStandoff = 9.184f,
            DefendForward = 3.375f,
            Stickiness = 0.506f,
            AttackDivertRange = 185.142f,
        };

        /// <summary>A copy, for a tuner to perturb.</summary>
        public TacticsProfileV1 Clone() => (TacticsProfileV1)MemberwiseClone();
    }
}
