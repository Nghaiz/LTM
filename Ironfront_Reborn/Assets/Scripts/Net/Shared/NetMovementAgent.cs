using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The seam between the shared movement simulation and Unity's collision system. Add one
    /// to the player prefab, on the same GameObject as its <see cref="CharacterController"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this component exists instead of six new members on <c>Actor</c>.</b> The
    /// phase-00 plan asks the client track to expose <c>NetVelocity</c>, <c>IsGrounded</c> and
    /// <c>CharacterMove</c> on <c>Actor.cs</c>. That request was written on the assumption
    /// that <c>Actor</c> owns movement. It does not — see docs/movement-analysis.md § 0. All
    /// three would be pass-throughs on a 1188-line file A owns, forwarding to a controller
    /// that forwards to the <see cref="CharacterController"/> this component talks to
    /// directly.
    /// </para>
    /// <para>
    /// One added component beats six edits to someone else's file: it is a smaller ask, it
    /// cannot regress existing gameplay because nothing calls it until wired, and it removes
    /// a cross-owner dependency from the critical path.
    /// </para>
    /// <para>
    /// This component performs no simulation of its own. It holds state and applies motion;
    /// every rule lives in <see cref="MovementCore"/>.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(CharacterController))]
    public sealed class NetMovementAgent : MonoBehaviour
    {
        private CharacterController _controller;

        /// <summary>The simulation state. Shared verbatim by prediction and authority.</summary>
        public MoveState State;

        /// <summary>Collision flags from the last <see cref="Move"/>, for callers that care.</summary>
        public CollisionFlags LastCollisionFlags { get; private set; }

        private void Awake()
        {
            State = MoveState.AtRest(MovementSimulation.ToCore(transform.position),
                                     grounded: IsGrounded);
        }

        /// <summary>
        /// The collision capsule this agent moves through, resolved on first use.
        /// </summary>
        /// <remarks>
        /// <b>Lazy rather than cached in <see cref="Awake"/>, because <c>Awake</c> is not a
        /// guarantee.</b> It does not run at all in an EditMode test, and it has already run by
        /// the time anything adds a controller to a body that was built without one -- in both
        /// cases the cached field is a permanent null and every move silently takes the
        /// uncollided branch, which is the exact failure X-19 is about. `GetComponent` runs once
        /// per body in the normal case and the null-check is a field read on every tick after
        /// that.
        /// </remarks>
        private CharacterController Controller
            => _controller != null ? _controller : (_controller = GetComponent<CharacterController>());

        /// <summary>Velocity as the simulation believes it. Not the CharacterController's.</summary>
        /// <remarks>
        /// The two differ, and the difference is the point: <c>CharacterController.velocity</c>
        /// reports what actually happened after collision resolution, while this is what the
        /// simulation intended. Prediction and reconciliation need the intent, or a player
        /// walking into a wall would have their predicted velocity zeroed by geometry the
        /// server has not told them about yet.
        /// </remarks>
        public Vector3 NetVelocity
        {
            get => MovementSimulation.ToUnity(State.Velocity);
            set => State.Velocity = MovementSimulation.ToCore(value);
        }

        /// <summary>Ground contact, straight from the CharacterController.</summary>
        public bool IsGrounded => Controller != null && Controller.isGrounded;

        /// <summary>Crouch stance, as the simulation last saw it.</summary>
        public bool IsCrouching => State.IsCrouching;

        /// <summary>
        /// Moves through the collision system that is supposed to resolve the motion, and
        /// counts every time it could not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The bypass used to be silent, and it cost X-19.</b> When the
        /// <see cref="CharacterController"/> is absent or disabled this method has no way to
        /// resolve the motion, so it writes the delta straight onto the transform -- no sweep,
        /// no floor, no collision flags -- and then returns <c>transform.position</c> exactly as
        /// it would after a real move. <see cref="Tick"/> stores that as
        /// <see cref="MoveState.Position"/>, so the actor's own record of where it is agrees
        /// with a position collision never validated, and nothing anywhere reports it.
        /// </para>
        /// <para>
        /// Measured on <c>artifacts/lane-b/x19-move</c>: <b>11,785 of 11,785</b> client ticks
        /// took this branch, on all three clients, for the whole run -- the local body's
        /// controller is disabled by <c>FpsActorController.Start</c> and, on a networked client,
        /// nothing ever re-enables it. Each of those ticks sank the drawn body 0.332 m below the
        /// position the server held, which is why every shot at six metres passed over the
        /// target's head. Two runs and three weeks of investigation could not see it, because
        /// the one line that knew was the one that said nothing.
        /// </para>
        /// <para>
        /// <b>Counted rather than thrown, and logged once rather than every tick.</b> Throwing
        /// would take down a client for a condition it can still limp through, and a per-tick
        /// error at 30 Hz buries the log it is trying to write. The counter is the durable
        /// signal: <see cref="CollisionBypassedMoves"/> is <c>0</c> on a healthy body and rises
        /// monotonically on a broken one, so a run can be graded on it without reading a log at
        /// all. That is what makes this a detector and not a comment.
        /// </para>
        /// </remarks>
        public Vector3 CharacterMove(Vector3 delta)
        {
            CharacterController controller = Controller;

            if (controller != null && controller.enabled)
            {
                LastCollisionFlags = controller.Move(delta);
                return transform.position;
            }

            CollisionBypassedMoves++;

            // Zeroed, not left stale: a caller reading Below off a move that never touched the
            // collision system would conclude the actor is standing on something.
            LastCollisionFlags = CollisionFlags.None;

            if (CollisionBypassedMoves == 1)
            {
                Debug.LogError(
                    $"[net] '{name}' moved with no collision: its CharacterController is "
                    + (controller == null ? "missing" : "disabled")
                    + ". Motion is being written straight onto the transform, so this body will "
                    + "pass through the world and sink below the server's authoritative "
                    + "position. See ledger X-19. Further occurrences are counted in "
                    + "CollisionBypassedMoves and not logged.");
            }

            transform.position += delta;
            return transform.position;
        }

        /// <summary>
        /// Moves this agent applied WITHOUT collision, because the controller was missing or
        /// disabled. Zero on a healthy body; the X-19 detector.
        /// </summary>
        public long CollisionBypassedMoves { get; private set; }

        /// <summary>
        /// One authoritative or predicted tick: step the shared simulation, apply the motion
        /// through collision, and write back where the actor really is.
        /// </summary>
        /// <param name="dt">
        /// Use <see cref="MovementSimulation.FixedDeltaTime"/>, NOT
        /// <c>Time.fixedDeltaTime</c> — the project's fixed timestep is 1/60 and the
        /// simulation runs at 1/30. Two clocks, deliberately.
        /// </param>
        public void Tick(in MoveInput input, float dt)
        {
            State.IsGrounded = IsGrounded;

            Vector3 before = transform.position;
            Vector3 motion = MovementSimulation.Step(ref State, in input, dt);
            Vector3 landed = CharacterMove(motion);

            State.Position = MovementSimulation.ToCore(landed);
            ApplyStanceHeight();

            LogTick(before, motion, landed);
        }

        /// <summary>
        /// One line per simulated tick, when <c>IRONFRONT_LOG_MOVE=1</c>. Silent otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>X-19, and the half a checkpoint cannot see.</b> The lane-B record samples the
        /// transform seven times a run and reports it against the snapshot; that is enough to
        /// prove the client's body sits below the server's authority and not enough to say what
        /// put it there. The vertical channel is decided here, in three steps that a checkpoint
        /// collapses into one number: what the simulation ASKED for (<c>motion</c>), what
        /// collision GRANTED (<c>landed - before</c>), and whether the controller was even
        /// consulted (<c>ctrl</c>). A tick where those three disagree names the line; a
        /// checkpoint where they agree cannot.
        /// </para>
        /// <para>
        /// <b>The capsule is printed with them.</b> <c>CharacterController.height</c> is written
        /// every tick by <see cref="ApplyStanceHeight"/> while <c>center</c> is never touched,
        /// so a stance change moves the capsule's FEET without moving the transform -- and the
        /// body then falls or floats by half the height step with nothing in the movement
        /// simulation to account for it. Printing height and centre beside the motion is what
        /// makes that visible rather than inferred.
        /// </para>
        /// <para>
        /// <b>Off by default, and by an env var rather than a define</b>, matching the shot log
        /// (<c>IRONFRONT_LOG_SHOTS</c>): a built player can be asked the question without a
        /// rebuild. At 30 Hz this is a busy line, which is why nothing but an explicit opt-in
        /// turns it on.
        /// </para>
        /// </remarks>
        private void LogTick(in Vector3 before, in Vector3 motion, in Vector3 landed)
        {
            if (!MoveLoggingEnabled) return;

            float granted = landed.y - before.y;
            bool viaController = _controller != null && _controller.enabled;

            Debug.Log(
                $"[move] role={NetContext.Role} obj={name} "
                + $"pre={before.y:F4} asked={motion.y:F4} granted={granted:F4} post={landed.y:F4} "
                + $"grounded={State.IsGrounded} crouch={State.IsCrouching} "
                + $"velY={State.Velocity.Y:F3} flags={LastCollisionFlags} "
                + $"ctrl={viaController} height={(_controller != null ? _controller.height : -1f):F3} "
                + $"centerY={(_controller != null ? _controller.center.y : -1f):F3} "
                + $"radius={(_controller != null ? _controller.radius : -1f):F3} "
                + $"skin={(_controller != null ? _controller.skinWidth : -1f):F3}");
        }

        /// <summary>Read once: this is consulted on every simulated tick.</summary>
        private static bool MoveLoggingEnabled =>
            _moveLogging ??= System.Environment.GetEnvironmentVariable("IRONFRONT_LOG_MOVE") == "1";

        private static bool? _moveLogging;

        /// <summary>
        /// Adopts a state some other component has already simulated, without simulating
        /// again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The server drives movement through <c>InputAuthority.ApplyPendingInput</c>, which
        /// owns the authoritative <see cref="MoveState"/> on the session and calls back into
        /// <see cref="CharacterMove"/> for collision. That leaves this component holding a
        /// stale copy of a state it did not produce — harmless for position, which the
        /// CharacterController already moved, but not for the stance height, which is derived
        /// from <see cref="MoveState.IsCrouching"/> and would never change.
        /// </para>
        /// <para>
        /// Calling <see cref="Tick"/> instead would step the simulation a second time for the
        /// same input, which is why this exists rather than being folded into it.
        /// </para>
        /// </remarks>
        public void ApplyAuthoritativeState(in MoveState state)
        {
            State = state;
            ApplyStanceHeight();
        }

        /// <summary>
        /// Adopts a state the SERVER produced, and moves the body to it. The client's
        /// counterpart to <see cref="ApplyAuthoritativeState"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two callers, opposite preconditions, and that is ledger row X-13.</b>
        /// <see cref="ApplyAuthoritativeState"/> is correct for the server because
        /// <c>InputAuthority.ApplyPendingInput</c> has already driven the CharacterController --
        /// its own remarks say the position half is "harmless ... which the CharacterController
        /// already moved". On a client nothing has moved anything: the reconciler hands back a
        /// corrected state and the body stays exactly where it was. Measured 2026-08-21 on
        /// `combat-driver`: `corrections: 88`, `resyncs: 1`, `inSnapshot: true`,
        /// `authoritative (1603.1, 42.3, 1437.7)` -- and the rendered body at `x=0, z=0`,
        /// falling. Eighty-eight corrections were computed, accepted, and dropped on the floor.
        /// </para>
        /// <para>
        /// <b>A resync teleports; a correction moves through collision.</b> A resync is the
        /// server saying "you are not where you think you are" by more than
        /// <c>PredictionReconciler.PositionToleranceMetres</c> can absorb, and pushing that
        /// through <c>CharacterController.Move</c> would sweep the body across the level and
        /// snag it on the first wall. An ordinary correction is small and MUST resolve through
        /// collision, or the client would walk itself into geometry the server has not told it
        /// about yet.
        /// </para>
        /// <para>
        /// <b>Velocity is not reset on the teleport.</b> The server just sent one; discarding it
        /// would stall a player mid-fall and hand the next tick a velocity of zero to predict
        /// from.
        /// </para>
        /// </remarks>
        /// <param name="hardSnap">
        /// True for <c>ReconcileResult.Resynchronised</c>, false for <c>Corrected</c>.
        /// </param>
        public void ApplyCorrectedState(in MoveState state, bool hardSnap)
        {
            State = state;

            Vector3 target = MovementSimulation.ToUnity(state.Position);

            Vector3 before = transform.position;

            if (hardSnap)
            {
                Teleport(target, resetVelocity: false);
            }
            else
            {
                CharacterMove(target - transform.position);
                State.Position = MovementSimulation.ToCore(transform.position);
            }

            ApplyStanceHeight();

            // The other half of the X-19 pair. LogTick says where a PREDICTED tick left the
            // body; this says where the CORRECTION put it, and whether the body it was asked to
            // reach is one collision would let it hold. A correction that lands exactly on the
            // authoritative y and a next tick that leaves it a third of a metre lower are two
            // lines, and only together do they show a sawtooth rather than a drift.
            if (MoveLoggingEnabled)
            {
                Debug.Log(
                    $"[move-correct] hardSnap={hardSnap} obj={name} "
                    + $"pre={before.y:F4} wanted={target.y:F4} post={transform.position.y:F4} "
                    + $"grounded={IsGrounded} crouch={State.IsCrouching} "
                    + $"height={(_controller != null ? _controller.height : -1f):F3}");
            }
        }

        /// <summary>Teleports the actor, used on spawn and on a hard server correction.</summary>
        public void Teleport(Vector3 position, bool resetVelocity = true)
        {
            bool wasEnabled = Controller != null && Controller.enabled;

            // The CharacterController must be disabled around a direct transform write, or it
            // fights the assignment and the actor lands somewhere else.
            if (wasEnabled) _controller.enabled = false;

            transform.position = position;

            if (wasEnabled) _controller.enabled = true;

            State.Position = MovementSimulation.ToCore(position);
            if (resetVelocity) State.Velocity = Vec3.Zero;
        }

        private void ApplyStanceHeight()
        {
            if (Controller == null) return;

            float wanted = MovementCore.HeightFor(State.IsCrouching);
            if (!Mathf.Approximately(_controller.height, wanted)) _controller.height = wanted;
        }
    }
}
