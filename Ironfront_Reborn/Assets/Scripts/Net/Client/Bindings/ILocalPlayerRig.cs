using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The human at this keyboard: their input, their camera, their body. What
    /// <c>FpsActorController.instance</c> was reached for, named as a shape this assembly owns.
    /// Phase C4a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five presenters reached for that singleton, and every one of them wanted a different
    /// third of it.</b> The vehicle stage wanted the input source; the combat driver wanted the
    /// input source and the enable/disable pair; the combat presenter wanted the body, to fell
    /// it; the explosion presenter wanted the camera, to shake it; the presenter guard wanted
    /// identity. Those are the five members below, and nothing else — the interface is the union
    /// of what was actually called, measured at <c>file:line</c>, not a projection of
    /// <c>FpsActorController</c>'s public surface.
    /// </para>
    /// <para>
    /// <b>A registered instance, not a resolver.</b> The server side resolves per
    /// <c>GameObject</c> because it has many actors; there is exactly one local player, and every
    /// call site here was already a singleton read. Registration keeps the shape identical to the
    /// <c>FpsActorController.instance</c> it replaces — including that it can be absent, which is
    /// the normal state on a headless server and the reason every member below is safe to call
    /// when <see cref="Exists"/> is false.
    /// </para>
    /// <para>
    /// <b>Absent is a supported state, not an error</b>, exactly as <c>NetServerBindings</c>
    /// documents for its own seams. Nothing registered means <see cref="Exists"/> is false and
    /// every presenter takes the branch it already had for <c>instance == null</c>. That is what
    /// lets an EditMode test drive these types with no scene and no game.
    /// </para>
    /// </remarks>
    public interface ILocalPlayerRig
    {
        /// <summary>
        /// Whether a local player rig is present. False on a headless server, false before the
        /// rig spawns, and false once it is destroyed.
        /// </summary>
        /// <remarks>
        /// Maps to <c>FpsActorController.instance != null</c>, and carries the same
        /// <c>UnityEngine.Object</c> liveness semantics an interface reference otherwise loses —
        /// see <c>IGameplayActorPresence.Exists</c>.
        /// </remarks>
        bool Exists { get; }

        /// <summary>
        /// The rig's input source, or null when it has none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Maps to <c>FpsActorController.InputSource</c>. <c>IInputSource</c> lives in
        /// <c>Ironfront.Net.Unity.Input</c>, which this assembly references, so the type crosses
        /// the seam unwrapped — it was never a legacy type.
        /// </para>
        /// <para>
        /// <b>Read per frame, never cached by the caller.</b> The body is spawned, killed and
        /// respawned independently of every presenter, so a cached source goes stale exactly at a
        /// death — the one moment the respawn button matters.
        /// </para>
        /// </remarks>
        IInputSource InputSource { get; }

        /// <summary>Restores player control. Maps to <c>FpsActorController.EnableInput</c>.</summary>
        void EnableInput();

        /// <summary>Suppresses player control. Maps to <c>FpsActorController.DisableInput</c>.</summary>
        void DisableInput();

        /// <summary>
        /// Whether <paramref name="actor"/> is the body this rig drives.
        /// </summary>
        /// <remarks>
        /// Maps to <c>ReferenceEquals(instance.actor, actor)</c>. It duplicates
        /// <c>IGameplayActorPresence.IsLocalPlayerBody</c> from the other end on purpose: the
        /// presence seam answers it for an actor that is already in hand, and this answers it
        /// when only the rig is.
        /// </remarks>
        bool IsDriving(IGameplayActorPresence actor);

        /// <summary>
        /// The rig's world position, for distance falloff. <see cref="Vector3.zero"/> when
        /// absent.
        /// </summary>
        Vector3 Position { get; }

        /// <summary>
        /// Whether this rig has a first-person camera a screenshake can be applied to.
        /// </summary>
        /// <remarks>
        /// Maps to <c>FpsActorController.fpParent != null</c>. Separate from
        /// <see cref="ApplyScreenshake"/> because the caller must take its early-out <em>before</em>
        /// computing the falloff, which is the order the shipped code had.
        /// </remarks>
        bool CanApplyScreenshake { get; }

        /// <summary>
        /// Shakes the first-person camera. A no-op when
        /// <see cref="CanApplyScreenshake"/> is false.
        /// </summary>
        /// <remarks>Maps to <c>FpsActorController.fpParent.ApplyScreenshake</c>.</remarks>
        void ApplyScreenshake(float magnitude, int iterations);

        /// <summary>
        /// Whether the rig's body can be felled — it has a body, and that body has a rig.
        /// </summary>
        /// <remarks>
        /// Maps to <c>instance.actor != null &amp;&amp; instance.actor.ragdoll != null</c>.
        /// </remarks>
        bool HasFellableBody { get; }

        /// <summary>
        /// Fells the local player's own body, landing the impulse on <paramref name="bone"/>.
        /// A no-op when <see cref="HasFellableBody"/> is false.
        /// </summary>
        /// <remarks>
        /// At the client role <c>Actor.Damage</c> never reaches <c>Die()</c> — the client does not
        /// own health — so without this the local player takes hits, staggers, and stands there
        /// dead.
        /// </remarks>
        void FellBody(Vector3 force, HumanBodyBones bone);
    }
}
