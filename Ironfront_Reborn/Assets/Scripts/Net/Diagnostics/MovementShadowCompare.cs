using Ironfront.Net.Replication.Movement;
using UnityEngine;
using UnityStandardAssets.Characters.FirstPerson;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Runs the shared movement simulation alongside the original code and logs where the two
    /// disagree. Nothing it computes is ever applied to the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes replication phase-00 acceptance criterion 8 (task 5.2 steps
    /// 1-2), and is the safety strategy for risk C7 — replacing 1188 lines of someone else's
    /// working movement code in one commit is how you break gameplay in a way nobody can
    /// bisect.
    /// </para>
    /// <para>
    /// <b>Read-only by construction.</b> There is no code path here that writes to the
    /// <see cref="CharacterController"/>, the transform, or any Actor state. The shadow
    /// velocity is integrated in a local field. Deleting this component changes nothing about
    /// how the game plays, which is what makes it safe to leave attached.
    /// </para>
    /// <para>
    /// <b>How to use it.</b> Attach to the player prefab, press Play, walk/run/jump/crouch
    /// around for a few minutes, and read the Console. Then read the summary printed on exit.
    /// Expect divergence on slopes and against geometry — those are the two known, documented
    /// gaps in the port (docs/movement-analysis.md § 5). Divergence on <i>flat open ground</i>
    /// is a real bug and worth stopping for.
    /// </para>
    /// <para>
    /// <b>What this harness compares, and what it deliberately does not.</b> Round 5 of the
    /// the client track playtest (plans/reports/2026-08-13-unity-a3-shadow-harness.md) reported 87.4 % of
    /// ticks as divergent and the measurement was not usable, because the harness was comparing
    /// two quantities that are not the same kind of thing:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>The vertical channel while grounded belongs to collision, not to the simulation.</b>
    /// <see cref="MovementCore.Step"/> requests <c>-StickToGroundForce * dt</c> downwards every
    /// grounded tick by design; <c>CharacterController.Move</c> then resolves that request
    /// against the floor and the actor does not descend. Scoring "requested -0.167 m, moved
    /// 0 m" as divergence flagged 787 ticks of an idle player standing correctly still. The
    /// grounded vertical channel is therefore counted as absorbed-by-collision and excluded
    /// from the verdict. Airborne vertical IS scored — there, gravity integration is exactly
    /// what is under test.
    /// </item>
    /// <item>
    /// <b>Spawn, respawn and teleport are not locomotion.</b> A 1123 m relocation entered the
    /// mean and became the reported worst sample. Any real delta larger than one tick of
    /// legitimate motion is now treated as a discontinuity: the shadow re-syncs and the sample
    /// is skipped rather than scored.
    /// </item>
    /// <item>
    /// <b>Tick alignment is now declared, not hoped for.</b> With no execution order the
    /// harness could sample a transform the original had not moved yet that tick, comparing a
    /// real delta from tick N-1 against shadow motion for tick N. The
    /// <see cref="DefaultExecutionOrder"/> below pins this component to run after the default
    /// batch, so <c>transform.position</c> is always read after
    /// <c>FirstPersonController.FixedUpdate</c> has already moved for the same tick.
    /// </item>
    /// </list>
    /// </remarks>
    // Ordering, not cosmetics: the whole comparison is invalid if this samples the transform
    // before the original controller has moved it this tick. Standard Assets' controller sits
    // at the default order of 0, so any positive value runs strictly after it.
    [DefaultExecutionOrder(1000)]
    public sealed class MovementShadowCompare : MonoBehaviour
    {
        [Tooltip("Metres of disagreement per tick before a warning is logged.")]
        public float WarnThreshold = 0.01f;

        [Tooltip("Re-sync the shadow to the real position every N ticks, so error does not " +
                 "simply accumulate forever and drown the signal.")]
        public int ResyncEveryTicks = 30;

        [Tooltip("Log every tick's delta, not just the ones past the threshold. Very noisy.")]
        public bool VerboseLogging;

        /// <summary>
        /// Multiplier on the fastest single tick a player can legitimately produce. Anything
        /// past that is a teleport, not movement.
        /// </summary>
        /// <remarks>
        /// The bound is derived, not guessed: full run speed horizontally plus a generous
        /// vertical allowance for a long fall, times the tick length, times this margin. The
        /// margin exists so that a fast lift, a steep slide or one late physics frame is not
        /// mistaken for a respawn — the cost of a false teleport is a skipped sample, the cost
        /// of a missed one is another 1123 m entry in the mean.
        /// </remarks>
        [Tooltip("Safety margin on the largest plausible single-tick move before it is " +
                 "treated as a teleport and skipped.")]
        public float DiscontinuityMargin = 4f;

        private CharacterController _controller;
        private FirstPersonController _legacyController;
        private Transform _cameraParent;
        private MoveState _shadow;
        private Vector3 _previousRealPosition;

        private int _ticks;
        private int _skippedDiscontinuities;

        // Horizontal is the criterion the port is actually judged on: it is the only channel
        // where MovementCore and the original are both fully responsible for the answer.
        private int _groundedTicks;
        private int _groundedDiverged;
        private int _airborneTicks;
        private int _airborneDiverged;

        private float _worstHorizontal;
        private float _totalHorizontal;
        private float _worstAirborneVertical;

        private bool _primed;
        private bool _reported;

        /// <summary>
        /// The largest distance one tick of legitimate movement can cover, before the margin.
        /// </summary>
        private static float MaxPlausibleTickDistance(float dt)
        {
            // Horizontal is bounded by run speed. Vertical is not bounded by jump speed — a
            // fall accelerates — so allow a terminal-velocity-ish 60 m/s downward.
            const float terminalFallSpeed = 60f;
            float horizontal = MovementSimulation.RunSpeed * dt;
            float vertical   = terminalFallSpeed * dt;
            return Mathf.Sqrt(horizontal * horizontal + vertical * vertical);
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (_controller == null)
            {
                Debug.LogWarning(
                    $"[MovementShadowCompare] no CharacterController on '{name}'. Ground contact " +
                    "will be read as false for every tick and the comparison will be meaningless. " +
                    "Attach this to Assets/Prefab/Player Fps Actor.prefab, the only prefab that has " +
                    "both FpsActorController and CharacterController.");
            }

            _legacyController = GetComponent<FirstPersonController>();
            if (_legacyController == null)
            {
                Debug.LogWarning(
                    $"[MovementShadowCompare] no FirstPersonController on '{name}'. The harness " +
                    "cannot observe the effective input used by legacy movement and will not " +
                    "score this run.");
            }

            // The original derives its forward from the camera, not the body transform
            // (FirstPersonController.cs:189). Using the body would introduce a divergence that
            // is an artefact of this harness rather than of the port.
            Camera camera = GetComponentInChildren<Camera>();
            _cameraParent = camera != null ? camera.transform : transform;
        }

        private void OnEnable()
        {
            // Says "I am here and I am running" before any measurement exists to report.
            // Without this line the component is indistinguishable, in the Console, from a
            // component that was never attached — which is the failure that actually happened
            // and cost a playtest: the summary below early-returns at zero ticks, so a
            // harness sitting on the wrong GameObject stays completely silent.
            Debug.Log($"[MovementShadowCompare] attached to '{name}' and ticking. " +
                      "Play, move around, then stop Play — the summary line prints on exit.");

            _reported = false;

            // Dropping the prime on every enable is what makes a pooled respawn safe. Leaving
            // it set meant the first tick after re-enable measured against wherever the actor
            // stood before it was pooled, which is a whole-map delta arriving as movement.
            _primed = false;
            Resync();
        }

        private void FixedUpdate()
        {
            Vector3 realPosition = transform.position;

            // The prefab exists before the player is deployed. During that interval the legacy
            // input and CharacterController are disabled, so a stationary airborne-looking
            // transform is lifecycle state, not locomotion. Do not let those ticks pollute A3.
            if (!IsReadyToScore())
            {
                _primed = false;
                Resync();
                return;
            }

            if (!_primed)
            {
                _primed = true;
                _previousRealPosition = realPosition;
                Resync();
                return;
            }

            // Step the shadow with the same input and the same dt the original just used. This
            // deliberately uses Time.fixedDeltaTime rather than the 1/30 the netcode will run
            // at: the point is to compare against what the original ACTUALLY did this frame,
            // and the original used the project's fixed timestep.
            float dt = Time.fixedDeltaTime;
            MoveInput sampledInput = MovementSimulation.FromUnityInput(_cameraParent.eulerAngles.y);

            // FpsActorController.Update latches the effective sprint decision here before the
            // legacy FirstPersonController consumes it in FixedUpdate. Reading the raw button
            // again after that move can see a newer render-frame value; PR #42 caught exactly
            // two such physics ticks. Preserve every other live field, but compare both systems
            // with the sprint state the observed movement actually used.
            MoveInput input = new MoveInput(
                sampledInput.MoveX,
                sampledInput.MoveZ,
                sampledInput.YawDegrees,
                sampledInput.Jump,
                _legacyController.sprinting,
                sampledInput.Crouch);

            bool grounded = _controller != null && _controller.isGrounded;
            _shadow.IsGrounded = grounded;
            Vector3 shadowMotion = MovementSimulation.Step(ref _shadow, in input, dt);

            // Compare the DELTA, not the absolute position. Absolute positions drift apart the
            // moment collision moves the real actor and the shadow keeps flying, which says
            // nothing about whether the simulation agrees.
            Vector3 realDelta = realPosition - _previousRealPosition;
            _previousRealPosition = realPosition;

            // A discontinuity is scored as nothing at all. Re-sync and move on — including the
            // shadow's velocity, so the tick after a respawn does not inherit a stale fall.
            if (realDelta.magnitude > MaxPlausibleTickDistance(dt) * DiscontinuityMargin)
            {
                _skippedDiscontinuities++;
                Resync();
                if (VerboseLogging)
                {
                    Debug.Log($"[MovementShadowCompare] discontinuity skipped: moved " +
                              $"{realDelta.magnitude:F1}m in one tick — spawn, respawn or teleport, " +
                              "not locomotion. Shadow re-synced.");
                }
                return;
            }

            _ticks++;

            // Horizontal: both sides are fully responsible for this, so it is always scored.
            Vector2 realHorizontal   = new Vector2(realDelta.x, realDelta.z);
            Vector2 shadowHorizontal = new Vector2(shadowMotion.x, shadowMotion.z);
            float horizontal = Vector2.Distance(realHorizontal, shadowHorizontal);

            _totalHorizontal += horizontal;
            if (horizontal > _worstHorizontal) _worstHorizontal = horizontal;

            // Vertical: only meaningful while airborne. Grounded, the simulation's downward
            // stick force is a REQUEST that collision is supposed to absorb, and an actor that
            // does not sink into the floor is the correct outcome, not a divergence.
            float vertical = Mathf.Abs(realDelta.y - shadowMotion.y);
            bool verticalCounts = !grounded;
            if (verticalCounts && vertical > _worstAirborneVertical) _worstAirborneVertical = vertical;

            bool diverged = horizontal > WarnThreshold ||
                            (verticalCounts && vertical > WarnThreshold);

            if (grounded)
            {
                _groundedTicks++;
                if (diverged) _groundedDiverged++;
            }
            else
            {
                _airborneTicks++;
                if (diverged) _airborneDiverged++;
            }

            if (diverged)
            {
                Debug.LogWarning(
                    $"MOVEMENT DIVERGED tick={_ticks} dH={horizontal:F4}m " +
                    $"dV={(verticalCounts ? vertical.ToString("F4") + "m" : "absorbed")} " +
                    $"real={realDelta} shadow={shadowMotion} " +
                    $"grounded={grounded} input=({input.MoveX:F2},{input.MoveZ:F2}) " +
                    $"sprint={input.Sprint} jump={input.Jump} crouch={input.Crouch}");
            }
            else if (VerboseLogging)
            {
                Debug.Log($"movement ok tick={_ticks} dH={horizontal:F5}m");
            }

            if (ResyncEveryTicks > 0 && _ticks % ResyncEveryTicks == 0) Resync();
        }

        private bool IsReadyToScore()
            => _controller != null
               && _controller.enabled
               && _legacyController != null
               && _legacyController.enabled
               && _legacyController.inputEnabled;

        private void Resync()
        {
            _shadow = MoveState.AtRest(
                MovementSimulation.ToCore(transform.position),
                grounded: _controller == null || _controller.isGrounded);

            // Carry the real velocity across so a resync mid-jump does not register as a
            // divergence on the next tick.
            if (_controller != null) _shadow.Velocity = MovementSimulation.ToCore(_controller.velocity);
        }

        private void OnDisable() => Report();

        // OnDisable is not guaranteed to run when a built player exits; OnApplicationQuit is.
        // In the Editor both fire, hence the _reported latch.
        private void OnApplicationQuit() => Report();

        private void Report()
        {
            if (_reported) return;
            _reported = true;

            if (_ticks == 0)
            {
                // Silence here used to be the whole bug report: "no logs". It is not a
                // logging failure, it is this harness never having been stepped, and saying
                // so turns an afternoon of looking at the logger into a ten-second fix.
                Debug.LogWarning(
                    $"[MovementShadowCompare] on '{name}' ran zero ticks, so there is nothing to " +
                    "report. FixedUpdate never fired: the component is on a GameObject that was " +
                    "not spawned, was disabled the whole session, or is on the scene object rather " +
                    "than on Assets/Prefab/Player Fps Actor.prefab.");
                return;
            }

            int diverged = _groundedDiverged + _airborneDiverged;
            float mean = _totalHorizontal / _ticks;

            // The grounded number is the verdict. Airborne divergence is informative but is
            // also where the two documented port gaps live, so it does not by itself condemn
            // MovementCore; a grounded horizontal disagreement has no innocent explanation.
            string verdict = _groundedDiverged == 0
                ? "CLEAN on the ground — the port agrees with the original on every grounded tick observed"
                : $"{_groundedDiverged} of {_groundedTicks} GROUNDED ticks diverged " +
                  $"({(float)_groundedDiverged / Mathf.Max(1, _groundedTicks):P1})";

            Debug.Log(
                $"[MovementShadowCompare] {verdict}. " +
                $"airborne {_airborneDiverged}/{_airborneTicks} diverged. " +
                $"scored={_ticks} skipped_discontinuities={_skippedDiscontinuities} " +
                $"total_diverged={diverged}. " +
                $"meanH={mean:F5}m worstH={_worstHorizontal:F4}m " +
                $"worstV_airborne={_worstAirborneVertical:F4}m threshold={WarnThreshold:F3}m. " +
                "The grounded vertical channel is excluded on purpose: MovementCore requests a " +
                "downward stick-to-ground force that CharacterController.Move is supposed to " +
                "absorb, so an actor standing still on flat ground is agreement, not divergence. " +
                "Divergence on slopes and against geometry is expected and documented " +
                "(docs/movement-analysis.md section 5); horizontal divergence on flat open " +
                "ground is not.");
        }
    }
}
