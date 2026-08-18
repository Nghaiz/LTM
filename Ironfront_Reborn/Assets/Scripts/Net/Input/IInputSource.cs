namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where a controller's gameplay input comes from. Keyboard and mouse, the network, or
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes phase-00 task 3 — the seam a networked controller needs in order to
    /// exist. Written by the lead's assist track (plans/unity-client/study/step-02-input-source.md).
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

        /// <summary>Helicopter tail rotor. The <c>Vector4.x</c> of <c>HelicopterInput</c>.</summary>
        /// <remarks>
        /// <para>
        /// <b>Why four helicopter members exist here when <see cref="LookDeltaX"/> already
        /// carries a mouse delta (V5-D8).</b> A networked helicopter is structurally
        /// unreachable through the existing members. <c>NetInputSource.LookDeltaX</c> returns 0
        /// and correctly so — <c>C_INPUT</c> carries an absolute yaw, and a per-frame mouse
        /// delta is a different quantity an absolute-angle protocol cannot express. And the
        /// <c>helicopterType == 2</c> branch never read the seam at all: it read
        /// <c>Input.GetAxis</c> directly, booked as accepted debt in an in-file comment. Both
        /// roads end here.
        /// </para>
        /// <para>
        /// <b>These are post-scaling, post-inversion (V5-D9).</b> Sensitivity and the four
        /// invert flags are client-local settings from <c>OptionsUi</c>, which the server does
        /// not have and must never reach for — doing so at server role is an authority hole and
        /// a headless <c>NullReferenceException</c> at once. So the sender applies them and what
        /// crosses the seam is a finished control vector, bounded by <c>Vehicle.Clamp4</c> at
        /// the vehicle exactly as it already is offline.
        /// </para>
        /// <para>
        /// Which slot each of these occupies on the wire is <see cref="HelicopterAxes"/>'s to
        /// say, and nothing else's.
        /// </para>
        /// </remarks>
        float HeliYaw { get; }

        /// <summary>Helicopter lift. The <c>Vector4.y</c>. See <see cref="HeliYaw"/>.</summary>
        float HeliCollective { get; }

        /// <summary>Helicopter bank. The <c>Vector4.z</c>. See <see cref="HeliYaw"/>.</summary>
        float HeliRoll { get; }

        /// <summary>Helicopter nose pitch. The <c>Vector4.w</c>. See <see cref="HeliYaw"/>.</summary>
        float HeliPitch { get; }
    }
}
