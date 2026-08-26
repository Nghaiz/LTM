namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// The original first-person controller, as the movement shadow-comparison reads it: is it
    /// driving, and is it sprinting. Phase C4d.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This binding exists because of a blind spot worth recording.</b> <c>FirstPersonController</c>
    /// is declared in <c>Assets/Plugins/Assembly-CSharp-firstpass/</c> — a SECOND predefined
    /// assembly, and one that sits outside <c>Assets/Scripts</c>, which is the root both the C4
    /// enumeration and <c>check-net-layering.ps1</c> scan. Neither could see it. It surfaced the
    /// only way it could: the Unity compile went red on the asmdef.
    /// </para>
    /// <para>
    /// <c>Assembly-CSharp-firstpass</c> is unreachable from an asmdef for exactly the reason
    /// <c>Assembly-CSharp</c> is — predefined assemblies compile after every asmdef — so it needs
    /// the same treatment, and this is it.
    /// </para>
    /// <para>
    /// <b>Two facts, because two facts are what was read.</b> The shadow comparison samples
    /// <c>sprinting</c> to feed <c>MoveState</c>, and asks whether the legacy controller is
    /// actually driving before it trusts a sample at all. Nothing else on that controller crossed.
    /// </para>
    /// </remarks>
    public interface ILegacyMovementProbe
    {
        /// <summary>
        /// Whether the original controller is present, enabled and taking input — i.e. whether
        /// it is the thing currently moving this body.
        /// </summary>
        /// <remarks>
        /// All three conditions together, on this side of the seam, because they are one question
        /// and splitting them across the boundary would invite a caller to ask two of the three.
        /// A sample taken while it is not driving is comparing the port against nothing.
        /// </remarks>
        bool IsDriving { get; }

        /// <summary>Whether the original controller reports a sprint this frame.</summary>
        bool IsSprinting { get; }
    }
}
