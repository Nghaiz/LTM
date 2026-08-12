using Ironfront.Net.Replication.Interest;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Decides which bots run their AI this tick. phase-02 task 5, risks R6 and C3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bot nobody can see does not need to think 30 times a second. Bots at
    /// <see cref="InterestLevel.Mid"/> or better to some human player tick every time;
    /// everything further away ticks once every <see cref="FarTickInterval"/> ticks.
    /// </para>
    /// <para>
    /// <b>The phase is offset by actor id, which the task sketch does not do.</b> A plain
    /// <c>serverTick % 5 == 0</c> makes every distant bot think on the same tick, so instead of
    /// cutting the AI cost it concentrates four ticks' worth of it into one — the p99 the tick
    /// budget is graded on gets worse while the mean improves, which is exactly the wrong
    /// trade for criterion 7. Offsetting by id spreads them across the five ticks evenly.
    /// </para>
    /// <para>
    /// <b>This class only decides.</b> Actually skipping the AI is the Unity wrapper's job,
    /// and how it should do that is an open question: toggling
    /// <c>AiActorController.enabled</c> is what task 5 sketches, and trap 7 then warns that
    /// coroutines and <c>Time.deltaTime</c> timers inside the controller can misbehave when it
    /// is toggled repeatedly. The clean fix is an <c>updateInterval</c> field inside
    /// <c>AiActorController</c>, which is Dev A's file — filed on the Dev A checklist rather
    /// than changed here. Keeping the policy separate from the mechanism means that answer,
    /// whenever it lands, does not touch this logic or its tests.
    /// </para>
    /// </remarks>
    public sealed class BotLodScheduler
    {
        /// <summary>Ticks between AI updates for a bot no human is near.</summary>
        /// <remarks>
        /// 5 ticks is 6 Hz at a 30 Hz simulation — slow enough to save most of the cost, fast
        /// enough that a bot re-entering a player's view is already reacting rather than
        /// standing still for a fifth of a second.
        /// </remarks>
        public const int FarTickInterval = 5;

        /// <summary>The interest level at which a bot always ticks.</summary>
        public const InterestLevel FullRateThreshold = InterestLevel.Mid;

        /// <summary>Ticks granted since construction.</summary>
        public long TicksGranted { get; private set; }

        /// <summary>Ticks skipped since construction.</summary>
        public long TicksSkipped { get; private set; }

        /// <summary>
        /// Percentage of bot AI updates skipped. phase-02 criterion 8 wants at least 30%.
        /// </summary>
        /// <remarks>
        /// A proxy for CPU saving, not a measurement of it — it assumes every AI update costs
        /// the same. The real figure is a Profiler read on the Unity server, which is why the
        /// criterion says "Profiler, before/after" and why this is reported as skipped-update
        /// share rather than as a saving in milliseconds.
        /// </remarks>
        public double SkippedPercent
        {
            get
            {
                long total = TicksGranted + TicksSkipped;
                return total == 0 ? 0.0 : 100.0 * TicksSkipped / total;
            }
        }

        /// <summary>
        /// Whether this bot should run its AI on <paramref name="serverTick"/>.
        /// </summary>
        /// <param name="botActorId">Used to offset the phase — see the type remarks.</param>
        /// <param name="maxHumanInterest">
        /// From <see cref="InterestManager.MaxLevelAmongHumanPlayers"/>. Human interest, not
        /// any actor's: a cluster of bots keeping each other at full rate in an empty corner
        /// of the map is the whole cost this is meant to remove.
        /// </param>
        public bool ShouldTick(ushort botActorId, InterestLevel maxHumanInterest, uint serverTick)
        {
            bool tick = maxHumanInterest >= FullRateThreshold
                || (serverTick + botActorId) % FarTickInterval == 0;

            if (tick) TicksGranted++;
            else TicksSkipped++;

            return tick;
        }

        /// <summary>
        /// Seconds of simulated time a skipped-then-granted bot should be advanced by.
        /// </summary>
        /// <remarks>
        /// A bot ticking at 6 Hz that is handed a 30 Hz delta moves at a fifth of its intended
        /// speed — the LOD would show up in gameplay as distant bots crawling. The Unity
        /// wrapper feeds this value in as the AI's delta instead.
        /// </remarks>
        public static float DeltaTimeFor(InterestLevel maxHumanInterest, float fixedDeltaTime)
            => maxHumanInterest >= FullRateThreshold
                ? fixedDeltaTime
                : fixedDeltaTime * FarTickInterval;

        /// <summary>Zeroes the counters.</summary>
        public void ResetStatistics()
        {
            TicksGranted = 0;
            TicksSkipped = 0;
        }
    }
}
