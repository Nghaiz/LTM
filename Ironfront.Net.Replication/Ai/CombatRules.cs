namespace Ironfront.Net.Replication.Ai
{
    /// <summary>What a bot standing in the open does with its turn.</summary>
    public enum OpenGroundMove
    {
        /// <summary>Nothing to react to: stand, look round.</summary>
        Hold = 0,

        /// <summary>A far target: crouch, a smaller body and a steadier shot.</summary>
        Crouch = 1,

        /// <summary>A near target, or shots coming in: step sideways, a harder body to hit.</summary>
        SideStep = 2,
    }

    /// <summary>
    /// How one bot fights, apart from the engine: which enemy it shoots first, when it crouches,
    /// side-steps, falls back or holds cover, when a squad sneaking round the side holds its fire,
    /// and what a cover spot is worth. Phase P28, part 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The split.</b> <c>AiActorController</c> measures -- distances, rays, health, who shot
    /// last -- and asks these questions; the answers and every number they turn on live here,
    /// where a unit test pins them and part 4's tuner can read them.
    /// </para>
    /// <para>
    /// <b>What the original did.</b> A bot shot the nearest enemy it could see, stood still or
    /// walked its path at 2 m/s while firing, crouched only behind low cover while reloading, and
    /// left cover with its squad the moment no shot had passed near it for three seconds -- even
    /// mid-exchange. Nothing here changes how well a bot aims.
    /// </para>
    /// </remarks>
    public static class CombatRules
    {
        // ------------------------------------------------------------------ choosing a target

        /// <summary>The most enemies a bot weighs in one look round, best first.</summary>
        /// <remarks>
        /// The look round waits 0.2 s on every enemy it cannot see, so an uncapped list of fifty
        /// took ten seconds to walk; sixteen bounds it to about three.
        /// </remarks>
        public const int MaxTargetCandidates = 16;

        /// <summary>A fallen-over enemy counts as this many metres further: the original's number.</summary>
        public const float FallenOverPenalty = 30f;

        /// <summary>An enemy that shot at this bot counts as this many metres closer.</summary>
        public const float ShooterBonus = 30f;

        /// <summary>How long a bot remembers who shot at it, in seconds.</summary>
        public const float ShooterMemorySeconds = 6f;

        /// <summary>An enemy on the flag the bot's squad was sent to counts as this much closer.</summary>
        public const float ObjectiveBonus = 12f;

        /// <summary>How near the squad's flag an enemy is "on" it, in metres.</summary>
        public const float ObjectiveRadius = 25f;

        /// <summary>
        /// How urgent an enemy is, in metres: its distance, less the bonuses. Lower is shot first.
        /// </summary>
        public static float TargetScore(float distance, bool fallenOver, bool shootingAtMe, bool onObjective)
        {
            float score = distance;
            if (fallenOver) score += FallenOverPenalty;
            if (shootingAtMe) score -= ShooterBonus;
            if (onObjective) score -= ObjectiveBonus;
            return score;
        }

        // ------------------------------------------------------------------ in the open

        /// <summary>From this range out, a bot in the open crouches to shoot instead of stepping.</summary>
        public const float CrouchFireRange = 35f;

        /// <summary>A side-step's speed, in metres a second: a walk, not a sprint.</summary>
        public const float SideStepSpeed = 2.4f;

        /// <summary>A side-step lasts this long, in seconds, drawn between the two.</summary>
        public const float SideStepMinSeconds = 0.5f;

        public const float SideStepMaxSeconds = 1.2f;

        /// <summary>The pause after a step before the next, in seconds, drawn between the two.</summary>
        public const float SideStepPauseMinSeconds = 1.0f;

        public const float SideStepPauseMaxSeconds = 2.6f;

        /// <summary>The ground a step needs clear ahead of it, in metres: more than one step covers.</summary>
        public const float SideStepClearance = 3f;

        /// <summary>
        /// Whether a bot is in the open: on its feet, not walking a path, not in or bound for
        /// cover. Only a bot in the open side-steps or crouches to fire.
        /// </summary>
        public static bool InTheOpen(bool onFoot, bool walkingPath, bool inOrBoundForCover)
            => onFoot && !walkingPath && !inOrBoundForCover;

        /// <summary>What a bot in the open does: dodge incoming fire, crouch at range, step up close.</summary>
        public static OpenGroundMove InTheOpenMove(bool hasTarget, float targetDistance, bool takingFire)
        {
            if (takingFire) return OpenGroundMove.SideStep;
            if (!hasTarget) return OpenGroundMove.Hold;
            return targetDistance >= CrouchFireRange ? OpenGroundMove.Crouch : OpenGroundMove.SideStep;
        }

        // ------------------------------------------------------------------ falling back

        /// <summary>Below this health a bot in a fight falls back to cover. A bot has 100.</summary>
        public const float FallBackHealth = 35f;

        /// <summary>How far a hurt bot looks for somewhere to hide, in metres.</summary>
        public const float FallBackSearchRadius = 25f;

        /// <summary>How long a bot that fell back stays in its cover, in seconds.</summary>
        public const float FallBackHoldSeconds = 8f;

        /// <summary>The least time between two fall-backs by one bot, in seconds.</summary>
        public const float FallBackCooldownSeconds = 15f;

        /// <summary>Whether a hurt bot in a fight should break off for cover.</summary>
        public static bool ShouldFallBack(float health, bool engaged, bool inCover, bool onFoot)
            => onFoot && engaged && !inCover && health > 0f && health < FallBackHealth;

        // ------------------------------------------------------------------ holding cover

        /// <summary>
        /// The longest a squad that dug in under fire keeps firing from cover once no shot comes
        /// near it, in seconds, before it moves on.
        /// </summary>
        public const float HoldCoverSeconds = 12f;

        /// <summary>
        /// Whether a dug-in squad stays put: while any member still has an enemy in its sights,
        /// for up to <see cref="HoldCoverSeconds"/> after it dug in.
        /// </summary>
        public static bool HoldCover(bool dugIn, bool engaged, float secondsDugIn)
            => dugIn && engaged && secondsDugIn < HoldCoverSeconds;

        // ------------------------------------------------------------------ sneaking

        /// <summary>A squad sneaking round the side opens fire inside this range, or when found.</summary>
        public const float AmbushRange = 45f;

        /// <summary>
        /// Whether a sneaking bot keeps its finger off the trigger: a shot at a far target gives
        /// the flank away for nothing, so it waits until it is close or shot at.
        /// </summary>
        public static bool HoldFire(bool sneaking, bool takingFire, float targetDistance)
            => sneaking && !takingFire && targetDistance > AmbushRange;

        // ------------------------------------------------------------------ choosing cover

        /// <summary>How far round a bot under fire it looks for cover, in metres.</summary>
        /// <remarks>
        /// The original took any authored point up to 50 m away, which could mean a 50 m sprint
        /// across open ground under fire to reach it.
        /// </remarks>
        public const float CoverSearchRadius = 30f;

        /// <summary>The most authored cover points judged in one search, nearest first.</summary>
        public const int MaxCoverCandidates = 8;

        /// <summary>The most vehicles judged as cover in one search, nearest first.</summary>
        public const int MaxVehicleCandidates = 3;

        /// <summary>Metres added for a spot the bot could hide at but not shoot from, in a fight.</summary>
        public const float NoFirePenalty = 10f;

        /// <summary>Metres added per metre a fighting bot's spot lies toward the enemy.</summary>
        public const float ClosingWeight = 0.5f;

        /// <summary>Metres added per metre a fighting bot's spot gives ground.</summary>
        public const float GiveGroundWeight = 1f;

        /// <summary>Metres added per metre a falling-back bot's spot lies toward the enemy.</summary>
        public const float FallBackClosingWeight = 2f;

        /// <summary>
        /// What a cover spot costs, in metres; the cheapest wins. <paramref name="towardThreat"/>
        /// is how far the spot lies toward the enemy from where the search started (negative:
        /// away). Never less than <paramref name="distance"/>, which is what lets a search stop
        /// at the first candidate further than its best so far.
        /// </summary>
        public static float CoverCost(float distance, float towardThreat, bool canFire, bool fallingBack)
        {
            float cost = distance;
            if (fallingBack)
            {
                if (towardThreat > 0f) cost += towardThreat * FallBackClosingWeight;
                return cost;
            }
            cost += towardThreat > 0f ? towardThreat * ClosingWeight : -towardThreat * GiveGroundWeight;
            if (!canFire) cost += NoFirePenalty;
            return cost;
        }
    }
}
