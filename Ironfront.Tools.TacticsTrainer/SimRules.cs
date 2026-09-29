namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// The numbers the simulated conquest plays by. Where the game has the number, it is the
    /// game's; where the game has behaviour instead, the number is a stated stand-in.
    /// </summary>
    public static class SimRules
    {
        /// <summary>One step of the simulation, in seconds: the bots' own order tick.</summary>
        public const float TickSeconds = 0.5f;

        /// <summary>A round that nobody has won by then is scored by its margin.</summary>
        public const float MaxSeconds = 20f * 60f;

        /// <summary>Metres a second on foot: the bots' 3.2 m/s walk with their sprints mixed in.</summary>
        public const float WalkSpeed = 3.8f;

        /// <summary>A bot with a target walks at 2 m/s (AiActorController.Velocity).</summary>
        public const float EngagedSpeed = 2f;

        /// <summary>A sneaking squad does not sprint.</summary>
        public const float SneakSpeed = 3.2f;

        /// <summary>Two squads this close find each other and fight. Stand-in.</summary>
        public const float EngageRange = 70f;

        /// <summary>A sneaking squad is only found this close: CombatRules.AmbushRange.</summary>
        public const float SneakDetectRange = 45f;

        /// <summary>Kills a bot makes per second of fighting. Stand-in: one every 40 s.</summary>
        public const float KillsPerBotSecond = 0.025f;

        /// <summary>
        /// The share of its losses a squad dug in on its spot takes from fire inside its cover's
        /// arc. Stand-in for part 2's cover; flanking fire gets none of it.
        /// </summary>
        public const float CoverFactor = 0.5f;

        /// <summary>Cover faces its threat: fire from within 60 degrees of that bearing is covered.</summary>
        public const float CoverConeCos = 0.5f;

        /// <summary>Bots come back in waves this far apart: ActorManager.spawnTime.</summary>
        public const float SpawnWaveSeconds = 10f;

        /// <summary>A bot dead less than this waits for the next wave: AI_SPAWN_WAVE_DEATH_GRACE.</summary>
        public const float DeadGraceSeconds = 6f;

        /// <summary>
        /// Bots of one wave at one flag form squads of this many, drawn per squad, as
        /// ActorManager's Random.Range(2, Random.Range(2, maxSquadSize + 2)) mostly gives.
        /// </summary>
        public const int MinSquadSize = 2;

        public const int MaxSquadSize = 4;

        /// <summary>
        /// Share of spawns at a frontline flag rather than any flag held: the original bots' 70/30
        /// (AiActorController.SelectedSpawnPoint).
        /// </summary>
        public const float FrontlineSpawnShare = 0.7f;

        /// <summary>Ownership moved per second per bot of majority: CapturePoint.captureSpeed.</summary>
        public const float CaptureSpeed = 0.06f;

        /// <summary>A flag an enemy stood on counts as unsafe for this long. Stand-in.</summary>
        public const float UnsafeSeconds = 5f;

        /// <summary>The lead that wins: MatchRules.VictoryPoints.</summary>
        public const int VictoryPoints = 200;

        /// <summary>Holding no flag this long loses the round: MatchController's dwell.</summary>
        public const float EliminationDwellSeconds = 5f;

        /// <summary>How often each side's commander plans: BotCommander.PlanPeriod.</summary>
        public const float PlanPeriod = 2f;

        /// <summary>Enemies this near a flag threaten it: BotCommander.ThreatRadius.</summary>
        public const float ThreatRadius = 60f;

        /// <summary>An enemy this near one of the side's bots is known to it: BotCommander.ContactRadius.</summary>
        public const float ContactRadius = 90f;

        /// <summary>A flank has turned in once this close to its waypoint: Squad.UpdateCommandProgress.</summary>
        public const float FlankReached = 12f;

        /// <summary>How far past a flag's capture range an attack breaks off for it, when no profile says.</summary>
        public const float DivertSlack = 10f;
    }
}
