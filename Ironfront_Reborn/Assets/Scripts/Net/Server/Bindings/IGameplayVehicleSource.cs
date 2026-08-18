using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// One gameplay <c>Vehicle</c>, as the replication layer needs it — without naming the
    /// game's own <c>Vehicle</c>, <c>Seat</c> or <c>Actor</c> types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="IGameplayActorSource"/> inversion, applied to vehicles and for the same
    /// reason: <c>Vehicle</c> compiles into <c>Assembly-CSharp</c>, a <em>predefined</em>
    /// assembly that Unity builds after every asmdef, so no asmdef may reference it. This
    /// assembly declares the shape; <c>IronfrontNetBindings</c> — which lives outside every
    /// asmdef and therefore sees both halves — implements it and registers the resolver.
    /// </para>
    /// <para>
    /// <b>This is a wide interface, and deliberately one interface rather than five.</b> Every
    /// member is a read or a call against the same object, and splitting it by consumer
    /// (pose / health / seats) would give the binding three adapters over one <c>Vehicle</c>,
    /// each with its own <see cref="Exists"/> check that can disagree with the other two after
    /// the GameObject is destroyed.
    /// </para>
    /// <para>
    /// <b><see cref="Exists"/> is not ceremony</b> — see <see cref="IGameplayActorSource"/>. A
    /// plain interface reference has no notion of Unity's destroyed-object equality and would
    /// stay non-null over a torn-down component, so the check has to travel with the
    /// implementation.
    /// </para>
    /// </remarks>
    public interface IGameplayVehicleSource
    {
        /// <summary>False once the underlying <c>Vehicle</c> has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>The prefab's authored <c>networkId</c>. Maps to <c>Vehicle.NetworkId</c>.</summary>
        byte NetworkTypeId { get; }

        /// <summary>Seats on this vehicle. Maps to <c>Vehicle.seats.Length</c>.</summary>
        int SeatCount { get; }

        /// <summary>Current health. Maps to <c>Vehicle.Health</c>.</summary>
        float Health { get; }

        /// <summary>Health at full. Maps to <c>Vehicle.maxHealth</c>.</summary>
        float MaxHealth { get; }

        /// <summary>
        /// How long this vehicle burns before dying, in seconds. Maps to
        /// <c>Vehicle.burnTime</c>.
        /// </summary>
        /// <remarks>
        /// <b>Read once, at registration, and never replicated.</b> The field is simultaneously
        /// the serialized designer default and the live countdown, so its value mid-burn is
        /// meaningless to anyone but the vehicle itself — which is exactly why the server owns
        /// the countdown in ticks (V4-D11) and announces only the end.
        /// </remarks>
        float BurnTimeSeconds { get; }

        /// <summary>Maps to <c>Vehicle.crashSkipsBurn</c>. A crash kills such a vehicle outright.</summary>
        bool CrashSkipsBurn { get; }

        /// <summary>Maps to <c>Vehicle.burning</c>.</summary>
        bool IsBurning { get; }

        /// <summary>Maps to <c>Vehicle.dead</c>.</summary>
        bool IsDead { get; }

        /// <summary>Maps to <c>Vehicle.ownerTeam</c>. AI bookkeeping; never an interest team.</summary>
        int OwnerTeam { get; }

        /// <summary>
        /// Reads the whole rigid-body state at one instant.
        /// </summary>
        /// <remarks>
        /// <b>From the <c>Rigidbody</c>, never the <c>Transform</c></b> (V4-D14). The transform
        /// lags the body by up to one physics substep, and a constant lag does not average out —
        /// shipping it puts a fixed interpolation error into every client for free.
        /// <paramref name="angularVelocity"/> is in rad/s, which is what Unity reports.
        /// </remarks>
        void ReadPose(
            out Vector3 position, out Quaternion rotation,
            out Vector3 linearVelocity, out Vector3 angularVelocity);

        /// <summary>Turret yaw in degrees, or 0 on a vehicle with no turret.</summary>
        float TurretYaw { get; }

        /// <summary>Turret pitch in degrees.</summary>
        float TurretPitch { get; }

        /// <summary>
        /// The two subtype-tail bytes, packed per this vehicle's kind via
        /// <c>VehicleSubtypeTail</c>.
        /// </summary>
        void ReadSubtypeTail(out byte subtypeA, out byte subtypeB);

        /// <summary>Maps to <c>WaterLevel.InWater(transform.position)</c>.</summary>
        bool IsInWater { get; }

        /// <summary>True while no wheel or hull is touching the ground.</summary>
        bool IsAirborne { get; }

        /// <summary>
        /// Overwrites health outright. Maps to <c>Vehicle.SetHealthAuthoritative</c>, which V0
        /// opened for exactly this caller.
        /// </summary>
        void SetHealthAuthoritative(float value);

        /// <summary>World position of one seat, for the arbiter's reach check.</summary>
        Vector3 GetSeatPosition(int seatIndex);

        /// <summary>
        /// Seats an actor. Maps to <c>Actor.EnterSeat(vehicle.seats[i])</c>.
        /// </summary>
        /// <returns>
        /// <b>The <c>bool</c> the three shipped call sites throw away</b>
        /// (<c>FpsActorController</c>, <c>AiActorController</c>, <c>Actor.SwitchSeat</c>) — those
        /// are the offline and AI paths and stay as they are. This one call site checks it
        /// (V4-D7): <c>EnterSeat</c> re-reads <c>seat.vehicle.dead</c> and
        /// <c>seat.IsOccupied()</c> against the live scene, and a <c>false</c> from a condition
        /// the arbiter could not see becomes a refusal rather than a silent divergence between
        /// the arbiter's record and the scene.
        /// </returns>
        bool TryEnterSeat(GameObject actor, int seatIndex);

        /// <summary>Removes an actor from whichever seat of this vehicle it is in.</summary>
        /// <returns>False when it was not in one.</returns>
        bool TryLeaveSeat(GameObject actor);
    }
}
