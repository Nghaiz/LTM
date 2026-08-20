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
    /// Implements the decision recorded for checklist item A5 — <b>option B</b>:
    /// the project keeps its physics timestep, and prediction stops riding
    /// <see cref="MonoBehaviour"/>'s <c>FixedUpdate</c>.
    /// </para>
    /// <para>
    /// <b>Why option A could not have worked, whatever anyone chose.</b> Option A was to set
    /// <c>ProjectSettings/TimeManager.asset</c> to 0.0333 and let <c>FixedUpdate</c> be the
    /// tick. That setting did not survive the first frame: <c>IngameMenuUi.Hide()</c>
    /// assigned <c>Time.fixedDeltaTime = Time.timeScale / 60f</c> and was called from
    /// <c>IngameMenuUi.Awake()</c>, and <c>FpsActorController</c> assigned the same expression
    /// again on every slow-motion toggle. Issue #123 routed both through <c>PhysicsRate</c>, so
    /// the live timestep is now the asset's own value scaled by <c>Time.timeScale</c> — but that
    /// is still a rate the pause menu moves, and option A would have made it the simulation's
    /// tick. A tick rate any unrelated file can scale is not a tick rate. This component owns the netcode's clock outright, which is the only arrangement
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

        /// <summary>
        /// Aim pitch, in degrees, as of the last simulated tick. -90..90.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it is here and not on <see cref="MoveInput"/>.</b> Pitch is an aim quantity;
        /// <c>MovementCore</c> never reads it and must not start. But it does have to reach the
        /// wire — <c>ServerCombatAuthority.AimDirection</c> and <c>ShotOrigin</c> both read
        /// <c>InputFrame.PitchDegrees</c> — so the sender needs it, and the sender must have the
        /// value the TICK saw rather than whatever the render frame holds by the time the packet
        /// is built.
        /// </para>
        /// <para>
        /// <b>And not read directly by the sender.</b> <c>ClientPredictionStage</c> lives under
        /// <c>Net/Client/</c>, where the client-wiring gate's G4 rule forbids reaching
        /// <c>FpsActorController.instance</c> without a local-actor guard — the A16 camera-hijack
        /// class. That rule is right, and the answer is to keep the resolution out of
        /// <c>Net/Client/</c> rather than to write a G4 exemption for it.
        /// </para>
        /// </remarks>
        public float AimPitchDegrees { get; private set; }

        /// <summary>
        /// This tick's <c>C_INPUT</c> button bits for fire, aim and reload. Installed by
        /// <c>FpsActorController</c>; null means nothing pressed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Pushed in, not pulled.</b> This component is in <c>Ironfront.Net.Unity.Shared</c>,
        /// an assembly with no references and the one the SERVER assembly builds on. The input
        /// seam (<c>IInputSource</c>, <c>LocalInputSource</c>) is in Assembly-CSharp, a layer
        /// up, so this side cannot name it and the controller installs a delegate instead. A
        /// <c>Func</c> rather than a cached value because the whole seam is deliberately live —
        /// <c>LocalInputSource</c>'s own remark explains why latching a frame's input changes
        /// behaviour.
        /// </para>
        /// <para>
        /// <b>This is the seam a scripted client drives.</b> A programme calls
        /// <c>FpsActorController.SetInputSource</c> and the same tick loop, the same sender and
        /// the same frame layout carry its buttons — Lane B needs no second path. Movement is
        /// scripted through <see cref="InputSource"/> instead, which replaces the whole
        /// <see cref="MoveInput"/> rather than its combat half.
        /// </para>
        /// </remarks>
        public Func<InputButtons> CombatButtonSource;

        /// <summary>Aim pitch in degrees for this tick. Installed alongside
        /// <see cref="CombatButtonSource"/>; null reports level.</summary>
        public Func<float> AimPitchSource;

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

        /// <summary>
        /// The clock driving the local player, or null when none is active.
        /// </summary>
        /// <remarks>
        /// Same reason as <c>ServerTickLoop.Current</c>: the client stages need this every
        /// frame and it lives on the player prefab rather than in the scene, so the alternative
        /// is a per-frame <c>FindFirstObjectByType</c> — the thing phase-04 task 2 forbids.
        /// </remarks>
        public static NetPredictionClock Current { get; private set; }

        /// <summary>
        /// The tick stamped on the next simulated input. Advances with every tick this clock
        /// runs, and is re-seeded from the server's tick at connect.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="TickCount"/>, which counts ticks since this component was
        /// enabled and is a diagnostic. Reconciliation compares against the tick the SERVER
        /// acknowledged, so the number sent on the wire has to share the server's origin —
        /// stamping inputs with a local counter would make every acknowledgement land in the
        /// wrong slot and the correction never converge.
        /// </remarks>
        public uint InputTick { get; private set; }

        /// <summary>
        /// Raised after each simulated tick, with the tick stamped on it and the input applied.
        /// </summary>
        /// <remarks>
        /// This is what lets <c>PredictionReconciler</c> keep the unacknowledged history without
        /// this component knowing the reconciler exists.
        /// </remarks>
        public event Action<uint, MoveInput> OnTickSimulated;

        /// <summary>Re-seeds <see cref="InputTick"/> from the server's clock. Call on connect.</summary>
        public void SeedInputTick(uint serverTick) => InputTick = serverTick;

        private void OnEnable()
        {
            Current = this;
            _accumulator = 0f;
            _ticksThisSecond = 0;
            _secondTimer = 0f;

            Debug.Log($"[NetPredictionClock] enabled on '{name}' · {ProtocolConstants.SIM_TICK_RATE} Hz " +
                      $"(dt={TickInterval:F5}s), independent of Time.fixedDeltaTime={Time.fixedDeltaTime:F5}");
        }

        private void OnDisable()
        {
            // ReferenceEquals, not ==: Unity's overloaded operator reports a destroyed object as
            // null, so a plain comparison during teardown would leave Current pointing at it.
            if (ReferenceEquals(Current, this)) Current = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCurrentOnLoad() => Current = null;

        private void Update()
        {
            float frame = UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            _accumulator += frame;

            int ticks = 0;
            while (_accumulator >= TickInterval && ticks < MaxTicksPerFrame)
            {
                // Sampled with the tick, not when the sender gets round to it. Two reads a
                // frame apart is how a shot leaves along a direction the player was not
                // looking in, and it is unreproducible when it happens.
                AimPitchDegrees = AimPitchSource != null ? AimPitchSource() : 0f;

                MoveInput input = InputSource();
                _agent.Tick(in input, TickInterval);

                // Unchecked: a u32 tick at 30 Hz wraps after 4.5 years, and every comparison
                // downstream uses SequenceMath.IsNewer32, which handles the wrap.
                InputTick = unchecked(InputTick + 1);
                OnTickSimulated?.Invoke(InputTick, input);

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

        private MoveInput DefaultInput()
            => MovementSimulation.FromUnityInput(
                _cameraParent.eulerAngles.y,
                CombatButtonSource != null ? CombatButtonSource() : InputButtons.None);
    }
}
