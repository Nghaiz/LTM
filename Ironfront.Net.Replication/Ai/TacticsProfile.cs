namespace Ironfront.Net.Replication.Ai
{
    /// <summary>
    /// The numbers the team commander decides with: what each job is worth to a squad, what a flag
    /// costs in bots, how much of the side may sit on its own flags, when it flanks and when an
    /// assault gathers first. Phase P28, rebuilt in P29 around <see cref="TeamPlanner"/>'s auction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Data, not logic.</b> <see cref="TeamPlanner"/> reads every weight from here, so a tuning
    /// run changes a profile and never the planner, and the same planner code is what the tuner
    /// trains and what the server runs.
    /// </para>
    /// <para>
    /// <b>Two sets of numbers.</b> The property initialisers are the hand-set weights, each argued
    /// in its own summary: what <c>new TacticsProfile()</c> gives, what the planner's own tests run
    /// on, and where the trainer starts. <see cref="Default"/> is the profile the game ships: those
    /// weights after training; see its remark for the run that produced them.
    /// </para>
    /// <para>
    /// <b>Utility units.</b> The weights of a job's worth are in one unit, fixed by
    /// <see cref="TargetBase"/> = 1: what taking a flag is worth before anything else is counted.
    /// </para>
    /// </remarks>
    public sealed class TacticsProfile
    {
        // ---- what a flag the side does not hold is worth ----

        /// <summary>What any target is worth before anything else is counted: the unit of the rest.</summary>
        public float TargetBase { get; set; } = 1f;

        /// <summary>Extra worth of a neutral flag: nobody to take it from.</summary>
        public float NeutralBonus { get; set; } = 0.3f;

        /// <summary>
        /// Extra worth of taking back one of the side's own flags an enemy is standing on: it is
        /// still scoring for the side, and it stops the moment the enemy finishes.
        /// </summary>
        public float RetakeBonus { get; set; } = 0.3f;

        /// <summary>Worth lost per enemy remembered at a target, before its force is counted.</summary>
        public float ThreatWeight { get; set; } = 0.05f;

        /// <summary>Extra worth per enemy-held flag a target opens the way to.</summary>
        public float LinkWeight { get; set; } = 0.15f;

        /// <summary>
        /// Worth lost by a target that does not border a flag of the side's own: a flag deep in enemy
        /// ground, taken behind the front.
        /// </summary>
        public float DeepPenalty { get; set; } = 0.5f;

        /// <summary>Worth lost per 100 m between a squad and a job: the one weight the original squads had.</summary>
        public float DistanceWeight { get; set; } = 0.8f;

        /// <summary>The most flags a side goes for at once, however large it is.</summary>
        public int MaxObjectives { get; set; } = 3;

        // ---- what a flag costs, and what a squad adds to it (phase P29) ----

        /// <summary>
        /// The fewest bots a target is given, and so what an undefended flag costs. A defended one
        /// costs what <see cref="TeamPlanner.ForceFor"/> says, if that is more.
        /// </summary>
        public float BotsPerObjective { get; set; } = 2f;

        /// <summary>
        /// Lanchester's attrition order n: 1 is the linear law of one-on-one fights, 2 the square law
        /// of aimed fire, where numbers count twice. Stanescu et al. fitted about 1.56 to StarCraft.
        /// </summary>
        public float AttritionOrder { get; set; } = 2f;

        /// <summary>
        /// What one defender in cover is worth in attackers (k): the simulator halves fire into a
        /// squad dug in facing it, so 2 is where it starts.
        /// </summary>
        public float DefenderAdvantage { get; set; } = 2f;

        /// <summary>The margin over the force that would only just win: 0.25 sends a quarter more.</summary>
        public float ForceMargin { get; set; } = 0.25f;

        /// <summary>Worth of a squad's bots that go toward a job's force still unmet, per whole force.</summary>
        public float ShortWeight { get; set; } = 0.6f;

        /// <summary>Worth lost for bots a squad would add past the force a job needs, per whole force.</summary>
        public float OverWeight { get; set; } = 0.4f;

        /// <summary>
        /// Worth lost for the bots still missing at a target even with the squad there, per whole
        /// force: going in outnumbered.
        /// </summary>
        public float DeficitWeight { get; set; } = 0.5f;

        /// <summary>
        /// How long enemies seen round a flag stay on the side's mind after contact is lost: the time
        /// for the remembered number to fall to about a third, in seconds. 0 remembers nothing.
        /// </summary>
        public float ThreatMemorySeconds { get; set; } = 20f;

        // ---- the side's own flags ----

        /// <summary>A side smaller than this keeps nobody on a post: every bot is needed at the front.</summary>
        public int MinBotsToDefend { get; set; } = 6;

        /// <summary>What holding a front-line flag of the side's own is worth, by posture.</summary>
        public float GarrisonBalanced { get; set; } = 0.3f;

        public float GarrisonAggressive { get; set; } = 0.1f;

        public float GarrisonDefensive { get; set; } = 0.6f;

        /// <summary>Extra worth of a post per enemy remembered round it.</summary>
        public float DefendPerThreat { get; set; } = 0.25f;

        /// <summary>The most of the side that ever sits on posts, by posture.</summary>
        public float MaxDefendShareBalanced { get; set; } = 0.35f;

        public float MaxDefendShareAggressive { get; set; } = 0.2f;

        public float MaxDefendShareDefensive { get; set; } = 0.5f;

        /// <summary>Points ahead or behind that tip the posture, beside the flag count.</summary>
        public int PostureScoreMargin { get; set; } = 30;

        /// <summary>How far from the flag toward the threat a post digs in.</summary>
        public float DefendForward { get; set; } = 8f;

        // ---- how a job is done ----

        /// <summary>
        /// Worth added to the job a squad already has: what keeps a squad committed instead of
        /// turning round every plan. The owner asked for decisive.
        /// </summary>
        public float Stickiness { get; set; } = 0.25f;

        /// <summary>
        /// How far past a flag's capture range, in metres, an attacking squad breaks off from its
        /// objective to take a flag that needs taking -- one not its side's, or its side's with an
        /// enemy on it. The original squads always went for the nearest such flag; an attack keeps
        /// that reflex within this reach (read by <c>Squad.MayDivertTo</c>).
        /// </summary>
        public float AttackDivertRange { get; set; } = 10f;

        /// <summary>A side smaller than this never splits a squad off to flank.</summary>
        public int MinBotsToFlank { get; set; } = 8;

        /// <summary>How far to the side of the attack axis a flank's waypoint lies, in metres.</summary>
        public float FlankOffset { get; set; } = 45f;

        /// <summary>How far short of the target, along the axis, a flank's waypoint lies.</summary>
        public float FlankStandoff { get; set; } = 15f;

        /// <summary>How far short of a defended flag, toward home, an assault gathers, in metres.</summary>
        public float GatherDistance { get; set; } = 80f;

        /// <summary>
        /// The share of the force a defended flag costs that must have gathered before the assault
        /// goes in; 0 sends every squad straight in, as P28 did.
        /// </summary>
        public float GatherShare { get; set; } = 0.8f;

        /// <summary>The longest an assault waits to gather before it goes in anyway, in seconds.</summary>
        public float GatherMaxWait { get; set; } = 30f;

        // ---- keeping squads whole (phase P29) ----

        /// <summary>
        /// How far a lone bot walks to join a squad with room, in metres (<see cref="SquadRegroup"/>).
        /// Larger squads fight better and hold fewer flags: the simulator weighs the two.
        /// </summary>
        public float RegroupRadius { get; set; } = SquadRegroup.DefaultJoinRadius;

        /// <summary>
        /// How far from its flag a bot a spawn wave brings back on its own looks for a squad to
        /// reinforce instead of starting one, in metres (<c>ActorManager.SpawnWave</c>).
        /// </summary>
        public float ReinforceRadius { get; set; } = 150f;

        /// <summary>A new, independent copy of the shipped profile: the hand-set weights, trained.</summary>
        /// <remarks>
        /// <para>
        /// From <c>Ironfront.Tools.TacticsTrainer train --seed 2026093001 --generations 150
        /// --population 32</c>: CMA-ES against a league of the original squads and the P28 commander;
        /// the run, its numbers and how to repeat it are in
        /// <c>plans/reports/2026-09-30-p29-commander-training.md</c>. On 320 held-out simulated rounds
        /// this profile beat the original squads 124 to 30 (mean margin +0.42) and the P28 commander
        /// 159 to 19 (+0.54), ahead at every side size from 4 to 50.
        /// </para>
        /// <para>
        /// <b>What it learned.</b> Spread out: a squad pays heavily for going where the force a flag
        /// needs is already on its way (<see cref="OverWeight"/>), so the side works up to eight
        /// flags at once. Price a defended flag high -- about five times its remembered defenders --
        /// and remember them for half a minute. Keep nobody on a quiet flag of its own; defend one
        /// only against a real threat. Merge lone bots only when they are right beside a squad.
        /// And it switched off two things the hand-set profile did: gathering before an assault and
        /// flanking both cost more time than they won in the simulator.
        /// </para>
        /// <para>
        /// <b>Flanks stay off, by the owner's choice.</b> Asked for on 2026-09-30, they were measured
        /// back on: with this profile, flanking at 16 to 24 bots a side on a 30-45 m detour cut the
        /// lead over the original squads from +0.42 to +0.22-0.26, to about level at 32 and 50 a side;
        /// a run trained with flanks forced on reached only +0.13. The owner then chose the best
        /// result, this one (report: "Flanks, measured").
        /// </para>
        /// </remarks>
        public static TacticsProfile Default() => new TacticsProfile
        {
            NeutralBonus = 0.822f,
            RetakeBonus = 0.021f,
            ThreatWeight = 0.232f,
            LinkWeight = 0.015f,
            DeepPenalty = 0f,
            DistanceWeight = 0.509f,
            MaxObjectives = 8,
            BotsPerObjective = 3.134f,
            AttritionOrder = 1.129f,
            DefenderAdvantage = 3.045f,
            ForceMargin = 0.848f,
            ShortWeight = 0.247f,
            OverWeight = 2.849f,
            DeficitWeight = 0.001f,
            ThreatMemorySeconds = 31.491f,
            MinBotsToDefend = 5,
            GarrisonBalanced = -0.461f,
            GarrisonAggressive = -0.491f,
            GarrisonDefensive = -0.284f,
            DefendPerThreat = 0.295f,
            MaxDefendShareBalanced = 0.575f,
            MaxDefendShareAggressive = 0.552f,
            MaxDefendShareDefensive = 0.68f,
            PostureScoreMargin = 5,
            DefendForward = 6.595f,
            Stickiness = 0.495f,
            AttackDivertRange = 29.591f,
            MinBotsToFlank = 58,
            FlankOffset = 17.211f,
            FlankStandoff = 13.087f,
            GatherDistance = 136.294f,
            GatherShare = 0f,
            GatherMaxWait = 14.464f,
            RegroupRadius = 14.16f,
            ReinforceRadius = 79.113f,
        };

        /// <summary>A copy, for a tuner to perturb.</summary>
        public TacticsProfile Clone() => (TacticsProfile)MemberwiseClone();
    }
}
