using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Input that arrived over the wire, or that is being replayed out of the local history
    /// during reconciliation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A (assist track). The whole point of phase-00 task 3: a controller reading
    /// this instead of <c>UnityEngine.Input</c> is a networked controller, and nothing else
    /// about it has to change.
    /// </para>
    /// <para>
    /// <b>Values come back dequantized, and that is deliberately visible.</b> An
    /// <see cref="InputFrame"/> stores the move axes as i8 and yaw as u16, so a value that went
    /// in as 0.37 comes back as 0.3701. Callers must never compare a value from here for
    /// equality against a locally sampled one — <c>PredictionReconciler</c> exists precisely
    /// because those two numbers are not the same number.
    /// </para>
    /// </remarks>
    public sealed class NetInputSource : IInputSource
    {
        private InputFrame _frame;

        /// <summary>Replaces the frame every subsequent read reports.</summary>
        public void SetFrame(in InputFrame frame) => _frame = frame;

        /// <summary>The frame currently being reported, for diagnostics.</summary>
        public InputFrame Frame => _frame;

        public float MoveX => _frame.MoveXFloat;
        public float MoveZ => _frame.MoveZFloat;
        public float Yaw => _frame.YawDegrees;
        public float Pitch => _frame.PitchDegrees;

        /// <summary>
        /// Tri-state, because the wire has no lean axis.
        /// </summary>
        /// <remarks>
        /// <c>C_INPUT</c> carries <see cref="InputButtons.LeanLeft"/> and
        /// <see cref="InputButtons.LeanRight"/>, not a float, so a remote actor leans fully or
        /// not at all while a local one leans continuously. Both pressed cancels, which is the
        /// same thing Unity's "Lean" axis does when both keys are held. This asymmetry is real
        /// and is not something this class can fix — closing it means adding a lean axis to
        /// protocol-spec.md § 4.2, which is a shared-file decision.
        /// </remarks>
        public float Lean
        {
            get
            {
                bool left = (_frame.Buttons & InputButtons.LeanLeft) != 0;
                bool right = (_frame.Buttons & InputButtons.LeanRight) != 0;
                if (left == right) return 0f;
                return left ? -1f : 1f;
            }
        }

        /// <summary>Zero: there is no mouse at the other end of a socket.</summary>
        public float LookDeltaX => 0f;

        /// <summary>Zero. See <see cref="LookDeltaX"/>.</summary>
        public float LookDeltaY => 0f;

        public ushort Buttons => (ushort)_frame.Buttons;
    }
}
