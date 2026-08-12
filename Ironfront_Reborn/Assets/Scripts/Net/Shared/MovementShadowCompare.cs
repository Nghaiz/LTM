using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Runs the shared movement simulation alongside the original code and logs where the two
    /// disagree. Nothing it computes is ever applied to the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C. Closes dev-c-replication phase-00 acceptance criterion 8 (task 5.2 steps
    /// 1-2), and is the safety strategy for risk C7 — replacing 1188 lines of someone else's
    /// working movement code in one commit is how you break gameplay in a way nobody can
    /// bisect.
    /// </para>
    /// <para>
    /// <b>Read-only by construction.</b> There is no code path here that writes to the
    /// <see cref="CharacterController"/>, the transform, or any Actor state. The shadow
    /// position is integrated in a local field. Deleting this component changes nothing about
    /// how the game plays, which is what makes it safe to leave attached.
    /// </para>
    /// <para>
    /// <b>How to use it.</b> Attach to the player prefab, press Play, walk/run/jump/crouch
    /// around for a few minutes, and read the Console. Then read the summary printed on exit.
    /// Expect divergence on slopes and against geometry — those are the two known, documented
    /// gaps in the port (docs/movement-analysis.md § 5). Divergence on <i>flat open ground</i>
    /// is a real bug and worth stopping for.
    /// </para>
    /// </remarks>
    public sealed class MovementShadowCompare : MonoBehaviour
    {
        [Tooltip("Metres of disagreement per tick before a warning is logged.")]
        public float WarnThreshold = 0.01f;

        [Tooltip("Re-sync the shadow to the real position every N ticks, so error does not " +
                 "simply accumulate forever and drown the signal.")]
        public int ResyncEveryTicks = 30;

        [Tooltip("Log every tick's delta, not just the ones past the threshold. Very noisy.")]
        public bool VerboseLogging;

        private CharacterController _controller;
        private Transform _cameraParent;
        private MoveState _shadow;
        private Vector3 _shadowPosition;
        private Vector3 _previousRealPosition;

        private int _ticks;
        private int _divergences;
        private float _worstDivergence;
        private float _totalDivergence;
        private bool _primed;
        private bool _reported;

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
            Resync();
        }

        private void FixedUpdate()
        {
            Vector3 realPosition = transform.position;

            if (!_primed)
            {
                _primed = true;
                _previousRealPosition = realPosition;
                return;
            }

            // Step the shadow with the same input and the same dt the original just used. This
            // deliberately uses Time.fixedDeltaTime rather than the 1/30 the netcode will run
            // at: the point is to compare against what the original ACTUALLY did this frame,
            // and the original used the project's fixed timestep.
            float dt = Time.fixedDeltaTime;
            MoveInput input = MovementSimulation.FromUnityInput(_cameraParent.eulerAngles.y);

            _shadow.IsGrounded = _controller != null && _controller.isGrounded;
            Vector3 shadowMotion = MovementSimulation.Step(ref _shadow, in input, dt);
            _shadowPosition += shadowMotion;

            // Compare the DELTA, not the absolute position. Absolute positions drift apart the
            // moment collision moves the real actor and the shadow keeps flying, which says
            // nothing about whether the simulation agrees.
            Vector3 realDelta = realPosition - _previousRealPosition;
            float divergence = Vector3.Distance(realDelta, shadowMotion);

            _ticks++;
            _totalDivergence += divergence;
            if (divergence > _worstDivergence) _worstDivergence = divergence;

            if (divergence > WarnThreshold)
            {
                _divergences++;
                Debug.LogWarning(
                    $"MOVEMENT DIVERGED tick={_ticks} d={divergence:F4}m " +
                    $"real={realDelta} shadow={shadowMotion} " +
                    $"grounded={_shadow.IsGrounded} input=({input.MoveX:F2},{input.MoveZ:F2}) " +
                    $"sprint={input.Sprint} jump={input.Jump} crouch={input.Crouch}");
            }
            else if (VerboseLogging)
            {
                Debug.Log($"movement ok tick={_ticks} d={divergence:F5}m");
            }

            _previousRealPosition = realPosition;

            if (ResyncEveryTicks > 0 && _ticks % ResyncEveryTicks == 0) Resync();
        }

        private void Resync()
        {
            _shadowPosition = transform.position;
            _shadow = MoveState.AtRest(
                MovementSimulation.ToCore(_shadowPosition),
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

            float mean = _totalDivergence / _ticks;
            string verdict = _divergences == 0
                ? "CLEAN — the port agrees with the original on every tick observed"
                : $"{_divergences} of {_ticks} ticks diverged ({(float)_divergences / _ticks:P1})";

            Debug.Log(
                $"[MovementShadowCompare] {verdict}. " +
                $"mean={mean:F5}m worst={_worstDivergence:F4}m threshold={WarnThreshold:F3}m. " +
                "Divergence on slopes and against geometry is expected and documented " +
                "(docs/movement-analysis.md section 5); divergence on flat open ground is not.");
        }
    }
}
