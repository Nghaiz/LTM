// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
//
// Nothing outside Assets/Scripts/Net/Diagnostics/ names a type from this folder: the ten
// mentions elsewhere are doc-comments, checked 2026-08-21. So this guard needs no companion
// guard at any call site, and a strip cannot leave a dangling reference behind it.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// An <see cref="IInputSource"/> whose answers come from a recorded programme instead of a
    /// keyboard. Phase-3D lane B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of lane B's "no test-only path".</b> Handed to
    /// <c>FpsActorController.SetInputSource</c> it becomes the source every gameplay read
    /// already goes through — firing, aiming, reloading, and vehicle control by way of
    /// <c>ClientVehicleStage</c>, which reads <c>_localController.InputSource</c> directly.
    /// Nothing under test learns that a script is driving.
    /// </para>
    /// <para>
    /// <b>Movement does NOT arrive through here, and that is not an oversight.</b> Walking is
    /// read by <c>FirstPersonController</c> under <c>Assets/Plugins/</c> and the netcode's own
    /// movement intent is built by <c>MovementSimulation.FromUnityInput</c>, which samples
    /// <c>Input.GetAxis</c> directly — <c>IInputSource.MoveX</c>/<c>MoveZ</c> exist for swimming
    /// and vehicle steering inside <c>FpsActorController</c>, not for locomotion. So the driver
    /// scripts movement by replacing <c>NetPredictionClock.InputSource</c> wholesale, exactly as
    /// that field's own remark prescribes, and this class supplies the halves the controller
    /// owns. Two seams because there are two owners; see <c>NetPredictionClock.CombatButtonSource</c>.
    /// </para>
    /// <para>
    /// <b>No mouse delta.</b> <see cref="LookDeltaX"/> and <see cref="LookDeltaY"/> report 0 for
    /// the same reason <c>NetInputSource</c> does: a programme states an absolute facing, and a
    /// per-frame delta is a different quantity an absolute-angle record cannot express. A
    /// scripted helicopter is therefore out of scope here, and no check in
    /// <c>phase-3-harness.md</c> § 2 asks for one.
    /// </para>
    /// </remarks>
    public sealed class ScriptedInputSource : IInputSource
    {
        private readonly ScriptedInputCursor _cursor;
        private readonly ScriptedTargetSolver _solver;

        /// <param name="solver">
        /// Optional. Without one, a step's <c>aimAtPlayer</c> is inert and the programme's
        /// declared yaw and pitch stand — which is what the unit tests exercise, since the
        /// solver needs a live scene to resolve anything.
        /// </param>
        public ScriptedInputSource(ScriptedInputCursor cursor, ScriptedTargetSolver solver = null)
        {
            _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
            _solver = solver;
        }

        private ScriptedInputStep Step => _cursor.Current;

        public float MoveX => Step != null ? Step.moveX : 0f;

        public float MoveZ => Step != null ? Step.moveZ : 0f;

        /// <summary>
        /// The solved facing when the step names a target, else the cursor's integrated one —
        /// never the step's declared yaw, which the cursor has already absorbed.
        /// </summary>
        public float Yaw
        {
            get
            {
                ScriptedTargetSolver.Solution aim = Aim();
                return aim.Resolved ? aim.Yaw : _cursor.Yaw;
            }
        }

        public float Pitch
        {
            get
            {
                ScriptedTargetSolver.Solution aim = Aim();
                if (aim.Resolved) return aim.Pitch;
                return Step != null ? Step.pitchDegrees : 0f;
            }
        }

        /// <summary>
        /// The live step's target solution, or an unresolved one when the step names nobody.
        /// </summary>
        /// <remarks>
        /// The solver memoizes per frame, so calling this from <see cref="Yaw"/>,
        /// <see cref="Pitch"/> and the harness's <c>MoveInput</c> builder in one frame is one
        /// solve, not three — and, more to the point, one ANSWER rather than three that can
        /// disagree while the target walks.
        /// </remarks>
        public ScriptedTargetSolver.Solution Aim()
        {
            ScriptedInputStep step = Step;
            if (step == null || _solver == null || string.IsNullOrEmpty(step.aimAtPlayer))
                return default;

            return _solver.Solve(step.aimAtPlayer);
        }

        public float Lean => 0f;

        public float LookDeltaX => 0f;

        public float LookDeltaY => 0f;

        /// <summary>
        /// The <c>C_INPUT</c> bitfield, packed by the one packer rather than by masking here.
        /// </summary>
        /// <remarks>
        /// <c>InputButtonPacker</c> owns the bit numbers (protocol-spec.md § 4.2). A second
        /// transcription of them in a harness would drift from the shipped one with nothing
        /// watching, and lane B would then grade a wire format only it believes in.
        /// </remarks>
        public ushort Buttons
        {
            get
            {
                ScriptedInputStep step = Step;
                if (step == null) return 0;

                return InputButtonPacker.Pack(
                    fire: step.fire,
                    aim: step.aim,
                    reload: step.reload,
                    jump: step.jump,
                    crouch: step.crouch,
                    sprint: step.sprint,
                    use: step.use,
                    weaponSlot: step.switchWeaponSlot);
            }
        }

        public float HeliYaw => 0f;

        public float HeliCollective => 0f;

        public float HeliRoll => 0f;

        public float HeliPitch => 0f;

        /// <summary>
        /// True exactly once per step that declares <c>respawn</c>.
        /// </summary>
        /// <remarks>
        /// <b>Reading this consumes the edge.</b> That is deliberate and it is safe because
        /// exactly one consumer exists -- <c>NetClientLocalCombatDriver.Update</c>. A second
        /// reader would silently eat the press, so if one ever appears this becomes a method
        /// with a name that says so rather than a property that lies.
        /// </remarks>
        public bool RespawnPressed => _cursor.TryConsumeRespawn();
    }
}
#endif
