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
        /// Grenade, prone and lean bits are deliberately absent: nothing in
        /// <c>FpsActorController</c> produces them today. Prone does not exist in the game at
        /// all (docs/codebase-map.md § 2), and lean travels as the continuous
        /// <see cref="IInputSource.Lean"/> axis locally. Packing a bit that no reader sets is
        /// how a protocol field quietly becomes permanently zero.
        /// <para>
        /// <b>Weapon switch moved out of that list on 2026-08-21</b>, and only because BOTH
        /// halves landed together: the overload below produces bits 11-14 and
        /// <c>ServerCombatBridge</c> consumes them. The shipped keyboard path still does not
        /// produce them -- a human client switches weapons locally and the server is never told,
        /// which is a real gap and a separate decision (it needs prediction and a UI story), so
        /// it is recorded rather than half-built here.
        /// </para>
        /// </remarks>
        public static ushort Pack(
            bool fire, bool aim, bool reload, bool jump, bool crouch, bool sprint, bool use)
            => Pack(fire, aim, reload, jump, crouch, sprint, use, weaponSlot: -1);

        /// <summary>
        /// As above, plus a weapon selection. <paramref name="weaponSlot"/> is 0..3; anything
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
        {
            InputButtons b = InputButtons.None;

            if (fire)   b |= InputButtons.Fire;
            if (aim)    b |= InputButtons.Aim;
            if (reload) b |= InputButtons.Reload;
            if (jump)   b |= InputButtons.Jump;
            if (crouch) b |= InputButtons.Crouch;
            if (sprint) b |= InputButtons.Sprint;
            if (use)    b |= InputButtons.Use;

            // The mapping is InputFrame.SlotBit's, not a copy of it. MoveInput.ToButtons is the
            // other producer of these four bits and lives in an assembly this one cannot see;
            // two transcriptions of bits 11-14 is exactly how X-31 happened.
            b |= InputFrame.SlotBit(weaponSlot);

            return (ushort)b;
        }
    }
}
