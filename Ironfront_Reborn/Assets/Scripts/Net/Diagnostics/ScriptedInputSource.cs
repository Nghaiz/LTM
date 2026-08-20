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

        public ScriptedInputSource(ScriptedInputCursor cursor)
            => _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));

        private ScriptedInputStep Step => _cursor.Current;

        public float MoveX => Step != null ? Step.moveX : 0f;

        public float MoveZ => Step != null ? Step.moveZ : 0f;

        /// <summary>The cursor's integrated facing, not the step's declared one.</summary>
        public float Yaw => _cursor.Yaw;

        public float Pitch => Step != null ? Step.pitchDegrees : 0f;

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
                    use: step.use);
            }
        }

        public float HeliYaw => 0f;

        public float HeliCollective => 0f;

        public float HeliRoll => 0f;

        public float HeliPitch => 0f;
    }
}
