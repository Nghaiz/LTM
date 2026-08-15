namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where a controller's gameplay input comes from. Keyboard and mouse, the network, or
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Closes phase-00 task 3 — the seam a networked controller needs in order to
    /// exist. Written by the lead's assist track (plans/assist-dev-a/step-02-input-source.md).
    /// </para>
    /// <para>
    /// <b>No UnityEngine here, on purpose.</b> This file and its pure siblings
    /// (<see cref="InputButtonPacker"/>, <see cref="InputSourceExtensions"/>,
    /// <see cref="NetInputSource"/>, <see cref="NullInputSource"/>) are compiled a second time
    /// by <c>Ironfront.Client.Input.Tests</c> via <c>&lt;Compile Include&gt;</c> links, which is
    /// the only way anything under <c>Assets/</c> is reachable by <c>dotnet test</c> — the Unity
    /// project has no <c>.asmdef</c>, so the Unity Test Framework is not available either.
    /// Adding a <c>using UnityEngine;</c> to any of them silently drops it out of test coverage.
    /// </para>
    /// <para>
    /// <b>The button bit order is not ours to choose.</b> <see cref="Buttons"/> is the
    /// <c>C_INPUT</c> bitfield from protocol-spec.md § 4.2, and the one definition of it is
    /// <see cref="Ironfront.Net.Protocol.InputButtons"/>. Never re-declare the bit numbers —
    /// use <see cref="InputButtonPacker"/> to write them and <see cref="InputSourceExtensions"/>
    /// to read them.
    /// </para>
    /// <para>
    /// <b>What this interface does NOT carry.</b> Walking, jumping and mouse-look do not pass
    /// through <c>FpsActorController</c> at all — they are read by
    /// <c>FirstPersonController</c> under <c>Assets/Plugins/</c>, and the netcode replaces that
    /// component rather than feeding it (docs/codebase-map.md § 4). <see cref="MoveX"/> and
    /// <see cref="MoveZ"/> exist here because swimming and vehicle steering read the same two
    /// axes inside <c>FpsActorController</c>; they are not how the player walks.
    /// </para>
    /// </remarks>
    public interface IInputSource
    {
        /// <summary>Strafe axis, -1..1. Unity's "Horizontal".</summary>
        float MoveX { get; }

        /// <summary>Forward axis, -1..1. Unity's "Vertical".</summary>
        float MoveZ { get; }

        /// <summary>Absolute facing, degrees, 0..360.</summary>
        float Yaw { get; }

        /// <summary>Absolute aim pitch, degrees, -90..90.</summary>
        float Pitch { get; }

        /// <summary>Lean axis, -1..1. Continuous locally; tri-state over the wire.</summary>
        float Lean { get; }

        /// <summary>
        /// This frame's mouse movement, in Unity's raw axis units — a delta, not an angle.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Yaw"/> because helicopter control integrates the delta
        /// itself (<c>FpsActorController.HelicopterInput</c>) and cannot use an absolute angle.
        /// Substituting one for the other is a silent handling change, which is why both exist.
        /// A network source has no mouse and returns 0.
        /// </remarks>
        float LookDeltaX { get; }

        /// <summary>This frame's vertical mouse movement. See <see cref="LookDeltaX"/>.</summary>
        float LookDeltaY { get; }

        /// <summary>
        /// The <c>C_INPUT</c> button bitfield (protocol-spec.md § 4.2). Read it with
        /// <see cref="InputSourceExtensions"/> rather than by masking inline.
        /// </summary>
        ushort Buttons { get; }
    }
}
