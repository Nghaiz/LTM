using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A replicated vehicle's scene object, as the client netcode drives it: a body to take
    /// kinematic, a transform to correct, and a wreck to make. Phase C4b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client counterpart to the server's <c>IGameplayVehicleSource</c>, and narrower than
    /// it on purpose: the server reads authority off a vehicle, the client only ever pushes at
    /// one. Nothing here returns simulation state.
    /// </para>
    /// <para>
    /// <b><see cref="Rigidbody"/> crosses the seam unwrapped</b> because it is a
    /// <c>UnityEngine</c> type, which this assembly may name freely — the wall is around
    /// <c>Assembly-CSharp</c>, not around the engine. Wrapping it would mean re-declaring
    /// position, rotation and both velocities on this interface for no gain, and the correction
    /// path writes all four every snapshot.
    /// </para>
    /// <para>
    /// <b><see cref="Exists"/> carries the liveness</b> an interface reference otherwise loses —
    /// see <c>IGameplayActorPresence.Exists</c>. It matters more here than anywhere else in the
    /// client: a despawn destroys the object while the registry is still holding it, and
    /// <c>Destroy</c> takes a frame to complete.
    /// </para>
    /// </remarks>
    public interface IGameplayVehicleBody
    {
        /// <summary>False once the underlying vehicle has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>
        /// The scene object, for destruction and as the registry's reverse-lookup key.
        /// </summary>
        /// <remarks>
        /// <b>A destroyed object is not a usable dictionary key on Unity's Mono runtime</b>, which
        /// is why the registry drops its entry BEFORE destroying — that ordering is a
        /// pre-existing decision this seam must not disturb.
        /// </remarks>
        GameObject GameObject { get; }

        /// <summary>The vehicle's transform, for pose reads that do not go through the body.</summary>
        Transform Transform { get; }

        /// <summary>The body the interpolator and the corrector write, or null without one.</summary>
        Rigidbody Rigidbody { get; }

        /// <summary>
        /// The authored network type id, matched against <c>S_VEHICLE_SPAWN</c>'s.
        /// </summary>
        /// <remarks>Maps to <c>Vehicle.NetworkId</c>. Zero means "not replicated".</remarks>
        byte NetworkId { get; }

        /// <summary>
        /// Takes the body kinematic, or gives it back to PhysX.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Vehicle.SetNetworkDriven</c>. V5-D3: a replicated vehicle whose rigidbody
        /// is still dynamic runs local PhysX <em>against</em> the incoming snapshots, and the
        /// jitter that produces looks like a network problem and is not.
        /// </remarks>
        void SetNetworkDriven(bool networkDriven);

        /// <summary>
        /// The vehicle's full health, for un-normalising the replicated fraction.
        /// </summary>
        /// <remarks>
        /// The wire carries health as a fraction of maximum, and the maximum is authored per
        /// vehicle. Multiplying happens on this side because the fraction is the netcode's and
        /// the maximum is the game's.
        /// </remarks>
        float MaxHealth { get; }

        /// <summary>
        /// Sets health without running the game's damage path.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Vehicle.SetHealthAuthoritative</c>. Distinct from taking damage on purpose:
        /// the client does not own health, and routing this through the damage path would fire
        /// death effects for a wreck the server has not declared.
        /// </remarks>
        void SetHealthAuthoritative(float value);

        /// <summary>Applies the replicated environment flags.</summary>
        /// <remarks>Maps to <c>Vehicle.ApplyReplicatedFlags</c>, which subtypes override.</remarks>
        void ApplyReplicatedFlags(bool inWater, bool airborne);

        /// <summary>
        /// Applies the two subtype bytes, whose meaning depends on the vehicle family.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Vehicle.ApplyReplicatedSubtypeTail</c>, which subtypes override — a
        /// helicopter reads them as rotor state, a tank does not read them at all. The client
        /// hands the bytes over without interpreting them, which is the point of the tail.
        /// </remarks>
        void ApplyReplicatedSubtypeTail(byte subtypeA, byte subtypeB);

        /// <summary>
        /// Wrecks the vehicle, with the explosion.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Vehicle.Die</c>. Used only for
        /// <c>VehicleDespawnReason.Destroyed</c>; a round simply ending around a vehicle
        /// destroys the object instead, because playing the explosion there would be wrong.
        /// </remarks>
        void Die();
    }
}
