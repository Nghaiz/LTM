using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Named reads of <see cref="IInputSource.Buttons"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A (assist track). The read half of the bitfield;
    /// <see cref="InputButtonPacker"/> is the write half, and the two are covered by the same
    /// round-trip test.
    /// </para>
    /// <para>
    /// <b>Extension methods rather than default interface members.</b> The phase-00 sketch put
    /// these on the interface as C# 8 default implementations. Unity 6000.3 compiles C# 9, so
    /// that would probably work — "probably" being the problem, since the failure mode is a
    /// compile error at Dev A's desk that no gate in this repository can catch (CI builds no
    /// Unity code; .github/workflows/ci.yml says so in its own comment). Extension methods have
    /// worked since C# 3.0 and cost one <c>()</c> at the call site.
    /// </para>
    /// </remarks>
    public static class InputSourceExtensions
    {
        public static bool Fire(this IInputSource source)   => Has(source, InputButtons.Fire);
        public static bool Aim(this IInputSource source)    => Has(source, InputButtons.Aim);
        public static bool Reload(this IInputSource source) => Has(source, InputButtons.Reload);
        public static bool Jump(this IInputSource source)   => Has(source, InputButtons.Jump);
        public static bool Crouch(this IInputSource source) => Has(source, InputButtons.Crouch);
        public static bool Sprint(this IInputSource source) => Has(source, InputButtons.Sprint);
        public static bool Use(this IInputSource source)    => Has(source, InputButtons.Use);

        private static bool Has(IInputSource source, InputButtons button)
            => source != null && (source.Buttons & (ushort)button) != 0;
    }
}
