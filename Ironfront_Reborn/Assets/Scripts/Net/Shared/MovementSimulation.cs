using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The Unity-facing face of the shared movement simulation. Runs on BOTH the client (Dev
    /// A's prediction) and the server (the replication track's authoritative simulation).
    /// </summary>
    /// <remarks>
    /// <para>
    /// conventions.md section 7 marks this file "Nobody else may edit" — it is
    /// the shared source of truth for client and server, and the moment the two sides disagree
    /// about one line of it, every predicted tick mispredicts and the player rubber-bands.
    /// </para>
    /// <para>
    /// <b>There is deliberately no logic here.</b> Every constant and every branch lives in
    /// <see cref="MovementCore"/>, in a plain .NET assembly with no UnityEngine reference, so
    /// it can be unit-tested without the Editor — and it is, by 18 tests. This file is the
    /// type conversion and nothing else. Anything that looks like a rule belongs on the other
    /// side of this boundary.
    /// </para>
    /// <para>
    /// <b>There are no <c>if (IsClient)</c> branches here and there must never be any.</b>
    /// </para>
    /// </remarks>
    public static class MovementSimulation
    {
        // Re-exported so Unity code can read them without taking a using on the core.
        public const float WalkSpeed          = MovementCore.WalkSpeed;
        public const float RunSpeed           = MovementCore.RunSpeed;
        public const float JumpSpeed          = MovementCore.JumpSpeed;
        public const float StickToGroundForce = MovementCore.StickToGroundForce;
        public const float Gravity            = MovementCore.Gravity;
        public const float StandHeight        = MovementCore.StandHeight;
        public const float CrouchHeight       = MovementCore.CrouchHeight;

        /// <summary>The simulation timestep. Client prediction and the server MUST use this.</summary>
        /// <remarks>
        /// Not <c>Time.fixedDeltaTime</c>. The project's fixed timestep is 0.02 (50 Hz) while
        /// <see cref="ProtocolConstants.SIM_TICK_RATE"/> is 30 — feeding the project's value in
        /// here makes the client integrate gravity 50 times a second against the server's 30,
        /// and prediction disagrees with authority on every airborne tick.
        /// </remarks>
        public const float FixedDeltaTime = 1f / ProtocolConstants.SIM_TICK_RATE;

        public static Vec3 ToCore(Vector3 v) => new Vec3(v.x, v.y, v.z);

        public static Vector3 ToUnity(Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>
        /// Advances one tick and returns the motion to hand to
        /// <c>CharacterController.Move</c>.
        /// </summary>
        public static Vector3 Step(ref MoveState state, in MoveInput input, float dt)
            => ToUnity(MovementCore.Step(ref state, in input, dt));

        /// <summary>Builds movement intent from a wire input frame.</summary>
        public static MoveInput ToInput(in InputFrame frame) => MoveInput.FromFrame(in frame);

        /// <summary>
        /// Builds movement intent from live Unity input, matching what
        /// <c>FirstPersonController.GetInput</c> reads.
        /// </summary>
        /// <remarks>
        /// Used by the client to build the frame it both predicts with and sends. Sampling the
        /// same axes the original reads is what keeps the shadow comparison honest.
        /// </remarks>
        public static MoveInput FromUnityInput(float yawDegrees)
            => new MoveInput(
                Input.GetAxis("Horizontal"),
                Input.GetAxis("Vertical"),
                yawDegrees,
                Input.GetButton("Jump"),
                Input.GetButton("Sprint"),
                Input.GetButton("Crouch"));

        /// <summary>Quantizes movement intent into the frame that goes on the wire.</summary>
        public static InputFrame ToFrame(in MoveInput input, float pitchDegrees, InputButtons extraButtons)
        {
            InputButtons buttons = extraButtons;
            if (input.Jump)   buttons |= InputButtons.Jump;
            if (input.Sprint) buttons |= InputButtons.Sprint;
            if (input.Crouch) buttons |= InputButtons.Crouch;

            return InputFrame.FromFloats(
                input.MoveX, input.MoveZ, input.YawDegrees, pitchDegrees, buttons);
        }
    }
}
