using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Drives <see cref="NetMovementAgent"/> at exactly <c>SIM_TICK_RATE</c> from its own
    /// accumulator, independently of Unity's physics rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C. Implements the decision recorded for checklist item A5 — <b>option B</b>:
    /// the project keeps its physics timestep, and prediction stops riding
    /// <see cref="MonoBehaviour"/>'s <c>FixedUpdate</c>.
    /// </para>
    /// <para>
    /// <b>Why option A could not have worked, whatever anyone chose.</b> Option A was to set
    /// <c>ProjectSettings/TimeManager.asset</c> to 0.0333 and let <c>FixedUpdate</c> be the
    /// tick. That setting does not survive the first frame: <c>IngameMenuUi.Hide()</c>
    /// assigns <c>Time.fixedDeltaTime = Time.timeScale / 60f</c>, and it is called from
    /// <c>IngameMenuUi.Awake()</c>. <c>FpsActorController</c> assigns the same expression
    /// again on every slow-motion toggle. So the live timestep is 1/60 during play and
    /// 0.2/60 in slow motion, never the 0.02 in the asset and never the 0.0333 option A would
    /// have written there. A tick rate that three unrelated files can overwrite is not a tick
    /// rate. This component owns the netcode's clock outright, which is the only arrangement
    /// those assignments cannot break.
    /// </para>
    /// <para>
    /// <b>Update, not FixedUpdate.</b> The accumulator has to be sampled on a clock nobody
    /// else rewrites, and <c>Time.deltaTime</c> in <c>Update</c> is that clock.
    /// <c>CharacterController.Move</c> is legal outside <c>FixedUpdate</c>, so nothing about
    /// collision requires the physics callback.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(NetMovementAgent))]
    public sealed class NetPredictionClock : MonoBehaviour
    {
        /// <summary>Seconds per simulated tick. Always 1/SIM_TICK_RATE, never a frame delta.</summary>
        public const float TickInterval = MovementSimulation.FixedDeltaTime;

        [Tooltip("Ticks this component will run in a single frame before it gives up and " +
                 "discards the backlog. Without a ceiling, one long hitch makes the next " +
                 "frame longer still and the game never recovers.")]
        public int MaxTicksPerFrame = 5;

        [Tooltip("Ignore Time.timeScale. Leave off so the pause menu and the slow-motion key " +
                 "still stop prediction locally; turn on once a server is driving the clock, " +
                 "because a paused client must not fall behind the server's tick counter.")]
        public bool UseUnscaledTime;

        [Tooltip("Log one line per second with the tick count actually achieved. Useful " +
                 "exactly once, when confirming this really runs at 30 and not at 50 or 60.")]
        public bool LogTickRate;

        private NetMovementAgent _agent;
        private Transform _cameraParent;
        private float _accumulator;

        private int _ticksThisSecond;
        private float _secondTimer;

        /// <summary>Ticks simulated since this component was enabled.</summary>
        public int TickCount { get; private set; }

        /// <summary>
        /// How far the render frame sits between the last simulated tick and the next, in
        /// 0..1. Feed this to any interpolation that has to hide the 30 Hz step.
        /// </summary>
        public float Alpha => Mathf.Clamp01(_accumulator / TickInterval);

        /// <summary>
        /// Where a tick's intent comes from. Defaults to local keyboard and mouse; a replay,
        /// a bot, or a received input frame can replace it without touching this component.
        /// </summary>
        public Func<MoveInput> InputSource;

        private void Awake()
        {
            _agent = GetComponent<NetMovementAgent>();

            // The original derives its forward from the camera rather than the body
            // transform (FirstPersonController.cs:189). Using the body would make the
            // prediction disagree with the game for a reason that is this component's fault.
            Camera cam = GetComponentInChildren<Camera>();
            _cameraParent = cam != null ? cam.transform : transform;

            InputSource = InputSource ?? DefaultInput;
        }

        private void OnEnable()
        {
            _accumulator = 0f;
            _ticksThisSecond = 0;
            _secondTimer = 0f;

            Debug.Log($"[NetPredictionClock] enabled on '{name}' · {ProtocolConstants.SIM_TICK_RATE} Hz " +
                      $"(dt={TickInterval:F5}s), independent of Time.fixedDeltaTime={Time.fixedDeltaTime:F5}");
        }

        private void Update()
        {
            float frame = UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            _accumulator += frame;

            int ticks = 0;
            while (_accumulator >= TickInterval && ticks < MaxTicksPerFrame)
            {
                MoveInput input = InputSource();
                _agent.Tick(in input, TickInterval);

                _accumulator -= TickInterval;
                ticks++;
                TickCount++;
            }

            if (_accumulator >= TickInterval)
            {
                // Discard rather than carry: carrying a backlog forward turns a one-frame
                // hitch into a permanent deficit, and a client that is permanently behind
                // reconciles against the server on every single tick.
                int dropped = Mathf.FloorToInt(_accumulator / TickInterval);
                _accumulator = 0f;
                Debug.LogWarning($"[NetPredictionClock] dropped {dropped} tick(s) after a " +
                                 $"{frame * 1000f:F0} ms frame. Raise MaxTicksPerFrame only if this is routine.");
            }

            if (!LogTickRate) return;

            _ticksThisSecond += ticks;
            _secondTimer += Time.unscaledDeltaTime;
            if (_secondTimer < 1f) return;

            Debug.Log($"[NetPredictionClock] {_ticksThisSecond} ticks in the last {_secondTimer:F2}s " +
                      $"(target {ProtocolConstants.SIM_TICK_RATE})");
            _ticksThisSecond = 0;
            _secondTimer = 0f;
        }

        private MoveInput DefaultInput() => MovementSimulation.FromUnityInput(_cameraParent.eulerAngles.y);
    }
}
