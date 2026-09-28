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

        /// <summary>
        /// Respawn requested this frame. A rising edge, LOCAL ONLY -- it never reaches the wire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not a button bit, deliberately.</b> Respawning is <c>C_SPAWN_REQUEST</c>, its own
        /// reliable message on channel 2 (protocol-spec § 4.1), not a bit in <c>C_INPUT</c>. This
        /// member exists so a source that is not a keyboard can raise the intent; what happens
        /// next is <c>NetClientLocalCombatDriver</c>'s, and it is unchanged.
        /// </para>
        /// <para>
        /// <b>Why it exists at all.</b> The driver read <c>Input.GetKeyDown</c> directly, so
        /// check 13 of phase-3-harness could reach a death and a death screen and could not
        /// reach the respawn -- defect 4 of the phase-3D report. A scripted client had no way in.
        /// </para>
        /// </remarks>
        bool RespawnPressed { get; }

        /// <summary>
        /// Enter-or-leave-a-seat requested this frame. A rising edge, LOCAL ONLY -- it never
        /// reaches the wire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not <see cref="Ironfront.Net.Protocol.InputButtons"/><c>.Use</c>, and that
        /// difference is the whole reason this member exists.</b> <c>Use</c> is a LEVEL bit in
        /// the <c>C_INPUT</c> word
        /// (<c>InputButtonPacker</c> packs it; no server code reads it). Driving a seat request
        /// off a level would send one reliable message per tick for as long as the key is held —
        /// roughly thirty round trips for one press, every one of them arbitrated. An edge sends
        /// one. This is the same argument <see cref="RespawnPressed"/> makes for
        /// <c>C_SPAWN_REQUEST</c>, and it lands the same way: seat entry is
        /// <c>C_SEAT_REQUEST</c>, its own reliable message on channel 2, not a bit in
        /// <c>C_INPUT</c>.
        /// </para>
        /// <para>
        /// <b>Enter and leave are the same edge.</b> Which one the press means is not the input
        /// source's to decide — it depends on whether this client is seated, which only
        /// <c>ClientSeatRequester</c> knows, from the server's own <c>S_SEAT_CHANGE</c>. A
        /// source that tried to answer it would be making the local decision design D2 forbids.
        /// </para>
        /// <para>
        /// <b>Why it exists at all (ledger X-30).</b> <c>SeatRequestMessage</c> had zero
        /// production senders: the server routes it, <c>ServerSeatBridge</c> waits for it, and
        /// no client could ask. A recorded lane-B programme could not express the intent either,
        /// because a programme writes <c>InputButtons</c> and this is not one — which is why
        /// checks B-7 and B-13 were blocked on a client capability rather than on programme work.
        /// </para>
        /// </remarks>
        bool SeatTogglePressed { get; }
    }
}
