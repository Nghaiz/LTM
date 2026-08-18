using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// How this bot's LOD decision is made. Serialized so a measurement run can pin it.
    /// </summary>
    public enum BotLodMode
    {
        /// <summary>Ask <see cref="BotLodScheduler"/>. The shipping behaviour.</summary>
        Scheduler = 0,

        /// <summary>Never skip. This is the "LOD off" arm of the before/after comparison.</summary>
        AlwaysOn = 1,

        /// <summary>Always skip. Diagnostic only — measures the floor the AI cost can reach.</summary>
        AlwaysOff = 2,
    }

    /// <summary>
    /// The Unity seam <see cref="BotLodScheduler"/> was written to need: it turns the policy's
    /// per-tick answer into one boolean that <c>AiActorController</c> reads. phase-02 task 5,
    /// checklist item S5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a component and a guard rather than toggling <c>enabled</c>.</b> Toggling the
    /// MonoBehaviour is what the task sketch proposes, and the client track declined it on PR #47 for a
    /// reason that holds up: <c>AiActorController</c> runs eight coroutines alongside
    /// <c>Update</c>. Unity does pause a behaviour's coroutines when it is disabled, so the work
    /// genuinely stops — but every one of those coroutines is parked on a
    /// <c>WaitForSeconds</c>, and a paused-then-resumed wait does not resume at a time anyone
    /// can predict or assert on. <c>Update</c> additionally sees one large <c>Time.deltaTime</c>
    /// on the frame it comes back, which its <c>MoveTowards</c> smoothing reads as a jump.
    /// A run measured that way is measuring the toggle, not the LOD.
    /// </para>
    /// <para>
    /// The guard costs one boolean read per coroutine iteration and per <c>Update</c>, and it
    /// gates <b>all nine</b> workloads rather than the one <c>updateInterval</c> would reach.
    /// </para>
    /// <para>
    /// <b>The interest data it reads is one frame old, deliberately.</b>
    /// <see cref="InterestManager.MaxLevelAmongHumanPlayers"/> is populated while the snapshot
    /// stage builds each player's view, at execution order +200. This component sits at -100:
    /// after the input stage, before <c>AiActorController.Update</c> at the default order. So on
    /// any given frame it reads the levels the previous snapshot built. Chasing the freshest
    /// value would mean evaluating after +200 and having the AI act on it a frame later anyway —
    /// the same staleness, arrived at with more moving parts. One tick of lag on a decision
    /// about whether a bot nobody can see thinks at 30 Hz or 6 Hz does not change the answer.
    /// </para>
    /// <para>
    /// <b>Absent by default.</b> Nothing attaches this component automatically; a bot without one
    /// runs exactly as it did before. That is what makes the guard in
    /// <c>AiActorController</c> safe to land ahead of any measurement.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(NetServerActor))]
    public sealed class BotLodGate : MonoBehaviour
    {
        [Tooltip("Scheduler = shipping behaviour. AlwaysOn = the LOD-off arm of a measurement.")]
        [SerializeField] private BotLodMode _mode = BotLodMode.Scheduler;

        private NetServerActor _actor;

        // The tick AllowAiWork was last computed for. long, not uint, so -1 can mean "never":
        // tick 0 is a real tick and would otherwise be indistinguishable from the initial state.
        private long _evaluatedTick = -1;

        /// <summary>
        /// Whether this bot's AI may run right now. Read by <c>AiActorController</c>.
        /// </summary>
        /// <remarks>
        /// Starts true so a bot that is destroyed, or whose first frame lands before the tick
        /// loop exists, behaves as it always did instead of freezing. A gate that fails closed
        /// would turn a wiring mistake into motionless bots, which reads as a gameplay bug
        /// rather than as a missing binding.
        /// </remarks>
        public bool AllowAiWork { get; private set; } = true;

        /// <summary>The mode this gate is pinned to. Settable so a measurement can script it.</summary>
        public BotLodMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        private void Awake() => _actor = GetComponent<NetServerActor>();

        private void Update()
        {
            switch (_mode)
            {
                case BotLodMode.AlwaysOn:
                    AllowAiWork = true;
                    return;
                case BotLodMode.AlwaysOff:
                    AllowAiWork = false;
                    return;
            }

            // Read every frame rather than cached in Awake: a bot can exist before the server
            // binds, and it can outlive a loop that was torn down between matches. A field
            // caching a stale loop would keep gating against a tick that stopped advancing.
            // This is a static property read, not a scene search -- see ServerTickLoop.Current
            // for why that distinction is the whole reason the property exists.
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null)
            {
                AllowAiWork = true;
                return;
            }

            // Once per simulation tick, not once per frame. Update runs at the render rate and
            // the tick is 30 Hz, so at 60 fps every decision would be asked for twice -- the
            // answer is the same both times (ShouldTick is a pure function of id, interest and
            // tick), but BotLodScheduler counts each call, and those counters ARE the phase-02
            // criterion-8 figure. Double-counting would not change the percentage, but it would
            // make "ticks granted" a number that no longer means ticks.
            uint tick = loop.CurrentTick;
            if (_evaluatedTick == tick) return;
            _evaluatedTick = tick;

            InterestLevel level = loop.Interest.MaxLevelAmongHumanPlayers(_actor.ActorId);
            AllowAiWork = loop.BotLod.ShouldTick(_actor.ActorId, level, tick);
        }
    }
}
