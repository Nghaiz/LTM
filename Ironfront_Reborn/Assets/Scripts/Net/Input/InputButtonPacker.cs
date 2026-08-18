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
        /// Grenade, prone, lean and weapon-switch bits are deliberately absent: nothing in
        /// <c>FpsActorController</c> produces them today. Prone does not exist in the game at
        /// all (docs/codebase-map.md § 2), weapon switching is an edge-triggered key rather than
        /// a held button, and lean travels as the continuous
        /// <see cref="IInputSource.Lean"/> axis locally. Packing a bit that no reader sets is
        /// how a protocol field quietly becomes permanently zero.
        /// </remarks>
        public static ushort Pack(
            bool fire, bool aim, bool reload, bool jump, bool crouch, bool sprint, bool use)
        {
            InputButtons b = InputButtons.None;

            if (fire)   b |= InputButtons.Fire;
            if (aim)    b |= InputButtons.Aim;
            if (reload) b |= InputButtons.Reload;
            if (jump)   b |= InputButtons.Jump;
            if (crouch) b |= InputButtons.Crouch;
            if (sprint) b |= InputButtons.Sprint;
            if (use)    b |= InputButtons.Use;

            return (ushort)b;
        }
    }
}
