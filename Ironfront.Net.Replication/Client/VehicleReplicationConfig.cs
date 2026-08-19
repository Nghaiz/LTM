namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// How the client treats the vehicle it is driving. V5-D6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The no-prediction fallback is this struct, and it ships in the same phase as the
    /// prediction it falls back from.</b> Design section 9 scores "prediction never converges"
    /// at 15 and makes the fallback's existence a precondition of starting the phase, not a
    /// note for later. With <see cref="PredictLocalVehicle"/> false the driven vehicle takes the
    /// same kinematic remote path as every other vehicle and the driver's input is still sent —
    /// the server still simulates, the client just watches, and the cost is a round-trip of
    /// input lag rather than a broken game.
    /// </para>
    /// <para>
    /// <b>The whole test suite runs green under both presets.</b> A fallback nobody has ever
    /// flipped is not a fallback; it is an untested branch with a reassuring name.
    /// </para>
    /// <para>
    /// A <c>readonly struct</c> with static presets, mirroring <see cref="ReplicationConfig"/> —
    /// the same shape <c>InterestManager</c> already takes its tuning from, so there is one
    /// pattern in this assembly for "runtime policy that a test needs to vary".
    /// </para>
    /// </remarks>
    public readonly struct VehicleReplicationConfig
    {
        /// <summary>Blend time constant for the shipped preset, seconds.</summary>
        /// <remarks>
        /// 150 ms: long enough that a correction is not a visible jerk, short enough that the
        /// vehicle does not lag its own authority through a corner. It is a time constant, not
        /// a duration — see <c>VehicleCorrectionSolver</c> on why the blend is exponential.
        /// </remarks>
        public const float DefaultCorrectionBlendSeconds = 0.15f;

        /// <summary>Position error past which a blend is abandoned for a teleport, metres.</summary>
        /// <remarks>
        /// 4 m is about a car length. Below it a blend closes the gap invisibly; above it the
        /// vehicle is somewhere the player can see it is not, and continuing to blend renders a
        /// position the server has already rejected for as long as it takes to converge.
        /// </remarks>
        public const float DefaultHardSnapMetres = 4f;

        /// <summary>Angular error past which a blend is abandoned for a teleport, degrees.</summary>
        /// <remarks>
        /// 45 degrees. Past it the local and authoritative headings disagree about which way the
        /// vehicle is going, and every force the local simulation applies for the rest of the
        /// blend pushes it further apart rather than closer.
        /// </remarks>
        public const float DefaultHardSnapDegrees = 45f;

        /// <summary>
        /// Whether the locally-driven vehicle predicts. False routes it down the remote path.
        /// </summary>
        public readonly bool PredictLocalVehicle;

        /// <summary>Time constant of the exponential correction blend, seconds.</summary>
        public readonly float CorrectionBlendSeconds;

        /// <summary>Position error past which the correction teleports instead, metres.</summary>
        public readonly float HardSnapMetres;

        /// <summary>Angular error past which the correction teleports instead, degrees.</summary>
        public readonly float HardSnapDegrees;

        public VehicleReplicationConfig(
            bool predictLocalVehicle,
            float correctionBlendSeconds,
            float hardSnapMetres,
            float hardSnapDegrees)
        {
            PredictLocalVehicle    = predictLocalVehicle;
            CorrectionBlendSeconds = correctionBlendSeconds;
            HardSnapMetres         = hardSnapMetres;
            HardSnapDegrees        = hardSnapDegrees;
        }

        /// <summary>What ships: the driven vehicle predicts and is corrected by blending.</summary>
        public static VehicleReplicationConfig Shipped => new VehicleReplicationConfig(
            predictLocalVehicle:    true,
            correctionBlendSeconds: DefaultCorrectionBlendSeconds,
            hardSnapMetres:         DefaultHardSnapMetres,
            hardSnapDegrees:        DefaultHardSnapDegrees);

        /// <summary>
        /// The fallback: no prediction at all. The three tuning values are carried but never
        /// read, because the solver is never called.
        /// </summary>
        public static VehicleReplicationConfig NoPrediction => new VehicleReplicationConfig(
            predictLocalVehicle:    false,
            correctionBlendSeconds: DefaultCorrectionBlendSeconds,
            hardSnapMetres:         DefaultHardSnapMetres,
            hardSnapDegrees:        DefaultHardSnapDegrees);
    }
}
