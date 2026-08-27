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
    /// <b><see cref="Fire"/>, <see cref="Aim"/>, <see cref="Reload"/>, <see cref="Use"/> and
    /// <see cref="WeaponSlot"/> are carried but not simulated.</b> <see cref="MovementCore.Step"/>
    /// does not read them and must not — combat is <c>ServerCombatAuthority</c>'s, off the
    /// <see cref="InputFrame"/> directly. They live here because this struct is the one thing the
    /// client's tick loop hands to the sender, so a bit that is not on it cannot reach the wire at
    /// all: that was debt-ledger row X-3, where <c>InputButtons</c> declared all three, the server
    /// read all three, and the client's mask builder knew only Jump / Sprint / Crouch. Carrying
    /// them through <see cref="FromFrame"/> as well keeps the dequantize path symmetric, which is
    /// what makes a replayed frame on the server identical to the frame the client predicted with.
    /// </para>
    /// <para>
    /// <b><see cref="WeaponSlot"/> and <see cref="Use"/> arrived on 2026-08-27 as debt-ledger row
    /// X-31, and X-31 is X-3 happening a second time to the same struct.</b> <c>InputButtons</c>
    /// declared <c>SwitchWeapon0..3</c> and <c>Use</c>, <c>ServerCombatBridge</c> read the slot and
    /// called <c>ApplyWeaponSwitchIntent</c> — and <see cref="ToButtons"/>, the one place a
    /// <see cref="MoveInput"/> becomes buttons, had never heard of either. The bits could only
    /// reach the wire through <c>NetPredictionClock.DefaultInput</c>, which a scripted client
    /// replaces wholesale, so a lane-B grenade programme carrying <c>switchWeaponSlot: 2</c> put
    /// <c>buttons=0x0001</c> on the wire — Fire alone — on 60 of 60 frames
    /// (<c>artifacts/lane-b/x31-diag-04</c>). The lesson X-3's own remark drew is the one that was
    /// not applied: a field that is not on this struct cannot reach the wire, and adding a bit to
    /// <c>InputButtons</c> without adding it here builds exactly half a feature.
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

        /// <summary>Use/interact held. <see cref="InputButtons.Use"/>.</summary>
        public readonly bool Use;

        /// <summary>
        /// Weapon slot this tick selects, 0..3. Negative selects nothing.
        /// </summary>
        /// <remarks>
        /// <b>An int rather than four bools</b>, because the wire is four mutually exclusive bits
        /// and <c>InputFrame.WeaponSlot</c> decodes them back to exactly this quantity. Four bools
        /// here would admit a frame asking for two slots at once, which the wire can express and
        /// the server resolves by first-match — a disagreement between predicted and replayed
        /// input that nothing would report.
        /// </remarks>
        public readonly int WeaponSlot;

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

        /// <summary>Movement plus the three combat holds. No use, no weapon selection.</summary>
        /// <remarks>
        /// Kept for the callers that genuinely have nothing to say about a slot — the shadow
        /// comparison and <c>MovementCore</c>'s own tests. A caller that DOES carry a slot and
        /// reaches for this overload silently drops it, which is row X-31; the full constructor
        /// below is the one every send path uses.
        /// </remarks>
        public MoveInput(
            float moveX, float moveZ, float yawDegrees,
            bool jump, bool sprint, bool crouch,
            bool fire, bool aim, bool reload)
            : this(moveX, moveZ, yawDegrees, jump, sprint, crouch, fire, aim, reload,
                   use: false, weaponSlot: -1)
        {
        }

        public MoveInput(
            float moveX, float moveZ, float yawDegrees,
            bool jump, bool sprint, bool crouch,
            bool fire, bool aim, bool reload,
            bool use, int weaponSlot)
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
            Use        = use;
            WeaponSlot = weaponSlot;
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
                frame.IsPressed(InputButtons.Reload),
                frame.IsPressed(InputButtons.Use),
                frame.WeaponSlot);

        /// <summary>The same frame with the movement axes replaced. Used by the speed check.</summary>
        public MoveInput WithAxes(float moveX, float moveZ)
            => new MoveInput(
                moveX, moveZ, YawDegrees, Jump, Sprint, Crouch, Fire, Aim, Reload,
                Use, WeaponSlot);

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
            if (Use)    buttons |= InputButtons.Use;

            // Exactly one slot bit, or none, and the mapping is InputFrame's rather than a
            // fifth transcription of bits 11-14 here. That transcription is what X-3 and X-31
            // both are.
            buttons |= InputFrame.SlotBit(WeaponSlot);

            return buttons;
        }
    }
}
