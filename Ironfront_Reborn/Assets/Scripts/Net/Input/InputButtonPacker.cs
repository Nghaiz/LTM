using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Turns a frame's worth of pressed/not-pressed into the <c>C_INPUT</c> bitfield.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write half of the bitfield;
    /// <see cref="InputSourceExtensions"/> is the read half.
    /// </para>
    /// <para>
    /// <b>Why this is a separate file from <c>LocalInputSource</c>.</b> The bit assignment is
    /// the one part of the input seam that can be wrong without anything failing to compile, and
    /// <c>LocalInputSource</c> touches <c>UnityEngine.Input</c>, which puts it out of reach of
    /// <c>dotnet test</c> forever. Splitting the packing out is what makes the bit order
    /// testable at all — see <c>Ironfront.Client.Input.Tests</c>.
    /// </para>
    /// <para>
    /// The numbers come from <see cref="InputButtons"/> and are never restated here. If a bit
    /// moves in protocol-spec.md § 4.2, this file needs no edit.
    /// </para>
    /// </remarks>
    public static class InputButtonPacker
    {
        /// <summary>
        /// Packs the gameplay buttons a controller can observe.
        /// </summary>
        /// <remarks>
        /// Grenade and prone bits are deliberately absent: nothing in <c>FpsActorController</c>
        /// produces them. Prone does not exist in the game at all (docs/codebase-map.md § 2).
        /// Packing a bit that no reader sets is how a protocol field quietly becomes permanently
        /// zero. The lean bits have a producer and a reader since 2026-09-28 -- see the overload
        /// that takes <c>lean</c>.
        /// <para>
        /// <b>Weapon switch moved out of that list on 2026-08-21</b>, and only because both
        /// halves landed together: the overload below produces the slot bits and
        /// <c>ServerCombatBridge</c> consumes them. The human keyboard/wheel path supplies an
        /// absolute slot through <c>LocalInputSource</c>, keeping the immediate local switch
        /// while also informing the authoritative server.
        /// </para>
        /// </remarks>
        public static ushort Pack(
            bool fire, bool aim, bool reload, bool jump, bool crouch, bool sprint, bool use)
            => Pack(fire, aim, reload, jump, crouch, sprint, use, weaponSlot: -1);

        /// <summary>
        /// As above, plus a weapon selection. <paramref name="weaponSlot"/> is 0..4; anything
        /// else selects nothing.
        /// </summary>
        /// <remarks>
        /// Out of range is silently "no selection" rather than an exception: this is called once
        /// per input frame from a hot path, and a scripted programme with a typo'd slot should
        /// produce a run that visibly does not switch, not one that dies at frame 1.
        /// </remarks>
        public static ushort Pack(
            bool fire, bool aim, bool reload, bool jump, bool crouch, bool sprint, bool use,
            int weaponSlot)
            => Pack(fire, aim, reload, jump, crouch, sprint, use, weaponSlot, lean: 0f);

        /// <summary>
        /// As above, plus the lean: <see cref="InputButtons.LeanLeft"/> at <paramref name="lean"/>
        /// ≤ -0.5, <see cref="InputButtons.LeanRight"/> at ≥ 0.5, neither in between.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both halves landed together (playtest 2026-09-28, bug 5).</b> The reader is
        /// <c>ServerCombatAuthority.LeanOffset</c>, which moves a leaning shot's origin the
        /// 0.4 m to the side that <c>PlayerFpParent</c> moves the player's camera. Before this
        /// the bits were never set, so every shot fired while leaning left from beside the
        /// crosshair and missed what it was aimed at.
        /// </para>
        /// <para>
        /// Half a lean is the threshold because the camera's lean is an axis that eases in, and
        /// the wire has room for "leaning" and "not"; the half-way mark is where the camera is
        /// nearer to the one than the other.
        /// </para>
        /// </remarks>
        public static ushort Pack(
            bool fire, bool aim, bool reload, bool jump, bool crouch, bool sprint, bool use,
            int weaponSlot, float lean)
        {
            InputButtons b = InputButtons.None;

            if (lean <= -0.5f) b |= InputButtons.LeanLeft;
            else if (lean >= 0.5f) b |= InputButtons.LeanRight;

            if (fire)   b |= InputButtons.Fire;
            if (aim)    b |= InputButtons.Aim;
            if (reload) b |= InputButtons.Reload;
            if (jump)   b |= InputButtons.Jump;
            if (crouch) b |= InputButtons.Crouch;
            if (sprint) b |= InputButtons.Sprint;
            if (use)    b |= InputButtons.Use;

            // The mapping is InputFrame.SlotBit's, not a copy of it. MoveInput.ToButtons is the
            // other producer of these five bits and lives in an assembly this one cannot see;
            // duplicating the wire-bit transcription is exactly how X-31 happened.
            b |= InputFrame.SlotBit(weaponSlot);

            return (ushort)b;
        }
    }
}
