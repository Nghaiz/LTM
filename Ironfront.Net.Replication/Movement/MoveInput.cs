using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// One tick of movement intent, dequantized and ready for
    /// <see cref="MovementCore.Step"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="InputFrame"/> on purpose. <see cref="InputFrame"/> is the
    /// wire shape and stays quantized so a frame read off the network and a frame about to be
    /// written are byte-identical; this is the gameplay shape. Converting once at the edge
    /// (<see cref="FromFrame"/>) keeps the quantization boundary in one place instead of
    /// scattering <c>/127f</c> through the simulation.
    /// </para>
    /// <para>
    /// <b><see cref="Fire"/>, <see cref="Aim"/> and <see cref="Reload"/> are carried but not
    /// simulated.</b> <see cref="MovementCore.Step"/> does not read them and must not — combat
    /// is <c>ServerCombatAuthority</c>'s, off the <see cref="InputFrame"/> directly. They live
    /// here because this struct is the one thing the client's tick loop hands to the sender, so
    /// a bit that is not on it cannot reach the wire at all: that was debt-ledger row X-3, where
    /// <c>InputButtons</c> declared all three, the server read all three, and the client's mask
    /// builder knew only Jump / Sprint / Crouch. Carrying them through <see cref="FromFrame"/>
    /// as well keeps the dequantize path symmetric, which is what makes a replayed frame on the
    /// server identical to the frame the client predicted with.
    /// </para>
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

        /// <summary>Trigger held. <see cref="InputButtons.Fire"/>.</summary>
        public readonly bool Fire;

        /// <summary>Aiming down sights. <see cref="InputButtons.Aim"/>.</summary>
        public readonly bool Aim;

        /// <summary>Reload held. <see cref="InputButtons.Reload"/>.</summary>
        public readonly bool Reload;

        /// <summary>Movement-only intent. The combat bits read false.</summary>
        /// <remarks>
        /// Kept as its own overload rather than folded into optional parameters: the shadow
        /// comparison (<c>MovementShadowCompare</c>) rebuilds a frame field by field and means
        /// "no combat intent here", which an omitted argument would state less plainly.
        /// </remarks>
        public MoveInput(
            float moveX, float moveZ, float yawDegrees, bool jump, bool sprint, bool crouch)
            : this(moveX, moveZ, yawDegrees, jump, sprint, crouch, false, false, false)
        {
        }

        public MoveInput(
            float moveX, float moveZ, float yawDegrees,
            bool jump, bool sprint, bool crouch,
            bool fire, bool aim, bool reload)
        {
            MoveX      = moveX;
            MoveZ      = moveZ;
            YawDegrees = yawDegrees;
            Jump       = jump;
            Sprint     = sprint;
            Crouch     = crouch;
            Fire       = fire;
            Aim        = aim;
            Reload     = reload;
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
                frame.IsPressed(InputButtons.Crouch),
                frame.IsPressed(InputButtons.Fire),
                frame.IsPressed(InputButtons.Aim),
                frame.IsPressed(InputButtons.Reload));

        /// <summary>The same frame with the movement axes replaced. Used by the speed check.</summary>
        public MoveInput WithAxes(float moveX, float moveZ)
            => new MoveInput(
                moveX, moveZ, YawDegrees, Jump, Sprint, Crouch, Fire, Aim, Reload);

        /// <summary>
        /// The <c>C_INPUT</c> button mask this intent implies.
        /// </summary>
        /// <remarks>
        /// <b>The one place a <see cref="MoveInput"/> becomes buttons.</b> Both senders — the
        /// on-foot prediction stage and the shared Unity conversion — call this rather than
        /// repeating the six <c>if</c>s, because a mask built in two places is a mask that
        /// disagrees with itself the first time a bit is added. That is exactly how X-3
        /// happened: <c>ClientPredictionStage</c> carried its own copy and never grew Fire.
        /// </remarks>
        public InputButtons ToButtons()
        {
            InputButtons buttons = InputButtons.None;

            if (Jump)   buttons |= InputButtons.Jump;
            if (Sprint) buttons |= InputButtons.Sprint;
            if (Crouch) buttons |= InputButtons.Crouch;
            if (Fire)   buttons |= InputButtons.Fire;
            if (Aim)    buttons |= InputButtons.Aim;
            if (Reload) buttons |= InputButtons.Reload;

            return buttons;
        }
    }
}
