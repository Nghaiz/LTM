using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// One tick of movement intent, dequantized and ready for
    /// <see cref="MovementCore.Step"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InputFrame"/> on purpose. <see cref="InputFrame"/> is the
    /// wire shape and stays quantized so a frame read off the network and a frame about to be
    /// written are byte-identical; this is the gameplay shape. Converting once at the edge
    /// (<see cref="FromFrame"/>) keeps the quantization boundary in one place instead of
    /// scattering <c>/127f</c> through the simulation.
    /// </remarks>
    public readonly struct MoveInput
    {
        /// <summary>Strafe axis, -1..1. Positive is right.</summary>
        public readonly float MoveX;

        /// <summary>Forward axis, -1..1. Positive is forward.</summary>
        public readonly float MoveZ;

        /// <summary>Facing, in degrees, 0..360.</summary>
        public readonly float YawDegrees;

        public readonly bool Jump;
        public readonly bool Sprint;
        public readonly bool Crouch;

        public MoveInput(
            float moveX, float moveZ, float yawDegrees, bool jump, bool sprint, bool crouch)
        {
            MoveX      = moveX;
            MoveZ      = moveZ;
            YawDegrees = yawDegrees;
            Jump       = jump;
            Sprint     = sprint;
            Crouch     = crouch;
        }

        /// <summary>
        /// Dequantizes a wire frame.
        /// </summary>
        /// <remarks>
        /// This does <b>not</b> normalize the movement axes. That is the server's job and it
        /// is a security check, not a conversion — see
        /// <c>Ironfront.Net.Replication.Server.InputAuthority</c>. Folding it in here would
        /// hide the check inside a parser, where nobody reviewing the anti-cheat path would
        /// find it.
        /// </remarks>
        public static MoveInput FromFrame(in InputFrame frame)
            => new MoveInput(
                frame.MoveXFloat,
                frame.MoveZFloat,
                frame.YawDegrees,
                frame.IsPressed(InputButtons.Jump),
                frame.IsPressed(InputButtons.Sprint),
                frame.IsPressed(InputButtons.Crouch));

        /// <summary>The same frame with the movement axes replaced. Used by the speed check.</summary>
        public MoveInput WithAxes(float moveX, float moveZ)
            => new MoveInput(moveX, moveZ, YawDegrees, Jump, Sprint, Crouch);
    }
}
