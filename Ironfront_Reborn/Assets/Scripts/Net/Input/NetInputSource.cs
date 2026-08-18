using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Input that arrived over the wire, or that is being replayed out of the local history
    /// during reconciliation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole point of phase-00 task 3: a controller reading
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

        private bool _driving;
        private float _steer;
        private float _throttle;
        private HelicopterAxes _heli;

        /// <summary>Replaces the frame every subsequent read reports.</summary>
        public void SetFrame(in InputFrame frame) => _frame = frame;

        /// <summary>The frame currently being reported, for diagnostics.</summary>
        public InputFrame Frame => _frame;

        /// <summary>True while vehicle axes are overriding <see cref="MoveX"/> / <see cref="MoveZ"/>.</summary>
        public bool IsDriving => _driving;

        /// <summary>
        /// Publishes the axes of the vehicle this actor is driving, taken from the last
        /// <c>C_VEHICLE_INPUT</c> the server accepted for it.
        /// </summary>
        /// <param name="steer">
        /// <c>C_VEHICLE_INPUT.steer</c>. Becomes <see cref="MoveX"/>, because
        /// <c>FpsActorController.CarInput</c> is <c>(MoveX, MoveZ)</c> and <c>Car.FixedUpdate</c>
        /// reads its <c>.x</c> as the steering target.
        /// </param>
        /// <param name="throttle"><c>C_VEHICLE_INPUT.throttle</c>. Becomes <see cref="MoveZ"/>.</param>
        /// <param name="heli">The four helicopter controls. See <see cref="HelicopterAxes"/>.</param>
        /// <remarks>
        /// <para>
        /// <b>The hold window is not implemented here, deliberately.</b> V5-D11's
        /// <c>VEHICLE_INPUT_HOLD_TICKS</c> is server policy — how long the server keeps
        /// believing a fact it has not heard repeated — and it lives with the seat table that
        /// enforces it, in <c>VehicleInputAuthority</c>. This class reports what it was last
        /// given. Re-deriving the decay here would be a second definition of the window in an
        /// assembly that cannot see the first, which is how the two end up disagreeing about
        /// when a stalled driver stops.
        /// </para>
        /// <para>
        /// While driving, <see cref="MoveX"/> and <see cref="MoveZ"/> report the vehicle axes
        /// rather than the <c>C_INPUT</c> frame's. The frame's are what the actor would walk
        /// with, and a seated actor does not walk — but <c>FpsActorController.CarInput</c> reads
        /// the same two members for both, which is exactly why the seam works at all (V5-D7).
        /// </para>
        /// </remarks>
        public void SetVehicleAxes(float steer, float throttle, in HelicopterAxes heli)
        {
            _driving = true;
            _steer = steer;
            _throttle = throttle;
            _heli = heli;
        }

        /// <summary>
        /// Centres the vehicle axes and returns <see cref="MoveX"/> / <see cref="MoveZ"/> to the
        /// <c>C_INPUT</c> frame. Called on seat exit and when the hold window expires.
        /// </summary>
        public void ClearVehicleAxes()
        {
            _driving = false;
            _steer = 0f;
            _throttle = 0f;
            _heli = HelicopterAxes.Neutral;
        }

        public float MoveX => _driving ? _steer : _frame.MoveXFloat;
        public float MoveZ => _driving ? _throttle : _frame.MoveZFloat;
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

        /// <summary>
        /// The four helicopter controls from the last accepted <c>C_VEHICLE_INPUT</c>, or zero
        /// when not driving. V5-D8.
        /// </summary>
        /// <remarks>
        /// These are already scaled and inverted by the sender (V5-D9), so the server never
        /// reaches for <c>OptionsUi</c> — which it does not have, and which would be an
        /// authority hole if it did.
        /// </remarks>
        public float HeliYaw => _heli.Yaw;

        /// <summary>See <see cref="HeliYaw"/>.</summary>
        public float HeliCollective => _heli.Collective;

        /// <summary>See <see cref="HeliYaw"/>.</summary>
        public float HeliRoll => _heli.Roll;

        /// <summary>See <see cref="HeliYaw"/>.</summary>
        public float HeliPitch => _heli.Pitch;
    }
}
